// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Hosting.RemoteHost.Ats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Spawns and supervises integration host processes, on behalf of the AppHost server.
///
/// This is the server-side counterpart to how <c>AssemblyLoadContext</c> owns a .NET
/// integration's lifetime: the server discovers the integration, spawns the process
/// that hosts it, captures its stdio into server logs, holds its <see cref="Process"/>
/// handle, and terminates it when the server itself stops. The CLI is not involved in
/// integration host lifetime — it tells the server about npm/python/etc. integrations
/// the same way it tells the server about .NET integrations: via the appsettings.json
/// the AppHost server csproj is built and run with.
///
/// At <see cref="StartAsync"/> time the launcher reads the <c>IntegrationHosts</c> array
/// from <see cref="IConfiguration"/> (populated by the CLI's csproj generation step),
/// spawns the hosts, waits for them to register, then drives the existing
/// <see cref="ExternalCapabilityRegistry.InitializeAllHostsAsync"/> phase so the
/// projection context is populated before any guest connects.
/// </summary>
internal sealed class IntegrationHostLauncher : IHostedService
{
    private readonly LanguageSupportResolver _languageResolver;
    private readonly ExternalCapabilityRegistry _externalCapabilityRegistry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IntegrationHostLauncher> _logger;
    private readonly ConcurrentBag<Process> _spawnedProcesses = new();
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _startupFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IntegrationHostLauncher(
        LanguageSupportResolver languageResolver,
        ExternalCapabilityRegistry externalCapabilityRegistry,
        IConfiguration configuration,
        ILogger<IntegrationHostLauncher> logger)
    {
        _languageResolver = languageResolver;
        _externalCapabilityRegistry = externalCapabilityRegistry;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Awaitable that completes once <see cref="StartAsync"/> has finished spawning and
    /// initializing every integration host listed in <c>appsettings.json</c>. Anything
    /// downstream that needs the integration hosts' capabilities to be present in the
    /// registry — code generation, getCapabilities, capability dispatch — must await
    /// this first.
    /// </summary>
    public Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_readyTcs.Task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return _readyTcs.Task;
        }
        return _readyTcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Allows cold language-runtime startup while bounding unresponsive integration hosts.
    /// </summary>
    private static readonly TimeSpan s_perHostRegistrationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_perHostDiscoveryTimeout = TimeSpan.FromMinutes(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The CLI first generates the managed SDK that integration hosts themselves import.
            // Only that explicit bootstrap pass may omit configured integrations.
            if (_configuration.GetValue<bool>(KnownConfigNames.IntegrationHostBootstrap))
            {
                _logger.LogDebug("Skipping integration hosts while bootstrapping the managed SDK.");
                _readyTcs.TrySetResult(true);
                return;
            }

            var descriptors = ReadDescriptorsFromConfiguration();
            if (descriptors.Count == 0)
            {
                _readyTcs.TrySetResult(true);
                return;
            }

            _logger.LogInformation("Spawning {Count} integration host(s) from server config.", descriptors.Count);
            Launch(descriptors);

            using var initializationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var initialization = InitializeHostsAsync(
                    descriptors.Count, s_perHostRegistrationTimeout, s_perHostDiscoveryTimeout, initializationCancellation.Token);
                if (await Task.WhenAny(initialization, _startupFailure.Task).ConfigureAwait(false) == _startupFailure.Task)
                {
                    initializationCancellation.Cancel();
                    // Observe the losing task; the child's startup failure is the error reported below.
                    await initialization.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    throw await _startupFailure.Task.ConfigureAwait(false);
                }

                await initialization.ConfigureAwait(false);
                if (_startupFailure.Task.IsCompleted)
                {
                    throw await _startupFailure.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                // Cancel pending registration/discovery if a child exits before becoming ready.
                initializationCancellation.Cancel();
            }

            _readyTcs.TrySetResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _readyTcs.TrySetCanceled(cancellationToken);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize integration hosts at server startup.");
            _readyTcs.TrySetException(ex);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task InitializeHostsAsync(int expectedCount, TimeSpan registrationTimeout, TimeSpan discoveryTimeout, CancellationToken cancellationToken)
    {
        var registered = await _externalCapabilityRegistry
            .WaitForHostsAsync(expectedCount, registrationTimeout, cancellationToken).ConfigureAwait(false);
        if (registered != expectedCount)
        {
            throw new TimeoutException(
                $"Only {registered} of {expectedCount} integration hosts registered within the per-host timeout ({registrationTimeout}). " +
                "SDK generation was stopped because required integrations are unavailable. Check the integration host startup logs.");
        }

        await _externalCapabilityRegistry.InitializeAllHostsAsync(discoveryTimeout, cancellationToken).ConfigureAwait(false);
    }

    private List<IntegrationHostDescriptor> ReadDescriptorsFromConfiguration()
    {
        var section = _configuration.GetSection("IntegrationHosts");
        var result = new List<IntegrationHostDescriptor>();
        foreach (var entry in section.GetChildren())
        {
            var language = entry["Language"];
            var packageName = entry["PackageName"];
            var hostEntryPoint = entry["HostEntryPoint"];
            if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(hostEntryPoint))
            {
                throw new InvalidOperationException(
                    $"Malformed IntegrationHosts entry: Language='{language}', PackageName='{packageName}', HostEntryPoint='{hostEntryPoint}'. " +
                    "All three fields are required.");
            }
            if (!File.Exists(hostEntryPoint))
            {
                throw new FileNotFoundException(
                    $"Integration host entry point for package '{packageName}' [{language}] does not exist. Verify the integration path.",
                    hostEntryPoint);
            }
            result.Add(new IntegrationHostDescriptor
            {
                Language = language,
                PackageName = packageName,
                HostEntryPoint = hostEntryPoint,
            });
        }
        return result;
    }

    /// <summary>
    /// Launches an integration host process for each descriptor. Each host inherits
    /// <c>REMOTE_APP_HOST_SOCKET_PATH</c> and <c>ASPIRE_REMOTE_APPHOST_TOKEN</c> via env
    /// and is expected to connect back, authenticate, and call
    /// <c>registerAsIntegrationHost</c> on its own JSON-RPC connection.
    /// </summary>
    private void Launch(IReadOnlyList<IntegrationHostDescriptor> descriptors)
    {
        if (descriptors.Count == 0)
        {
            return;
        }

        var socketPath = _configuration["REMOTE_APP_HOST_SOCKET_PATH"];
        if (string.IsNullOrEmpty(socketPath))
        {
            throw new InvalidOperationException(
                "REMOTE_APP_HOST_SOCKET_PATH is not set on the server; cannot spawn integration hosts.");
        }

        var token = _configuration[KnownConfigNames.RemoteAppHostToken];

        foreach (var descriptor in descriptors)
        {
            var languageSupport = _languageResolver.GetLanguageSupport(descriptor.Language);
            var hostSpec = languageSupport is null ? null : LanguageService.GetIntegrationHostSpec(languageSupport);
            if (hostSpec is null)
            {
                throw new InvalidOperationException(
                    $"Language '{descriptor.Language}' does not provide an integration host for '{descriptor.PackageName}'.");
            }

            // Note: dependency restore for the integration host (e.g. `npm install` for TS)
            // is run by the CLI during its restore phase — symmetric with how the CLI runs
            // `dotnet build` on the AppHost server csproj before launching it. By the time
            // we get here the host's deps are already present on disk.

            var command = hostSpec.Execute.Command;

            // On Windows, executables shipped via npm are .cmd shims (e.g. npx.cmd) — Process.Start
            // cannot find them by bare name without shell execution. Resolve to the full path via
            // PATH lookup with PATHEXT expansion before spawning.
            var resolvedCommand = PathLookupHelper.FindFullPathFromPath(command) ?? command;
            if (!ReferenceEquals(resolvedCommand, command))
            {
                _logger.LogDebug("Resolved integration host command '{Command}' to '{ResolvedCommand}'.", command, resolvedCommand);
            }

            var psi = CreateProcessStartInfo(resolvedCommand, hostSpec.Execute.Args, descriptor.HostEntryPoint, OperatingSystem.IsWindows());
            var hostDir = psi.WorkingDirectory;
            var args = psi.ArgumentList.Count > 0 ? string.Join(" ", psi.ArgumentList) : psi.Arguments;

            psi.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath;
            if (!string.IsNullOrEmpty(token))
            {
                psi.Environment[KnownConfigNames.RemoteAppHostToken] = token;
            }

            _logger.LogInformation(
                "Spawning integration host '{Name}' [{Language}]: {Command} {Args} (cwd: {HostDir})",
                descriptor.PackageName, descriptor.Language, command, args, hostDir);

            Process? hostProcess;
            try
            {
                hostProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to spawn integration host '{Name}' [{Language}]. " +
                    "Tried command '{Command}' with args '{Args}' in '{HostDir}'. " +
                    "Verify the host runtime is installed (e.g. node/npm on PATH for typescript/nodejs) " +
                    "and that '{HostEntryPoint}' exists. " +
                    "Did you run `aspire restore` after installing the integration?",
                    descriptor.PackageName, descriptor.Language, resolvedCommand, args, hostDir, descriptor.HostEntryPoint);
                throw;
            }

            if (hostProcess is null)
            {
                throw new InvalidOperationException(
                    $"Could not start integration host '{descriptor.PackageName}' [{descriptor.Language}] using '{resolvedCommand}'.");
            }

            _spawnedProcesses.Add(hostProcess);
            _logger.LogInformation(
                "Started integration host '{Name}' [{Language}] (PID {Pid})",
                descriptor.PackageName, descriptor.Language, hostProcess.Id);

            // Watch for unexpected early exit so users get an actionable diagnostic instead of
            // a silent missing-capability later. If the host exits during the registration
            // window (even with exit code zero), fail startup — the host's
            // registerAsIntegrationHost call may never have happened, which
            // would otherwise just look like a slow timeout from WaitForHostsAsync.
            var processId = hostProcess.Id;
            hostProcess.Exited += (_, _) => LogProcessExit(hostProcess, processId, descriptor.PackageName, descriptor.HostEntryPoint);
            hostProcess.EnableRaisingEvents = true;

            // Pipe the host's stdout into server logs at Information, stderr at Warning.
            // Tagged IntegrationHost[Name] so the user can find the host's own diagnostics
            // when chasing a failure.
            var packageName = descriptor.PackageName;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await hostProcess.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        _logger.LogInformation("IntegrationHost[{Name}]: {Line}", packageName, line);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "stdout reader for integration host '{Name}' stopped.", packageName);
                }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await hostProcess.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        _logger.LogWarning("IntegrationHost[{Name}]: {Line}", packageName, line);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "stderr reader for integration host '{Name}' stopped.", packageName);
                }
            });
        }
    }

    internal void LogProcessExit(Process process, int processId, string packageName, string entryPoint)
    {
        try
        {
            var exitCode = process.ExitCode;
            if (!_readyTcs.Task.IsCompleted)
            {
                _startupFailure.TrySetResult(new InvalidOperationException(
                    $"Integration host '{packageName}' (PID {processId}, entry '{entryPoint}') exited with code {exitCode} during startup. " +
                    "Check its stdout/stderr for the startup failure."));
            }

            if (exitCode != 0)
            {
                _logger.LogError(
                    "Integration host '{Name}' (PID {Pid}, entry '{EntryPoint}') exited unexpectedly with code {ExitCode}. " +
                    "This usually means the host's own startup code threw before it could call registerAsIntegrationHost. " +
                    "Look for IntegrationHost[{Name}] lines above this for the host's own stdout/stderr.",
                    packageName, processId, entryPoint, exitCode, packageName);
            }
            else
            {
                _logger.LogDebug(
                    "Integration host '{Name}' (PID {Pid}) exited cleanly with code 0.",
                    packageName, processId);
            }
        }
        catch (InvalidOperationException ex)
        {
            // StopAsync can dispose the process after its Exited event has been queued.
            // An exception escaping that ThreadPool callback would terminate the server.
            _logger.LogDebug(ex, "Exit observer for integration host '{Name}' (PID {Pid}) stopped.", packageName, processId);
        }
    }

    /// <summary>
    /// Creates launch settings preserving the argument boundaries declared by the language provider.
    /// </summary>
    internal static ProcessStartInfo CreateProcessStartInfo(string command, IReadOnlyList<string> argumentTemplates, string entryPoint, bool isWindows)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(entryPoint)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        ProcessStartInfoHelper.SetCommand(
            startInfo,
            command,
            argumentTemplates.Select(argument => argument.Replace("{entryPoint}", entryPoint)),
            isWindows);

        return startInfo;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        while (_spawnedProcesses.TryTake(out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.LogDebug("Terminating integration host process (PID {Pid}).", process.Id);
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error terminating integration host process (PID {Pid}).", process.Id);
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Minimal per-integration descriptor the AppHost server reads from its own
/// <c>appsettings.json</c> (under the <c>IntegrationHosts</c> section). The CLI's
/// csproj generation step writes this section from the integrations it parsed
/// from <c>aspire.config.json</c>. The server resolves <see cref="Language"/> to
/// an <see cref="Aspire.TypeSystem.ILanguageSupport"/> at launch time and calls
/// <see cref="LanguageService.GetIntegrationHostSpec"/> to
/// discover how to spawn the host.
/// </summary>
internal sealed class IntegrationHostDescriptor
{
    /// <summary>The language the host runtime targets (e.g. <c>"typescript/nodejs"</c>).</summary>
    public required string Language { get; init; }

    /// <summary>The package name, used for logging and diagnostics.</summary>
    public required string PackageName { get; init; }

    /// <summary>Absolute path to the host entry point file (e.g. the integration's <c>host.ts</c>).</summary>
    public required string HostEntryPoint { get; init; }
}
