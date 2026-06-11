// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Processes;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class StopCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<StopCommand> _logger;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ProcessShutdownService _processShutdownService;
    private readonly ProfilingTelemetry _profilingTelemetry;

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", StopCommandStrings.ProjectArgumentDescription);

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = StopCommandStrings.AllOptionDescription
    };

    public StopCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IProjectLocator projectLocator,
        ICliHostEnvironment hostEnvironment,
        ProcessShutdownService processShutdownService,
        ILogger<StopCommand> logger,
        ProfilingTelemetry profilingTelemetry,
        CommonCommandServices services)
        : base("stop", StopCommandStrings.Description, services)
    {
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, services.ExecutionContext, logger, profilingTelemetry);
        _hostEnvironment = hostEnvironment;
        _processShutdownService = processShutdownService;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;

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
            InteractionService.DisplayError(SharedCommandStrings.AppHostNotRunning);
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
        InteractionService.DisplayError(string.Format(CultureInfo.InvariantCulture, StopCommandStrings.MultipleAppHostsNonInteractive, s_appHostOption.Name, s_allOption.Name));
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
            return AppHostConnectionResultHandler.DisplayFailureAsInformation(result, InteractionService);
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
            InteractionService.DisplayMessage(KnownEmojis.Information, SharedCommandStrings.AppHostNotRunning);
            return CliExitCodes.Success;
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
        InteractionService.DisplayMessage(KnownEmojis.Package, string.Format(CultureInfo.CurrentCulture, StopCommandStrings.FoundRunningAppHost, appHostIdentifier));
        _logger.LogDebug("Stopping AppHost: {AppHostPath}", appHostPath);

        InteractionService.DisplayMessage(KnownEmojis.StopSign, string.Format(CultureInfo.CurrentCulture, StopCommandStrings.SendingStopSignal, appHostIdentifier));

        var stopped = await InteractionService.ShowStatusAsync(
            string.Format(CultureInfo.CurrentCulture, StopCommandStrings.StoppingAppHost, appHostIdentifier),
            async () => await _processShutdownService.StopAppHostAsync(appHostInfo, connection.StopAppHostAsync, cancellationToken).ConfigureAwait(false));

        // Reset cursor position after spinner
        InteractionService.DisplayPlainText("");

        if (stopped)
        {
            InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostStoppedSuccessfully, appHostIdentifier));
            return CompleteStopActivity(activity, CliExitCodes.Success);
        }
        else
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, StopCommandStrings.FailedToStopAppHost, appHostIdentifier));
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

}
