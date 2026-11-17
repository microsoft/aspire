// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Resolves and starts contained integration processes without managing recovery.
/// </summary>
internal sealed class IntegrationHostProcessLauncher(
    LanguageSupportResolver languageResolver,
    IntegrationHostConfiguration configuration,
    RemoteHostProfilingTelemetry profilingTelemetry,
    ILogger<IntegrationHostLauncher> logger,
    IChildProcessFactory processFactory,
    TimeProvider timeProvider)
{
    public async Task<ProcessScope> LaunchAsync(IntegrationHostDescriptor descriptor, string registrationId, CancellationToken cancellationToken)
    {
        using var activity = profilingTelemetry.StartIntegrationHostPhase("launch", descriptor.PackageName, descriptor.Language);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(configuration.SocketPath))
            {
                throw new InvalidOperationException("REMOTE_APP_HOST_SOCKET_PATH is not set on the server; cannot spawn integration hosts.");
            }

            var languageSupport = languageResolver.GetLanguageSupport(descriptor.Language);
            var hostSpec = languageSupport is null ? null : LanguageService.GetIntegrationHostSpec(languageSupport);
            if (hostSpec is null)
            {
                throw new InvalidOperationException(
                    $"Language '{descriptor.Language}' does not provide an integration host for '{descriptor.PackageName}'.");
            }
            // Restore is a CLI operation, never a side effect of process recovery.
            if (!CommandPathResolver.TryResolveCommand(hostSpec.Execute.Command, out var command, out var error))
            {
                throw new InvalidOperationException($"Cannot launch integration host '{descriptor.PackageName}' [{descriptor.Language}]: {error}");
            }

            var startInfo = CreateProcessStartInfo(command!, hostSpec.Execute.Args, descriptor.HostEntryPoint, OperatingSystem.IsWindows());
            startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = configuration.SocketPath;
            startInfo.Environment[IntegrationHostLauncher.RegistrationIdVariable] = registrationId;
            if (!string.IsNullOrEmpty(configuration.Token))
            {
                startInfo.Environment[KnownConfigNames.RemoteAppHostToken] = configuration.Token;
            }
            logger.LogInformation(
                "Launching integration host '{Name}' [{Language}]: {Command} (entry: {EntryPoint}, cwd: {Directory}).",
                descriptor.PackageName, descriptor.Language, command, descriptor.HostEntryPoint, startInfo.WorkingDirectory);
            var execution = processFactory.Create(startInfo, logger, new ChildProcessOptions
            {
                Lifetime = ChildProcessLifetime.OwnedTree,
                TimeProvider = timeProvider,
                TerminationTimeout = configuration.ShutdownTimeout,
                OutputDrainIdleTimeout = configuration.OutputDrainIdleTimeout,
                StandardOutputCallback = line => logger.LogInformation("IntegrationHost[{Name}]: {Line}", descriptor.PackageName, line),
                StandardErrorCallback = line => logger.LogWarning("IntegrationHost[{Name}]: {Line}", descriptor.PackageName, line)
            }, OperatingSystem.IsWindows());
            try
            {
                if (!await execution.StartAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"Could not start integration host '{descriptor.PackageName}'.");
                }
                var process = new ProcessScope(execution, logger, descriptor.PackageName);
                activity.SetIntegrationHostProcessId(process.ProcessId);
                logger.LogInformation("Started integration host supervisor '{Name}' (PID {Pid}).", descriptor.PackageName, process.ProcessId);

                return process;
            }
            catch (Exception launchError)
            {
                try
                {
                    await execution.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Integration host launch and cleanup both failed.", launchError, cleanupError);
                }
                throw;
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            activity.SetError(ex);
            logger.LogDebug("Launch canceled for integration host '{Name}' [{Language}].",
                descriptor.PackageName, descriptor.Language);
            throw;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            logger.LogError(ex, "Failed to launch integration host '{Name}' [{Language}].",
                descriptor.PackageName, descriptor.Language);
            throw;
        }
    }

    internal static ProcessStartInfo CreateProcessStartInfo(string command, IReadOnlyList<string> argumentTemplates, string entryPoint, bool isWindows)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(entryPoint)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ProcessStartInfoHelper.SetCommand(
            startInfo, command,
            argumentTemplates.Select(argument => argument.Replace("{entryPoint}", entryPoint)),
            isWindows);

        return startInfo;
    }
}
