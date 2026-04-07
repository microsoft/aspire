// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class StopCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<StopCommand> _logger;
    private readonly ICliHostEnvironment _hostEnvironment;
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
        ICliHostEnvironment hostEnvironment,
        ILogger<StopCommand> logger,
        AspireCliTelemetry telemetry,
        TimeProvider? timeProvider = null)
        : base("stop", StopCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Options.Add(s_appHostOption);
        Options.Add(s_allOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var stopAll = parseResult.GetValue(s_allOption);

        // Validate mutual exclusivity of --all and --project
        if (stopAll && passedAppHostProjectFile is not null)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.InvariantCulture, StopCommandStrings.AllAndProjectMutuallyExclusive, s_allOption.Name, s_appHostOption.Name));
            return ExitCodeConstants.FailedToFindProject;
        }

        // Handle --all: stop all running AppHosts
        if (stopAll)
        {
            return await StopAllAppHostsAsync(cancellationToken);
        }

        // In non-interactive mode, try to auto-resolve without prompting
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return await ExecuteNonInteractiveAsync(passedAppHostProjectFile, cancellationToken);
        }

        return await ExecuteInteractiveAsync(passedAppHostProjectFile, cancellationToken);
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
            return ExitCodeConstants.FailedToFindProject;
        }

        // In non-interactive mode, only consider in-scope AppHosts (under current directory)
        // to avoid accidentally stopping unrelated AppHosts
        var inScopeConnections = allConnections.Where(c => c.Connection!.IsInScope).ToArray();

        // Single in-scope AppHost: auto-select it
        if (inScopeConnections.Length == 1)
        {
            var connection = inScopeConnections[0].Connection!;
            return await StopAppHostAsync(connection, cancellationToken);
        }

        // Multiple in-scope AppHosts or none in scope: error with guidance
        _interactionService.DisplayError(string.Format(CultureInfo.InvariantCulture, StopCommandStrings.MultipleAppHostsNonInteractive, s_appHostOption.Name, s_allOption.Name));
        return ExitCodeConstants.FailedToFindProject;
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
            _interactionService.DisplayMessage(KnownEmojis.Information, result.ErrorMessage);
            return ExitCodeConstants.Success;
        }

        return await StopAppHostAsync(result.Connection!, cancellationToken);
    }

    /// <summary>
    /// Stops all running AppHosts discovered via socket scanning.
    /// </summary>
    private async Task<int> StopAllAppHostsAsync(CancellationToken cancellationToken)
    {
        var allConnections = await _connectionResolver.ResolveAllConnectionsAsync(
            SharedCommandStrings.ScanningForRunningAppHosts,
            cancellationToken);

        if (allConnections.Length == 0)
        {
            _interactionService.DisplayError(SharedCommandStrings.AppHostNotRunning);
            return ExitCodeConstants.FailedToFindProject;
        }

        _logger.LogDebug("Found {Count} running AppHost(s) to stop", allConnections.Length);

        // Stop all AppHosts in parallel
        var stopTasks = allConnections.Select(connectionResult =>
        {
            var connection = connectionResult.Connection!;
            var appHostPath = connection.AppHostInfo?.AppHostPath ?? "Unknown";
            _logger.LogDebug("Queuing stop for AppHost: {AppHostPath}", appHostPath);
            return StopAppHostAsync(connection, cancellationToken);
        }).ToArray();

        var results = await Task.WhenAll(stopTasks);
        var allStopped = results.All(exitCode => exitCode == ExitCodeConstants.Success);

        _logger.LogDebug("Stop all completed. All stopped: {AllStopped}", allStopped);

        return allStopped ? ExitCodeConstants.Success : ExitCodeConstants.FailedToDotnetRunAppHost;
    }

    /// <summary>
    /// Stops a single AppHost by sending a stop signal to its CLI process or falling back to RPC.
    /// </summary>
    private async Task<int> StopAppHostAsync(IAppHostAuxiliaryBackchannel connection, CancellationToken cancellationToken)
    {
        // Stop the selected AppHost
        var appHostPath = connection.AppHostInfo?.AppHostPath ?? "Unknown";
        // Use relative path for in-scope, full path for out-of-scope
        var displayPath = connection.IsInScope
            ? Path.GetRelativePath(ExecutionContext.WorkingDirectory.FullName, appHostPath)
            : appHostPath;
        _interactionService.DisplayMessage(KnownEmojis.Package, $"Found running AppHost: {displayPath}");
        _logger.LogDebug("Stopping AppHost: {AppHostPath}", appHostPath);

        var appHostInfo = connection.AppHostInfo;

        _interactionService.DisplayMessage(KnownEmojis.StopSign, "Sending stop signal...");

        // Get the CLI process ID - this is the process we need to kill
        // Killing the CLI process will tear down everything including the AppHost
        var cliProcessId = appHostInfo?.CliProcessId;

        if (cliProcessId is int cliPid)
        {
            _logger.LogDebug("Sending stop signal to CLI process (PID {Pid})", cliPid);
            try
            {
                SendStopSignal(cliPid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send stop signal to CLI process {Pid}", cliPid);
                _interactionService.DisplayError(StopCommandStrings.FailedToStopAppHost);
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }
        }
        else
        {
            // Fallback: Try the RPC method if we don't have CLI process ID
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

            // If RPC didn't work, try sending SIGINT to AppHost process directly
            if (!rpcSucceeded && appHostInfo?.ProcessId is int appHostPid)
            {
                _logger.LogDebug("RPC stop not available, sending SIGINT to AppHost PID {Pid}", appHostPid);
                try
                {
                    SendStopSignal(appHostPid);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send stop signal to process {Pid}", appHostPid);
                    _interactionService.DisplayError(StopCommandStrings.FailedToStopAppHost);
                    return ExitCodeConstants.FailedToDotnetRunAppHost;
                }
            }
            else if (!rpcSucceeded)
            {
                _interactionService.DisplayError(StopCommandStrings.FailedToStopAppHost);
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }
        }

        var stopped = await _interactionService.ShowStatusAsync(
            StopCommandStrings.StoppingAppHost,
            async () =>
            {
                try
                {
                    // Wait for processes to terminate
                    var manager = new RunningInstanceManager(_logger, _interactionService, _timeProvider);

                    if (appHostInfo is not null)
                    {
                        return await manager.MonitorProcessesForTerminationAsync(appHostInfo, cancellationToken).ConfigureAwait(false);
                    }

                    return true;
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
            _interactionService.DisplaySuccess(StopCommandStrings.AppHostStoppedSuccessfully);
            return ExitCodeConstants.Success;
        }
        else
        {
            _interactionService.DisplayError(StopCommandStrings.FailedToStopAppHost);
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
    }

    /// <summary>
    /// Sends a stop signal to a process to terminate it and its process tree.
    /// Uses Process.Kill(entireProcessTree: true) to ensure all child processes are terminated.
    /// </summary>
    private static void SendStopSignal(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Process doesn't exist - already terminated
        }
        catch (InvalidOperationException)
        {
            // Process has already exited
        }
        catch (Exception)
        {
            // Some other error (e.g., permission denied) - ignore
        }
    }

}
