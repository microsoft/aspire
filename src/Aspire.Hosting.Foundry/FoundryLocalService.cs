// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Foundry;

internal static class FoundryLocalService
{
    internal const string ApiKey = "unused";

    private static readonly TimeSpan s_serviceStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_serviceStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim s_managerLock = new(1, 1);
    private static readonly Regex s_urlRegex = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex s_progressRegex = new(@"(?<progress>\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static Process? s_serviceProcess;
    private static string? s_daemonVerb;
    private static bool s_shouldStopService;

    public static bool IsServiceRunning => Endpoint is not null;

    public static Uri? Endpoint { get; private set; }

    public static async Task StartAsync(ILogger logger, CancellationToken cancellationToken)
    {
        await s_managerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Endpoint is not null)
            {
                return;
            }

            var daemonVerb = await GetDaemonVerbAsync(cancellationToken).ConfigureAwait(false);
            if (daemonVerb == "server")
            {
                await StartCliServerAsync(logger, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StartLegacyCliServiceAsync(logger, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            s_managerLock.Release();
        }
    }

    public static async Task<string> DownloadModelAsync(string modelName, Action<float> downloadProgress, CancellationToken cancellationToken)
    {
        var output = await RunFoundryCommandAsync(
            ["model", "download", modelName],
            line => ReportProgress(line, downloadProgress),
            cancellationToken).ConfigureAwait(false);

        if (TryParseModelId(output, out var modelId))
        {
            return modelId;
        }

        return await GetModelIdAsync(modelName, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string?> TryLoadCachedModelAsync(string modelName, CancellationToken cancellationToken)
    {
        var daemonVerb = await GetDaemonVerbAsync(cancellationToken).ConfigureAwait(false);
        if (daemonVerb != "server")
        {
            var legacyModelId = await GetModelIdAsync(modelName, cancellationToken).ConfigureAwait(false);
            var loadResult = await RunFoundryCommandCoreAsync(
                ["model", "load", legacyModelId],
                onOutput: null,
                cancellationToken).ConfigureAwait(false);
            return loadResult.ExitCode == 0 ? legacyModelId : null;
        }

        var output = await RunFoundryCommandAsync(
            ["model", "info", modelName, "--output", "json"],
            onOutput: null,
            cancellationToken).ConfigureAwait(false);

        if (!TryParseModelInfo(output, out var modelId, out var cached))
        {
            throw new InvalidOperationException($"Foundry CLI did not return model information for model '{modelName}'.");
        }

        if (!cached)
        {
            return null;
        }

        await LoadModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        return modelId;
    }

    public static async Task LoadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await RunFoundryCommandAsync(["model", "load", modelId], onOutput: null, cancellationToken).ConfigureAwait(false);
    }

    public static Task<bool> IsModelLoadedAsync(Uri endpoint, string modelId, HttpClient httpClient, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<string>>? legacyModelListProvider = null;
        if (endpoint == Endpoint && s_daemonVerb == "service")
        {
            legacyModelListProvider = static cancellationToken =>
                RunFoundryCommandAsync(["service", "ps"], onOutput: null, cancellationToken);
        }

        return IsModelLoadedCoreAsync(endpoint, modelId, httpClient, legacyModelListProvider, cancellationToken);
    }

    internal static async Task<bool> IsModelLoadedCoreAsync(
        Uri endpoint,
        string modelId,
        HttpClient httpClient,
        Func<CancellationToken, Task<string>>? legacyModelListProvider,
        CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "models/loaded", "openai/loadedmodels" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var output = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var isLoaded = TryParseModelIds(output, out var modelIds) &&
                modelIds.Contains(modelId, StringComparer.OrdinalIgnoreCase);
            if (isLoaded || path == "models/loaded")
            {
                return isLoaded;
            }
        }

        if (legacyModelListProvider is not null)
        {
            var models = await legacyModelListProvider(cancellationToken).ConfigureAwait(false);
            return models.Contains(modelId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static async Task StopAsync(CancellationToken cancellationToken)
    {
        await s_managerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var process = s_serviceProcess;
            var daemonVerb = s_daemonVerb;

            if (!s_shouldStopService && Endpoint is null && process is null)
            {
                return;
            }

            var stopTimeout = cancellationToken.IsCancellationRequested ? TimeSpan.FromSeconds(2) : s_serviceStopTimeout;
            using var stopCancellation = new CancellationTokenSource(stopTimeout);
            try
            {
                // Both CLI generations start a daemon that outlives the command used to launch it.
                // Use the matching stop command rather than relying on the tracked legacy process tree.
                var arguments = daemonVerb == "server"
                    ? new[] { "server", "stop", "--output", "json" }
                    : new[] { "service", "stop" };
                await RunFoundryCommandAsync(arguments, onOutput: null, stopCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or Win32Exception)
            {
                // Stopping the external Foundry service is best-effort. The tracked legacy process
                // is still killed below, and modern CLI failures must not fail AppHost shutdown.
            }

            if (process is not null)
            {
                KillProcess(process);
                process.Dispose();
            }

            // Clear local ownership after the best-effort stop attempt.
            s_serviceProcess = null;
            Endpoint = null;
            s_shouldStopService = false;
        }
        finally
        {
            s_managerLock.Release();
        }
    }

    private static async Task StartCliServerAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var endpointSource = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await RunFoundryCommandAsync(
                ["server", "start", "--output", "json"],
                line =>
                {
                    // The daemon writes this before the CLI parent reports its endpoint:
                    //   foundrylocald 0.10.1 starting (log-level=info)
                    // Retain cleanup ownership if endpoint parsing subsequently fails.
                    if (line.Contains("foundrylocald", StringComparison.OrdinalIgnoreCase) &&
                        line.Contains("starting", StringComparison.OrdinalIgnoreCase))
                    {
                        s_shouldStopService = true;
                    }

                    logger.LogInformation("{Output}", line);
                },
                cancellationToken,
                stopReadingAfterProcessExit: true,
                outputCompletionPredicate: line =>
                {
                    if (!TryParseServerEndpoint(line, out var endpoint))
                    {
                        return false;
                    }

                    s_shouldStopService = true;
                    endpointSource.TrySetResult(endpoint);
                    return true;
                }).ConfigureAwait(false);

            Endpoint = await endpointSource.Task.ConfigureAwait(false);
        }
        catch
        {
            if (s_shouldStopService)
            {
                using var stopCancellation = new CancellationTokenSource(s_serviceStopTimeout);
                try
                {
                    await RunFoundryCommandAsync(
                        ["server", "stop", "--output", "json"],
                        onOutput: null,
                        stopCancellation.Token).ConfigureAwait(false);
                    s_shouldStopService = false;
                }
                catch (Exception cleanupException) when (cleanupException is OperationCanceledException or InvalidOperationException or Win32Exception)
                {
                    logger.LogWarning(cleanupException, "Foundry CLI server cleanup failed after startup did not complete. Cleanup will be retried when the AppHost stops.");
                }
            }

            throw;
        }
    }

    private static async Task StartLegacyCliServiceAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var startInfo = CreateFoundryStartInfo(["service", "start"]);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var ownsProcess = true;

        try
        {
            var endpointSource = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                logger.LogInformation("{Output}", e.Data);

                if (TryParseEndpoint(e.Data, out var endpoint))
                {
                    endpointSource.TrySetResult(endpoint);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    logger.LogInformation("{Output}", e.Data);
                }
            };

            process.Exited += (_, _) =>
            {
                endpointSource.TrySetException(new InvalidOperationException($"Foundry CLI service exited before reporting an endpoint. Exit code: {process.ExitCode}."));
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Foundry CLI service process could not be started.");
            }

            using var startCancellation = new CancellationTokenSource(s_serviceStartTimeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startCancellation.Token);
            using var cancellationRegistration = linkedCancellation.Token.Register(static state =>
            {
                var (source, foundryProcess, timeoutToken) = ((TaskCompletionSource<Uri>, Process, CancellationToken))state!;
                if (timeoutToken.IsCancellationRequested)
                {
                    source.TrySetException(new TimeoutException($"Timed out waiting for Foundry CLI service to report an endpoint after {s_serviceStartTimeout}."));
                }
                else
                {
                    source.TrySetCanceled();
                }

                KillProcess(foundryProcess);
            }, (endpointSource, process, startCancellation.Token));

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Endpoint = await endpointSource.Task.ConfigureAwait(false);
            s_serviceProcess = process;
            ownsProcess = false;
        }
        finally
        {
            if (ownsProcess)
            {
                KillProcess(process);
                process.Dispose();
            }
        }
    }

    private static async Task<string> RunFoundryCommandAsync(
        string[] arguments,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        bool stopReadingAfterProcessExit = false,
        Func<string, bool>? outputCompletionPredicate = null)
    {
        var result = await RunFoundryCommandCoreAsync(
            arguments,
            onOutput,
            cancellationToken,
            stopReadingAfterProcessExit,
            outputCompletionPredicate).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Foundry CLI command '{FormatCommand(arguments)}' failed with exit code {result.ExitCode}: {result.Error}{result.Output}");
        }

        return result.Output;
    }

    private static async Task<FoundryCommandResult> RunFoundryCommandCoreAsync(
        string[] arguments,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        bool stopReadingAfterProcessExit = false,
        Func<string, bool>? outputCompletionPredicate = null)
    {
        using var process = new Process
        {
            StartInfo = CreateFoundryStartInfo(arguments)
        };

        return await RunProcessAsync(
            process,
            FormatCommand(arguments),
            onOutput,
            cancellationToken,
            stopReadingAfterProcessExit,
            outputCompletionPredicate).ConfigureAwait(false);
    }

    internal static async Task<FoundryCommandResult> RunProcessAsync(
        Process process,
        string command,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        bool stopReadingAfterProcessExit = false,
        Func<string, bool>? outputCompletionPredicate = null)
    {
        if (!process.Start())
        {
            throw new InvalidOperationException($"Foundry CLI command '{command}' could not be started.");
        }

        using var cancellationRegistration = cancellationToken.Register(static state => KillProcess((Process)state!), process);
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outputCompletionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void ProcessOutput(string line)
        {
            onOutput?.Invoke(line);
            if (outputCompletionPredicate?.Invoke(line) is true)
            {
                outputCompletionSource.TrySetResult();
            }
        }

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var outputTask = ReadOutputAsync(process.StandardOutput, ProcessOutput, cancellationToken, readCancellation.Token);
        var errorTask = ReadOutputAsync(process.StandardError, ProcessOutput, cancellationToken, readCancellation.Token);

        if (stopReadingAfterProcessExit)
        {
            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startCancellation.CancelAfter(s_serviceStartTimeout);
            using var startCancellationRegistration = startCancellation.Token.Register(static state => KillProcess((Process)state!), process);

            try
            {
                // The modern "server start" command daemonizes, and the daemon inherits the CLI's
                // redirected stream handles. Wait until the parent exits and reports its endpoint,
                // then stop draining instead of waiting forever for EOF from the daemon. If both
                // streams close first, the CLI exited without producing the required endpoint.
                await process.WaitForExitAsync(startCancellation.Token).ConfigureAwait(false);
                var readersCompletionTask = Task.WhenAll(outputTask, errorTask);
                var completedTask = await Task
                    .WhenAny(outputCompletionSource.Task, readersCompletionTask)
                    .WaitAsync(startCancellation.Token)
                    .ConfigureAwait(false);
                if (completedTask == readersCompletionTask && !outputCompletionSource.Task.IsCompleted)
                {
                    var incompleteOutput = await outputTask.ConfigureAwait(false);
                    var incompleteError = await errorTask.ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Foundry CLI command '{command}' exited before producing required output with exit code {process.ExitCode}: {incompleteError}{incompleteOutput}");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for Foundry CLI server startup after {s_serviceStartTimeout}.");
            }
            finally
            {
                readCancellation.Cancel();
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            }
        }
        else
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        return new(process.ExitCode, output, error);
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        Action<string>? onOutput,
        CancellationToken commandCancellationToken,
        CancellationToken readCancellationToken)
    {
        var output = new List<string>();

        try
        {
            while (await reader.ReadLineAsync(readCancellationToken).ConfigureAwait(false) is { } line)
            {
                output.Add(line);
                onOutput?.Invoke(line);
            }
        }
        catch (OperationCanceledException) when (!commandCancellationToken.IsCancellationRequested)
        {
        }

        return string.Join(Environment.NewLine, output);
    }

    private static ProcessStartInfo CreateFoundryStartInfo(string[] arguments)
    {
        var startInfo = new ProcessStartInfo("foundry")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string> GetModelIdAsync(string modelName, CancellationToken cancellationToken)
    {
        var daemonVerb = await GetDaemonVerbAsync(cancellationToken).ConfigureAwait(false);
        var arguments = daemonVerb == "server"
            ? new[] { "model", "info", modelName, "--output", "json" }
            : new[] { "model", "info", modelName };
        var output = await RunFoundryCommandAsync(arguments, onOutput: null, cancellationToken).ConfigureAwait(false);

        if (daemonVerb == "server" && TryParseModelInfo(output, out var modernModelId, out _))
        {
            return modernModelId;
        }

        if (TryParseModelId(output, out var modelId))
        {
            return modelId;
        }

        throw new InvalidOperationException($"Foundry CLI did not return a model ID for model '{modelName}'.");
    }

    internal static bool TryParseEndpoint(string output, out Uri endpoint)
    {
        // Foundry CLI emits service startup lines like:
        //   Service is Started on http://127.0.0.1:50920/, PID 78399!
        // and status lines like:
        //   Model management service is running on http://127.0.0.1:50920/openai/status
        var match = s_urlRegex.Match(output);
        if (!match.Success)
        {
            endpoint = null!;
            return false;
        }

        var url = match.Value.TrimEnd(',', '.', '!', ')', ']', '}', '"', '\'');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedEndpoint))
        {
            endpoint = null!;
            return false;
        }

        var builder = new UriBuilder(parsedEndpoint)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        endpoint = builder.Uri;
        return true;
    }

    internal static string DetermineDaemonVerb(string helpOutput)
    {
        // Old CLI help lists:
        //   service  Commands to start and stop the Foundry Local service
        // New CLI help lists:
        //   server   Start, stop, restart, inspect, and troubleshoot the local Foundry daemon
        if (Regex.IsMatch(helpOutput, @"(?im)^\s*(?:Server:\s*)?server(?:\s|$)"))
        {
            return "server";
        }

        if (Regex.IsMatch(helpOutput, @"(?im)^\s*service(?:\s|$)"))
        {
            return "service";
        }

        throw new InvalidOperationException("The installed Foundry CLI does not expose a 'server' or 'service' command. Update Foundry Local and ensure the 'foundry' command on PATH is the expected installation.");
    }

    internal static bool TryParseServerEndpoint(string output, out Uri endpoint)
    {
        // Current CLI JSON output:
        //   {"running":true,"webUrls":["http://127.0.0.1:55829"],"port":55829}
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("webUrls", out var webUrls) &&
                webUrls.ValueKind is JsonValueKind.Array &&
                webUrls.GetArrayLength() > 0 &&
                Uri.TryCreate(webUrls[0].GetString(), UriKind.Absolute, out var parsedEndpoint))
            {
                endpoint = EnsureTrailingSlash(parsedEndpoint);
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return TryParseEndpoint(output, out endpoint);
    }

    internal static bool TryParseModelInfo(string output, out string modelId, out bool cached)
    {
        // Current CLI JSON output:
        //   {"model":{"id":"Phi-4-mini-instruct-generic-gpu:5","cached":true,...}}
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("model", out var model) &&
                model.TryGetProperty("id", out var idProperty) &&
                idProperty.GetString() is { Length: > 0 } id &&
                model.TryGetProperty("cached", out var cachedProperty) &&
                cachedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                modelId = id;
                cached = cachedProperty.GetBoolean();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        modelId = string.Empty;
        cached = false;
        return false;
    }

    internal static bool TryParseModelIds(string output, out string[] modelIds)
    {
        // Foundry Local loaded-model endpoints return a JSON array:
        //   ["Phi-4-mini-instruct-generic-gpu:5"]
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind is JsonValueKind.Array)
            {
                modelIds = document.RootElement
                    .EnumerateArray()
                    .Where(static item => item.ValueKind is JsonValueKind.String)
                    .Select(static item => item.GetString()!)
                    .ToArray();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        modelIds = [];
        return false;
    }

    internal static bool TryParseModelId(string output, out string modelId)
    {
        // Foundry CLI emits model info as a fixed-width table:
        //   Alias                          Device     Task           File Size    License      Model ID
        //   phi-3.5-mini                   GPU        chat           2.16 GB      MIT          Phi-3.5-mini-instruct-generic-gpu:1
        using var reader = new StringReader(output);
        string? line;
        var modelIdStart = -1;
        while ((line = reader.ReadLine()) is not null)
        {
            if (modelIdStart < 0)
            {
                modelIdStart = line.IndexOf("Model ID", StringComparison.Ordinal);
                continue;
            }

            if (line.Length <= modelIdStart || line.All(c => c == '-' || char.IsWhiteSpace(c)))
            {
                continue;
            }

            var candidate = line[modelIdStart..].Trim();
            if (candidate.Length > 0)
            {
                modelId = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                return true;
            }
        }

        modelId = string.Empty;
        return false;
    }

    private static void ReportProgress(string output, Action<float> downloadProgress)
    {
        var match = s_progressRegex.Match(output);
        if (match.Success && float.TryParse(match.Groups["progress"].Value, CultureInfo.InvariantCulture, out var progress))
        {
            downloadProgress(progress);
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process can exit between HasExited and Kill.
        }
        catch (Win32Exception)
        {
            // Cleanup paths can race with process teardown or OS-level process removal.
        }
    }

    private static string FormatCommand(string[] arguments)
    {
        return $"foundry {string.Join(' ', arguments)}";
    }

    private static async Task<string> GetDaemonVerbAsync(CancellationToken cancellationToken)
    {
        if (s_daemonVerb is not null)
        {
            return s_daemonVerb;
        }

        var helpOutput = await RunFoundryCommandAsync(["--help"], onOutput: null, cancellationToken).ConfigureAwait(false);
        s_daemonVerb = DetermineDaemonVerb(helpOutput);
        return s_daemonVerb;
    }

    private static Uri EnsureTrailingSlash(Uri endpoint)
    {
        if (endpoint.AbsolutePath.EndsWith('/'))
        {
            return endpoint;
        }

        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath + "/"
        };

        return builder.Uri;
    }

    internal readonly record struct FoundryCommandResult(int ExitCode, string Output, string Error);
}

internal sealed class FoundryLocalLifecycleService : IHostedService, IAsyncDisposable
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return FoundryLocalService.StopAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return new(FoundryLocalService.StopAsync(CancellationToken.None));
    }
}
