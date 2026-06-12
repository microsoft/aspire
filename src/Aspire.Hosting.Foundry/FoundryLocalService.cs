// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    public static bool IsServiceRunning => Endpoint is not null && s_serviceProcess is { HasExited: false };

    public static Uri? Endpoint { get; private set; }

    public static async Task StartAsync(ILogger logger, CancellationToken cancellationToken)
    {
        await s_managerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (s_serviceProcess is { HasExited: false })
            {
                return;
            }

            await StartCliServiceAsync(logger, cancellationToken).ConfigureAwait(false);
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

    public static async Task LoadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        await RunFoundryCommandAsync(["model", "load", modelId], onOutput: null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> IsModelLoadedAsync(string modelId, CancellationToken cancellationToken)
    {
        if (!IsServiceRunning)
        {
            return false;
        }

        var result = await RunFoundryCommandAsync(["service", "ps"], onOutput: null, cancellationToken).ConfigureAwait(false);

        return result.Contains(modelId, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? process;

        await s_managerLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            process = s_serviceProcess;
            s_serviceProcess = null;
            Endpoint = null;
        }
        finally
        {
            s_managerLock.Release();
        }

        if (process is null)
        {
            return;
        }

        var stopTimeout = cancellationToken.IsCancellationRequested ? TimeSpan.FromSeconds(2) : s_serviceStopTimeout;
        using var stopCancellation = new CancellationTokenSource(stopTimeout);
        try
        {
            // The Foundry CLI starts the inference agent as a service process, which can outlive
            // the foreground "foundry service start" process if we only kill the process tree.
            await RunFoundryCommandAsync(["service", "stop"], onOutput: null, stopCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or Win32Exception)
        {
            // Stopping the external Foundry service is best-effort. The tracked foreground
            // process is still killed and disposed below even if the CLI stop command fails.
        }

        KillProcess(process);

        process.Dispose();
    }

    private static async Task StartCliServiceAsync(ILogger logger, CancellationToken cancellationToken)
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

    private static async Task<string> RunFoundryCommandAsync(string[] arguments, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateFoundryStartInfo(arguments)
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Foundry CLI command '{FormatCommand(arguments)}' could not be started.");
        }

        using var cancellationRegistration = cancellationToken.Register(static state => KillProcess((Process)state!), process);

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var outputTask = ReadOutputAsync(process.StandardOutput, onOutput, cancellationToken);
        var errorTask = ReadOutputAsync(process.StandardError, onOutput, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Foundry CLI command '{FormatCommand(arguments)}' failed with exit code {process.ExitCode}: {error}{output}");
        }

        return output;
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var output = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            output.Add(line);
            onOutput?.Invoke(line);
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
        var output = await RunFoundryCommandAsync(["model", "info", modelName], onOutput: null, cancellationToken).ConfigureAwait(false);
        if (TryParseModelId(output, out var modelId))
        {
            return modelId;
        }

        throw new InvalidOperationException($"Foundry CLI did not return a model ID for model '{modelName}'.");
    }

    private static bool TryParseEndpoint(string output, out Uri endpoint)
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

        var url = match.Value.TrimEnd(',', '.', '!');
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
