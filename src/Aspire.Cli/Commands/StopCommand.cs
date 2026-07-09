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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Commands;

internal sealed class StopCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<StopCommand> _logger;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IEnvironment _environment;
    private readonly ProcessTreeGracefulShutdownService _processShutdownService;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly IProjectLocator _projectLocator;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly AppHostCleanupLauncher _cleanupLauncher;
    private readonly IConfiguration _configuration;
    private static readonly SemVersion s_minimumResourceCleanupVersion = SemVersion.Parse("13.5.0");

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", StopCommandStrings.ProjectArgumentDescription);

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = StopCommandStrings.AllOptionDescription
    };

    private static readonly Option<bool> s_forceOption = new("--force")
    {
        Description = StopCommandStrings.ForceOptionDescription
    };

    public StopCommand(
        AppHostConnectionResolver connectionResolver,
        ICliHostEnvironment hostEnvironment,
        IEnvironment environment,
        ProcessTreeGracefulShutdownService processShutdownService,
        IProjectLocator projectLocator,
        IAppHostProjectFactory projectFactory,
        AppHostCleanupLauncher cleanupLauncher,
        IConfiguration configuration,
        ILogger<StopCommand> logger,
        ProfilingTelemetry profilingTelemetry,
        CommonCommandServices services)
        : base("stop", StopCommandStrings.Description, services)
    {
        _connectionResolver = connectionResolver;
        _hostEnvironment = hostEnvironment;
        _environment = environment;
        _processShutdownService = processShutdownService;
        _projectLocator = projectLocator;
        _projectFactory = projectFactory;
        _cleanupLauncher = cleanupLauncher;
        _configuration = configuration;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;

        Options.Add(s_appHostOption);
        Options.Add(s_allOption);
        Options.Add(s_forceOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var stopAll = parseResult.GetValue(s_allOption);
        var force = parseResult.GetValue(s_forceOption);
        using var activity = _profilingTelemetry.StartStopCommand(stopAll, passedAppHostProjectFile is not null);

        // Validate mutual exclusivity of --all and --project
        if (stopAll && passedAppHostProjectFile is not null)
        {
            return CommandResult.Failure(CompleteStopActivity(activity, CliExitCodes.FailedToFindProject), string.Format(CultureInfo.InvariantCulture, StopCommandStrings.AllAndProjectMutuallyExclusive, s_allOption.Name, s_appHostOption.Name));
        }

        if (stopAll && force)
        {
            return CommandResult.Failure(CompleteStopActivity(activity, CliExitCodes.InvalidCommand), string.Format(CultureInfo.InvariantCulture, StopCommandStrings.AllAndProjectMutuallyExclusive, s_allOption.Name, s_forceOption.Name));
        }

        if (force)
        {
            return CommandResult.FromExitCode(CompleteStopActivity(activity, await ForceStopAppHostAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false)));
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

    private async Task<int> ForceStopAppHostAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        var appHostFile = await TryResolveAppHostFileAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false);

        var stopResult = appHostFile is not null
            ? new StopAppHostResult(await ExecuteInteractiveAsync(appHostFile, cancellationToken).ConfigureAwait(false), appHostFile)
            : _hostEnvironment.SupportsInteractiveInput
                ? await ExecuteInteractiveWithResultAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false)
                : await ExecuteNonInteractiveWithResultAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false);

        if (stopResult.ExitCode != CliExitCodes.Success)
        {
            return stopResult.ExitCode;
        }

        appHostFile ??= stopResult.AppHostFile;
        if (appHostFile is null)
        {
            InteractionService.DisplayError("Unrecognized app host type.");
            return CliExitCodes.FailedToFindProject;
        }

        var project = _projectFactory.TryGetProject(appHostFile);
        if (project is null)
        {
            InteractionService.DisplayError("Unrecognized app host type.");
            return CliExitCodes.FailedToFindProject;
        }

        var aspireHostingVersion = await project.GetAspireHostingVersionAsync(appHostFile, cancellationToken).ConfigureAwait(false);
        if (!SupportsResourceCleanup(aspireHostingVersion))
        {
            InteractionService.DisplayMessage(
                KnownEmojis.Warning,
                string.Format(CultureInfo.CurrentCulture, StopCommandStrings.ForceCleanupUnsupportedWarning, aspireHostingVersion ?? StopCommandStrings.UnknownAspireHostingVersion, s_minimumResourceCleanupVersion));
            return CliExitCodes.Success;
        }

        if (!AppHostStartupTimeout.TryGetTimeoutSeconds(_configuration, InteractionService, out var timeoutSeconds))
        {
            return CliExitCodes.InvalidCommand;
        }

        return await _cleanupLauncher.CleanupAsync(project, appHostFile, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    private static bool SupportsResourceCleanup(string? aspireHostingVersion)
    {
        if (!SemVersion.TryParse(aspireHostingVersion, out var version))
        {
            return false;
        }

        return version.Major > s_minimumResourceCleanupVersion.Major ||
            version.Major == s_minimumResourceCleanupVersion.Major && version.Minor > s_minimumResourceCleanupVersion.Minor ||
            version.Major == s_minimumResourceCleanupVersion.Major && version.Minor == s_minimumResourceCleanupVersion.Minor && version.Patch >= s_minimumResourceCleanupVersion.Patch;
    }

    private async Task<FileInfo?> TryResolveAppHostFileAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        var multipleAppHostBehavior = _hostEnvironment.SupportsInteractiveInput
            ? MultipleAppHostProjectsFoundBehavior.Prompt
            : MultipleAppHostProjectsFoundBehavior.Throw;

        try
        {
            var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(
                passedAppHostProjectFile,
                multipleAppHostBehavior,
                createSettingsFile: false,
                cancellationToken).ConfigureAwait(false);

            return searchResult.SelectedProjectFile;
        }
        catch (ProjectLocatorException ex)
        {
            if (passedAppHostProjectFile is not null)
            {
                var projectOptionSpecifiedAsDirectory = Directory.Exists(passedAppHostProjectFile.FullName);
                var (_, errorMessage) = ProjectLocatorErrorHelper.GetExitCodeAndMessage(ex, projectOptionSpecifiedAsDirectory);
                InteractionService.DisplayError(errorMessage);
            }
            else
            {
                _logger.LogDebug(ex, "Failed to resolve AppHost project file for resource cleanup.");
            }

            return null;
        }
    }

    /// <summary>
    /// Handles the stop command in non-interactive mode by auto-resolving a single AppHost
    /// or returning an error when multiple AppHosts are running.
    /// </summary>
    private async Task<int> ExecuteNonInteractiveAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        var result = await ExecuteNonInteractiveWithResultAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    private async Task<StopAppHostResult> ExecuteNonInteractiveWithResultAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        // If --project is specified, use the standard resolver (no prompting needed)
        if (passedAppHostProjectFile is not null)
        {
            return await ExecuteInteractiveWithResultAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false);
        }

        // Scan for all running AppHosts
        var allConnections = await _connectionResolver.ResolveAllConnectionsAsync(
            SharedCommandStrings.ScanningForRunningAppHosts,
            cancellationToken);

        if (allConnections.Length == 0)
        {
            InteractionService.DisplayError(SharedCommandStrings.AppHostNotRunning);
            return new StopAppHostResult(CliExitCodes.FailedToFindProject, null);
        }

        // In non-interactive mode, only consider in-scope AppHosts (under current directory)
        // to avoid accidentally stopping unrelated AppHosts
        var inScopeConnections = allConnections.Where(c => c.Connection!.IsInScope).ToArray();

        // Single in-scope AppHost: auto-select it
        if (inScopeConnections.Length == 1)
        {
            var connection = inScopeConnections[0].Connection!;
            _profilingTelemetry.CurrentActivity.SetAppHostStopCount(1);
            var exitCode = await StopAppHostAsync(connection, GetSingleAppHostDisplayPath(connection), cancellationToken).ConfigureAwait(false);
            return new StopAppHostResult(exitCode, GetAppHostFile(connection));
        }

        // Multiple in-scope AppHosts or none in scope: error with guidance
        InteractionService.DisplayError(string.Format(CultureInfo.InvariantCulture, StopCommandStrings.MultipleAppHostsNonInteractive, s_appHostOption.Name, s_allOption.Name));
        return new StopAppHostResult(CliExitCodes.FailedToFindProject, null);
    }

    /// <summary>
    /// Handles the stop command in interactive mode, prompting the user to select an AppHost if multiple are running.
    /// </summary>
    private async Task<int> ExecuteInteractiveAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        var result = await ExecuteInteractiveWithResultAsync(passedAppHostProjectFile, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    private async Task<StopAppHostResult> ExecuteInteractiveWithResultAsync(FileInfo? passedAppHostProjectFile, CancellationToken cancellationToken)
    {
        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, StopCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return new StopAppHostResult(AppHostConnectionResultHandler.DisplayFailureAsInformation(result, InteractionService), null);
        }

        _profilingTelemetry.CurrentActivity.SetAppHostStopCount(1);
        var exitCode = await StopAppHostAsync(result.Connection!, GetSingleAppHostDisplayPath(result.Connection!), cancellationToken).ConfigureAwait(false);
        return new StopAppHostResult(exitCode, GetAppHostFile(result.Connection!));
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
        var displayPaths = FileSystemHelper.ShortenPaths(appHostPaths, _environment);
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
            // ProcessShutdownService only reports success once it has confirmed the AppHost process has
            // terminated, so the socket's owner is gone and the file is safe to remove by exact path. Doing
            // it here is the primary guard against a stale socket tripping up later commands: the AppHost's own
            // cleanup is skipped if it crashes hard, and the orphan-pruning backstop misfires on Windows when the
            // dead PID is reused (https://github.com/microsoft/aspire/issues/17587).
            AppHostHelper.TryDeleteSocketFile(connection.SocketPath, _logger);
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

    private static FileInfo? GetAppHostFile(IAppHostAuxiliaryBackchannel connection)
    {
        return string.IsNullOrEmpty(connection.AppHostInfo?.AppHostPath)
            ? null
            : new FileInfo(connection.AppHostInfo.AppHostPath);
    }

    private StringComparer GetAppHostPathComparer()
    {
        return _environment.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static string GetAppHostIdentifier(IAppHostAuxiliaryBackchannel connection, string displayPath, bool includeProcessId)
    {
        return includeProcessId && connection.AppHostInfo is { } appHostInfo
            ? string.Format(CultureInfo.CurrentCulture, StopCommandStrings.AppHostIdentifierWithProcessId, displayPath, appHostInfo.ProcessId)
            : displayPath;
    }

    private sealed record StopAppHostResult(int ExitCode, FileInfo? AppHostFile);
}
