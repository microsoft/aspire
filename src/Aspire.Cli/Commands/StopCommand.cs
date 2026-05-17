// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Layout;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class StopCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<StopCommand> _logger;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ILayoutDiscovery _layoutDiscovery;
    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly TimeProvider _timeProvider;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", StopCommandStrings.ProjectArgumentDescription);

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = StopCommandStrings.AllOptionDescription
    };

    public StopCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IProjectLocator projectLocator,
        ICliHostEnvironment hostEnvironment,
        ILayoutDiscovery layoutDiscovery,
        LayoutProcessRunner layoutProcessRunner,
        ILogger<StopCommand> logger,
        AspireCliTelemetry telemetry,
        ProfilingTelemetry profilingTelemetry,
        TimeProvider? timeProvider = null)
        : base("stop", StopCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, executionContext, logger, profilingTelemetry);
        _hostEnvironment = hostEnvironment;
        _layoutDiscovery = layoutDiscovery;
        _layoutProcessRunner = layoutProcessRunner;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Options.Add(s_appHostOption);
        Options.Add(s_allOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var stopAll = parseResult.GetValue(s_allOption);
        using var activity = _profilingTelemetry.StartStopCommand(stopAll, passedAppHostProjectFile is not null);

        // Validate mutual exclusivity of --all and --project
        if (stopAll && passedAppHostProjectFile is not null)
        {
            return CommandResult.Failure(CompleteStopActivity(activity, CliExitCodes.FailedToFindProject), string.Format(CultureInfo.InvariantCulture, StopCommandStrings.AllAndProjectMutuallyExclusive, s_allOption.Name, s_appHostOption.Name));
        }

        // Handle --all: stop all running AppHosts
        if (stopAll)
        {
            return CommandResult.FromExitCode(CompleteStopActivity(activity, await StopAllAppHostsAsync(cancellationToken)));
        }

        // In non-interactive mode, try to auto-resolve without prompting
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return CommandResult.FromExitCode(CompleteStopActivity(activity, await ExecuteNonInteractiveAsync(passedAppHostProjectFile, cancellationToken)));
        }

        return CommandResult.FromExitCode(CompleteStopActivity(activity, await ExecuteInteractiveAsync(passedAppHostProjectFile, cancellationToken)));
    }

    /// <summary>
    /// Handles the stop command in non-interactive mode by auto-resolving a single AppHost
    /// or returning an error when multiple AppHosts are running.
    /// </summary>
    private async Task<int> ExecuteNonInteractiveAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        // If --project is specified, use the standard resolver (no prompting needed)
        if (passedAppHostProjectFile is not null)
        {
            return await ExecuteInteractiveAsync(passedAppHostProjectFile, cancellationToken);
        }

        // Scan for all running AppHosts
        var allConnections = await _connectionResolver.ResolveAllConnectionsAsync(
            SharedCommandStrings.ScanningForRunningAppHosts,
            cancellationToken);

        if (allConnections.Length == 0)
        {
            _interactionService.DisplayError(SharedCommandStrings.AppHostNotRunning);
            return CliExitCodes.FailedToFindProject;
        }

        // In non-interactive mode, only consider in-scope AppHosts (under current directory)
        // to avoid accidentally stopping unrelated AppHosts
        var inScopeConnections = allConnections.Where(c => c.Connection!.IsInScope).ToArray();

        // Single in-scope AppHost: auto-select it
        if (inScopeConnections.Length == 1)
        {
            var connection = inScopeConnections[0].Connection!;
            _profilingTelemetry.CurrentActivity.SetAppHostStopCount(1);
            return await StopAppHostAsync(connection, GetSingleAppHostDisplayPath(connection), cancellationToken);
        }

        // Multiple in-scope AppHosts or none in scope: error with guidance
        _interactionService.DisplayError(string.Format(CultureInfo.InvariantCulture, StopCommandStrings.MultipleAppHostsNonInteractive, s_appHostOption.Name, s_allOption.Name));
        return CliExitCodes.FailedToFindProject;
    }

    /// <summary>
    /// Handles the stop command in interactive mode, prompting the user to select an AppHost if multiple are running.
    /// </summary>
    private async Task<int> ExecuteInteractiveAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, StopCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return AppHostConnectionResultHandler.DisplayFailureAsInformation(result, _interactionService);
        }

        _profilingTelemetry.CurrentActivity.SetAppHostStopCount(1);
        return await StopAppHostAsync(result.Connection!, GetSingleAppHostDisplayPath(result.Connection!), cancellationToken);
    }

    /// <summary>
    /// Stops all running AppHosts discovered via socket scanning.
    /// </summary>
    private async Task<int> StopAllAppHostsAsync(CancellationToken cancellationToken)
    {
        var allConnections = await _connectionResolver.ResolveAllConnectionsAsync(
            SharedCommandStrings.ScanningForRunningAppHosts,
            cancellationToken);
        _profilingTelemetry.CurrentActivity.SetAppHostStopCount(allConnections.Length);

        if (allConnections.Length == 0)
        {
            _interactionService.DisplayError(SharedCommandStrings.AppHostNotRunning);
            return CliExitCodes.FailedToFindProject;
        }

        _logger.LogDebug("Found {Count} running AppHost(s) to stop", allConnections.Length);

        var connections = allConnections.Select(connectionResult => connectionResult.Connection!).ToArray();
        var appHostPaths = connections.Select(GetAppHostPath).ToArray();
        var appHostPathComparer = GetAppHostPathComparer();
        var displayPaths = FileSystemHelper.ShortenPaths(appHostPaths);
        var appHostPathCounts = appHostPaths
            .GroupBy(path => path, appHostPathComparer)
            .ToDictionary(group => group.Key, group => group.Count(), appHostPathComparer);

        // Stop all AppHosts in parallel
        var stopTasks = connections.Select(connection =>
        {
            var appHostPath = GetAppHostPath(connection);
            var displayPath = displayPaths[appHostPath];
            var appHostIdentifier = GetAppHostIdentifier(connection, displayPath, appHostPathCounts[appHostPath] > 1);
            _logger.LogDebug("Queuing stop for AppHost: {AppHostPath}", appHostPath);
            return StopAppHostAsync(connection, appHostIdentifier, cancellationToken);
        }).ToArray();

        var results = await Task.WhenAll(stopTasks);
        var allStopped = results.All(exitCode => exitCode == CliExitCodes.Success);

        _logger.LogDebug("Stop all completed. All stopped: {AllStopped}", allStopped);

        return allStopped ? CliExitCodes.Success : CliExitCodes.FailedToDotnetRunAppHost;
    }

    /// <summary>
    /// Stops a single AppHost by sending a stop signal to its CLI process or falling back to RPC.
    /// </summary>
    private async Task<int> StopAppHostAsync(IAppHostAuxiliaryBackchannel connection, string appHostIdentifier, CancellationToken cancellationToken)
    {
        // Stop the selected AppHost
        var appHostPath = connection.AppHostInfo?.AppHostPath ?? "Unknown";
        var appHostInfo = connection.AppHostInfo;
        using var activity = _profilingTelemetry.StartStopAppHost(appHostInfo);
        _interactionService.DisplayMessage(KnownEmojis.Package, string.Format(CultureInfo.CurrentCulture, StopCommandStrings.FoundRunningAppHost, appHostIdentifier));
        _logger.LogDebug("Stopping AppHost: {AppHostPath}", appHostPath);

        _interactionService.DisplayMessage(KnownEmojis.StopSign, string.Format(CultureInfo.CurrentCulture, StopCommandStrings.SendingStopSignal, appHostIdentifier));

        if (appHostInfo?.CliProcessId is int cliPid)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    _logger.LogDebug("Stopping AppHost process tree via DCP (root CLI PID {Pid})", cliPid);
                    // CliStartedAt is recorded with second-level precision, so validate it locally with tolerance
                    // instead of passing it to DCP's millisecond-precision process-start-time option.
                    if (!await TryStopProcessTreeWithDcpAsync(cliPid, appHostInfo.CliStartedAt, includeStartTime: false, cancellationToken).ConfigureAwait(false))
                    {
                        ForceKillProcess(appHostInfo.ProcessId, appHostInfo.StartedAt);
                    }
                }
                else
                {
                    _logger.LogDebug("Sending stop signal to CLI process (PID {Pid})", cliPid);
                    SendStopSignal(cliPid, appHostInfo?.CliStartedAt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send stop signal to CLI process {Pid}. Will attempt force-kill.", cliPid);
            }
        }
        else
        {
            // Fallback: Try the RPC method if we don't have CLI process ID.
            _logger.LogDebug("No CLI process ID available, trying RPC stop");
            var rpcSucceeded = false;
            try
            {
                rpcSucceeded = await connection.StopAppHostAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send stop signal via RPC");
            }

            // If RPC didn't work, try sending a stop signal to the AppHost process directly.
            if (!rpcSucceeded && appHostInfo?.ProcessId is int appHostPid)
            {
                _logger.LogDebug("RPC stop not available, sending stop signal to AppHost PID {Pid}", appHostPid);
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        if (!await TryStopProcessTreeWithDcpAsync(appHostPid, appHostInfo.StartedAt, includeStartTime: true, cancellationToken).ConfigureAwait(false))
                        {
                            ForceKillProcess(appHostPid, appHostInfo.StartedAt);
                        }
                    }
                    else
                    {
                        SendStopSignal(appHostPid, appHostInfo.StartedAt);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send stop signal to process {Pid}. Will attempt force-kill.", appHostPid);
                }
            }
            else if (!rpcSucceeded)
            {
                _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, StopCommandStrings.FailedToStopAppHost, appHostIdentifier));
                return CompleteStopActivity(activity, CliExitCodes.FailedToDotnetRunAppHost);
            }
        }

        var manager = new RunningInstanceManager(_logger, _interactionService, _timeProvider);
        var stopped = await _interactionService.ShowStatusAsync(
            string.Format(CultureInfo.CurrentCulture, StopCommandStrings.StoppingAppHost, appHostIdentifier),
            async () =>
            {
                try
                {
                    if (appHostInfo is null)
                    {
                        return true;
                    }

                    if (await manager.MonitorProcessesForTerminationAsync(appHostInfo, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    var procsToKill = new HashSet<(int, DateTimeOffset?)> { (appHostInfo.ProcessId, appHostInfo.StartedAt) };

                    if (appHostInfo.CliProcessId is int cliPid)
                    {
                        procsToKill.Add((cliPid, appHostInfo.CliStartedAt));
                    }

                    foreach (var (pid, startTime) in procsToKill)
                    {
                        _logger.LogWarning("AppHost did not stop gracefully within timeout. Forcing process {Pid} to terminate.", pid);
                        ForceKillProcess(pid, startTime);
                    }

                    return await manager.MonitorProcessesForTerminationAsync(appHostInfo, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed while waiting for AppHost to stop");
                    return false;
                }
            });

        // Reset cursor position after spinner
        _interactionService.DisplayPlainText("");

        if (stopped)
        {
            _interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostStoppedSuccessfully, appHostIdentifier));
            return CompleteStopActivity(activity, CliExitCodes.Success);
        }
        else
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, StopCommandStrings.FailedToStopAppHost, appHostIdentifier));
            return CompleteStopActivity(activity, CliExitCodes.FailedToDotnetRunAppHost);
        }
    }

    private static int CompleteStopActivity(ProfilingTelemetry.ActivityScope activity, int exitCode)
    {
        activity.SetProcessExitCode(exitCode);
        if (exitCode != CliExitCodes.Success)
        {
            activity.SetError($"Stop exited with code {exitCode}.");
        }

        return exitCode;
    }

    private string GetSingleAppHostDisplayPath(IAppHostAuxiliaryBackchannel connection)
    {
        if (string.IsNullOrEmpty(connection.AppHostInfo?.AppHostPath))
        {
            return "Unknown";
        }

        var appHostPath = connection.AppHostInfo.AppHostPath;
        return connection.IsInScope
            ? Path.GetRelativePath(ExecutionContext.WorkingDirectory.FullName, appHostPath)
            : appHostPath;
    }

    private static string GetAppHostPath(IAppHostAuxiliaryBackchannel connection)
    {
        return string.IsNullOrEmpty(connection.AppHostInfo?.AppHostPath)
            ? "Unknown"
            : connection.AppHostInfo.AppHostPath;
    }

    private static StringComparer GetAppHostPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static string GetAppHostIdentifier(IAppHostAuxiliaryBackchannel connection, string displayPath, bool includeProcessId)
    {
        return includeProcessId && connection.AppHostInfo is { } appHostInfo
            ? string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostIdentifierWithProcessId, displayPath, appHostInfo.ProcessId)
            : displayPath;
    }

    /// <summary>
    /// Sends a best-effort graceful shutdown signal to the target process.
    /// Uses SIGTERM on non-Windows.
    /// </summary>
    private void SendStopSignal(int pid, DateTimeOffset? startTime)
    {
        ProcessSignaler.RequestGracefulShutdown(pid, startTime, _logger);
    }

    private async Task<bool> TryStopProcessTreeWithDcpAsync(int pid, DateTimeOffset? startTime, bool includeStartTime, CancellationToken cancellationToken)
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, startTime, _logger);
        if (process is null)
        {
            return true;
        }

        var dcpDirectory = _layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, ExecutionContext.WorkingDirectory.FullName);
        if (dcpDirectory is null)
        {
            _logger.LogWarning("Could not find DCP in the Aspire layout.");
            return false;
        }

        var dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
        if (!File.Exists(dcpPath))
        {
            _logger.LogWarning("Could not find DCP executable at '{DcpPath}'.", dcpPath);
            return false;
        }

        // Ensure we only stop the target process and not all children to allow DCP to avoid accidentally killing the child DCP instance
        var arguments = new List<string>
        {
            "stop-process-tree",
            "--skip-descendants",
            "--pid",
            pid.ToString(CultureInfo.InvariantCulture)
        };

        if (includeStartTime && startTime is not null)
        {
            arguments.Add("--process-start-time");
            arguments.Add(FormatDcpProcessStartTime(startTime.Value));
        }

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            dcpPath,
            arguments,
            workingDirectory: ExecutionContext.WorkingDirectory.FullName,
            ct: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(output))
        {
            _logger.LogDebug("DCP stop-process-tree stdout: {Output}", output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogDebug("DCP stop-process-tree stderr: {Error}", error.Trim());
        }

        if (exitCode != 0)
        {
            _logger.LogWarning("DCP stop-process-tree exited with code {ExitCode}.", exitCode);
            return false;
        }

        return true;
    }

    private static string FormatDcpProcessStartTime(DateTimeOffset startTime)
    {
        return startTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Forcefully kills the target process after the graceful shutdown timeout elapses.
    /// This does not terminate the entire process tree.
    /// </summary>
    private void ForceKillProcess(int pid, DateTimeOffset? startTime)
    {
        ProcessSignaler.ForceKill(pid, startTime, _logger);
    }

}
