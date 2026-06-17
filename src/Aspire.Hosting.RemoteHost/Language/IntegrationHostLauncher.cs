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
/// spawns the hosts, briefly waits for them to register, then drives the existing
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
    /// Per-host timeout for integration host registration. Bounds the worst case where a
    /// spawned host fails to start or hangs before calling registerAsIntegrationHost — the
    /// launcher logs a warning and proceeds with whatever did register, instead of blocking
    /// server startup forever.
    /// </summary>
    private static readonly TimeSpan s_perHostRegistrationTimeout = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var descriptors = ReadDescriptorsFromConfiguration();
            if (descriptors.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Spawning {Count} integration host(s) from server config.", descriptors.Count);
            Launch(descriptors);

            // Wait for each spawned host to call registerAsIntegrationHost. The signal is the
            // ExternalCapabilityRegistry's per-registration semaphore release; we consume
            // exactly `descriptors.Count` releases (the number of hosts we spawned), with a
            // per-host timeout cap. Returns the moment the last expected host registers — no
            // blind delay.
            var registered = await _externalCapabilityRegistry
                .WaitForHostsAsync(descriptors.Count, s_perHostRegistrationTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (registered < descriptors.Count)
            {
                _logger.LogError(
                    "Only {Registered} of {Expected} integration host(s) registered within the per-host timeout " +
                    "({TimeoutSeconds}s). Capabilities from the missing host(s) will be absent from the consumer's " +
                    "generated SDK, which typically surfaces as 'builder.<method> is not a function' at runtime. " +
                    "Look for `Spawning integration host` lines above to identify which hosts were launched, and " +
                    "for `IntegrationHost[<name>]` lines for each host's own stdout/stderr — a host that crashes " +
                    "during its own startup is the most common cause.",
                    registered, descriptors.Count, s_perHostRegistrationTimeout.TotalSeconds);
            }

            await _externalCapabilityRegistry.InitializeAllHostsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down before integration host metadata gather completed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize integration hosts at server startup.");
        }
        finally
        {
            // Always release the readiness gate so downstream handlers do not deadlock.
            _readyTcs.TrySetResult(true);
        }
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
                _logger.LogError(
                    "Malformed IntegrationHosts entry in appsettings.json: Language='{Language}', " +
                    "PackageName='{PackageName}', HostEntryPoint='{HostEntryPoint}'. Skipping. " +
                    "All three fields are required. The CLI's csproj generation step writes this " +
                    "section — if you see this, it is a bug in AppHostServerAppSettingsWriter.",
                    language, packageName, hostEntryPoint);
                continue;
            }
            if (!File.Exists(hostEntryPoint))
            {
                _logger.LogError(
                    "Integration host entry point '{HostEntryPoint}' for package '{PackageName}' [{Language}] " +
                    "does not exist on disk. The host process cannot be spawned. " +
                    "Verify the path in appsettings.json (under IntegrationHosts) is correct.",
                    hostEntryPoint, packageName, language);
                continue;
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
            var hostSpec = languageSupport?.GetIntegrationHostSpec();
            if (hostSpec is null)
            {
                _logger.LogWarning(
                    "Language '{Language}' does not provide an integration host (integration '{Name}'). " +
                    "The language support is either missing or returns null from GetIntegrationHostSpec. Skipping.",
                    descriptor.Language, descriptor.PackageName);
                continue;
            }

            var hostDir = Path.GetDirectoryName(descriptor.HostEntryPoint)!;

            // Note: dependency restore for the integration host (e.g. `npm install` for TS)
            // is run by the CLI during its restore phase — symmetric with how the CLI runs
            // `dotnet build` on the AppHost server csproj before launching it. By the time
            // we get here the host's deps are already present on disk.

            var command = hostSpec.Execute.Command;
            var argsTemplate = hostSpec.Execute.Args;

            var args = string.Join(" ", argsTemplate.Select(a => a.Replace("{entryPoint}", descriptor.HostEntryPoint)));

            // On Windows, executables shipped via npm are .cmd shims (e.g. npx.cmd) — Process.Start
            // cannot find them by bare name without shell execution. Resolve to the full path via
            // PATH lookup with PATHEXT expansion before spawning.
            var resolvedCommand = PathLookupHelper.FindFullPathFromPath(command) ?? command;
            if (!ReferenceEquals(resolvedCommand, command))
            {
                _logger.LogDebug("Resolved integration host command '{Command}' to '{ResolvedCommand}'.", command, resolvedCommand);
            }

            var psi = new ProcessStartInfo
            {
                FileName = resolvedCommand,
                Arguments = args,
                WorkingDirectory = hostDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

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
                continue;
            }

            if (hostProcess is null)
            {
                _logger.LogError(
                    "Process.Start returned null for integration host '{Name}' [{Language}] " +
                    "(command: '{Command}' args: '{Args}' cwd: '{HostDir}'). " +
                    "This usually means the OS rejected the launch — check that the executable " +
                    "is on PATH and the working directory exists.",
                    descriptor.PackageName, descriptor.Language, resolvedCommand, args, hostDir);
                continue;
            }

            _spawnedProcesses.Add(hostProcess);
            _logger.LogInformation(
                "Started integration host '{Name}' [{Language}] (PID {Pid})",
                descriptor.PackageName, descriptor.Language, hostProcess.Id);

            // Watch for unexpected early exit so users get an actionable diagnostic instead of
            // a silent missing-capability later. If the host exits during the registration
            // window with a non-zero code, log it loud — that almost always means the host's
            // own startup threw and the registerAsIntegrationHost call never happened, which
            // would otherwise just look like a slow timeout from WaitForHostsAsync.
            var packageNameForExit = descriptor.PackageName;
            var entryPointForExit = descriptor.HostEntryPoint;
            var hostProcessForExit = hostProcess;
            hostProcess.EnableRaisingEvents = true;
            hostProcess.Exited += (_, _) =>
            {
                if (hostProcessForExit.ExitCode != 0)
                {
                    _logger.LogError(
                        "Integration host '{Name}' (PID {Pid}, entry '{EntryPoint}') exited unexpectedly with code {ExitCode}. " +
                        "This usually means the host's own startup code threw before it could call registerAsIntegrationHost. " +
                        "Look for IntegrationHost[{Name}] lines above this for the host's own stdout/stderr.",
                        packageNameForExit, hostProcessForExit.Id, entryPointForExit, hostProcessForExit.ExitCode, packageNameForExit);
                }
                else
                {
                    _logger.LogDebug(
                        "Integration host '{Name}' (PID {Pid}) exited cleanly with code 0.",
                        packageNameForExit, hostProcessForExit.Id);
                }
            };

            // Pipe the host's stdout into server logs at Information, stderr at Warning.
            // Tagged IntegrationHost[Name] so the user can find the host's own diagnostics
            // when chasing a failure.
            var packageName = descriptor.PackageName;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!hostProcess.HasExited)
                    {
                        var line = await hostProcess.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                        {
                            break;
                        }
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
                    while (!hostProcess.HasExited)
                    {
                        var line = await hostProcess.StandardError.ReadLineAsync().ConfigureAwait(false);
                        if (line is null)
                        {
                            break;
                        }
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var process in _spawnedProcesses)
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
/// <see cref="Aspire.TypeSystem.ILanguageSupport.GetIntegrationHostSpec"/> to
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
