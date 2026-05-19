// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Processes;
using Aspire.Cli.Profiling;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Encapsulates the logic for launching an AppHost in detached (background) mode.
/// Used by both RunCommand (--detach) and StartCommand (no resource).
/// When adding new launch options, add them here and wire them in both commands.
/// </summary>
internal sealed class AppHostLauncher(
    IProjectLocator projectLocator,
    CliExecutionContext executionContext,
    IInteractionService interactionService,
    IAuxiliaryBackchannelMonitor backchannelMonitor,
    ICliHostEnvironment hostEnvironment,
    AspireCliTelemetry telemetry,
    ProfilingTelemetry profilingTelemetry,
    FileLoggerProvider fileLoggerProvider,
    ProcessShutdownService processShutdownService,
    IDetachedProcessLauncher detachedProcessLauncher,
    ILogger<AppHostLauncher> logger,
    TimeProvider timeProvider)
{
    private const int MaxDisplayedChildLogLines = 80;
    private const int MaxParentLogReplayLines = 200;
    private static readonly TimeSpan s_legacyDetachedStartupStabilityWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_legacyDetachedStartupProbeInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Shared option for the AppHost project file path.
    /// </summary>
    internal static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    /// <summary>
    /// Shared option for output format (JSON or table) in detached AppHost mode.
    /// </summary>
    internal static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = SharedCommandStrings.FormatOptionDescription
    };

    /// <summary>
    /// Shared option for isolated AppHost mode.
    /// </summary>
    internal static readonly Option<bool> s_isolatedOption = new("--isolated")
    {
        Description = SharedCommandStrings.IsolatedOptionDescription
    };

    /// <summary>
    /// Adds the detached launch options to a command so they appear in --help.
    /// Called by both RunCommand and StartCommand to keep options in sync.
    /// </summary>
    internal static void AddLaunchOptions(Command command)
    {
        command.Options.Add(s_appHostOption);
        command.Options.Add(s_formatOption);
        command.Options.Add(s_isolatedOption);
    }

    /// <summary>
    /// Launches an AppHost in detached mode, waits for the backchannel, and displays the result.
    /// </summary>
    /// <param name="passedAppHostProjectFile">The project file passed via --project, or null to auto-discover.</param>
    /// <param name="format">The output format (JSON or table).</param>
    /// <param name="isolated">Whether to run in isolated mode.</param>
    /// <param name="isExtensionHost">Whether running inside VS Code extension.</param>
    /// <param name="waitForDebugger">Whether the AppHost is waiting for a debugger to attach.</param>
    /// <param name="globalArgs">Global CLI args to forward to child process.</param>
    /// <param name="additionalArgs">Additional unmatched args to forward.</param>
    /// <param name="stopAfterLaunchDelay">Optional delay after launch before stopping the AppHost.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CommandResult"/> indicating success or failure.</returns>
    public async Task<CommandResult> LaunchDetachedAsync(
        FileInfo? passedAppHostProjectFile,
        OutputFormat? format,
        bool isolated,
        bool isExtensionHost,
        bool waitForDebugger,
        IEnumerable<string> globalArgs,
        IEnumerable<string> additionalArgs,
        TimeSpan? stopAfterLaunchDelay,
        CancellationToken cancellationToken)
    {
        // In JSON mode or non-interactive mode, avoid interactive prompts.
        var multipleAppHostBehavior = format == OutputFormat.Json || !hostEnvironment.SupportsInteractiveInput
            ? MultipleAppHostProjectsFoundBehavior.Throw
            : MultipleAppHostProjectsFoundBehavior.Prompt;

        // Failure mode 1: Project not found
        AppHostProjectSearchResult searchResult;
        try
        {
            searchResult = await projectLocator.UseOrFindAppHostProjectFileAsync(
                passedAppHostProjectFile,
                multipleAppHostBehavior,
                createSettingsFile: false,
                cancellationToken);
        }
        catch (ProjectLocatorException ex)
        {
            return BaseCommand.HandleProjectLocatorException(ex, interactionService, telemetry);
        }

        var effectiveAppHostFile = searchResult.SelectedProjectFile;

        if (effectiveAppHostFile is null)
        {
            return CommandResult.Failure(CliExitCodes.FailedToFindProject);
        }

        logger.LogDebug("Starting AppHost in background: {AppHostPath}", effectiveAppHostFile.FullName);

        // Check for running instance and stop it if found (same behavior as regular run)
        await StopExistingInstancesAsync(effectiveAppHostFile, cancellationToken);

        // Build child process arguments
        var childLogFile = GenerateChildLogFilePath(executionContext.LogsDirectory.FullName, timeProvider);
        executionContext.AppHostCliLogFilePath = childLogFile;
        var (executablePath, childArgs) = BuildChildProcessArgs(effectiveAppHostFile, childLogFile, isolated, globalArgs, additionalArgs);

        // Compute the expected socket prefix for backchannel detection
        var expectedSocketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(
            effectiveAppHostFile.FullName,
            executionContext.HomeDirectory.FullName);
        var expectedHash = AppHostHelper.ExtractHashFromSocketPath(expectedSocketPrefix)!;
        var legacyHashes = AppHostHelper.ComputeLegacyHashes(effectiveAppHostFile.FullName);

        logger.LogDebug("Waiting for socket with prefix: {SocketPrefix}, Hash: {Hash}", expectedSocketPrefix, expectedHash);
        if (legacyHashes.Length > 0)
        {
            logger.LogDebug("Also searching for legacy hash(es): {LegacyHashes}", string.Join(", ", legacyHashes));
        }

        // If --wait-for-debugger is active, show a message so the user knows the AppHost
        // is paused. In detached mode we don't have the AppHost PID (stdout is suppressed),
        // so we show a generic message without a PID.
        if (waitForDebugger)
        {
            interactionService.DisplayMessage(
                KnownEmojis.Bug,
                InteractionServiceStrings.WaitingForDebuggerToAttachToAppHost);
        }

        // Start the child process and wait for the backchannel
        LaunchResult launchResult;
        try
        {
            launchResult = await interactionService.ShowStatusAsync(
                RunCommandStrings.StartingAppHostInBackground,
                () => LaunchAndWaitForBackchannelAsync(executablePath, childArgs, expectedHash, legacyHashes, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Cancelled(CliExitCodes.Success);
        }

        // Handle failure cases
        if (launchResult.Backchannel is null || launchResult.ChildProcess is null)
        {
            return HandleLaunchFailure(launchResult, childLogFile);
        }

        // Display results
        DisplayLaunchResult(launchResult, effectiveAppHostFile, childLogFile, format, isExtensionHost);

        if (stopAfterLaunchDelay is not null)
        {
            await StopLaunchedAppHostAsync(launchResult, stopAfterLaunchDelay.Value, cancellationToken).ConfigureAwait(false);
        }

        return CommandResult.Success();
    }

    private async Task StopLaunchedAppHostAsync(LaunchResult result, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (result.Backchannel is not null)
        {
            // Reuse the shared "RPC stop + wait for termination" flow so capture mode follows the
            // same teardown path as socket-discovered running-instance stops.
            var manager = new RunningInstanceManager(logger, interactionService, timeProvider);
            await manager.StopAndMonitorAsync(result.Backchannel, cancellationToken).ConfigureAwait(false);
        }

        if (result.ChildProcess is { HasExited: false } childProcess)
        {
            // Safety net for the hidden capture path: if the RPC stop did not bring the spawned
            // child CLI down within the grace period, terminate the process tree so we never
            // leave an orphaned AppHost behind.
            try
            {
                await childProcess.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                childProcess.Kill(entireProcessTree: true);
            }
            catch (OperationCanceledException) when (!childProcess.HasExited)
            {
                childProcess.Kill(entireProcessTree: true);
                throw;
            }
        }
    }

    private async Task StopExistingInstancesAsync(FileInfo effectiveAppHostFile, CancellationToken cancellationToken)
    {
        var existingSockets = AppHostHelper.FindMatchingSockets(
            effectiveAppHostFile.FullName,
            executionContext.HomeDirectory.FullName);

        if (existingSockets.Length > 0)
        {
            logger.LogDebug("Found {Count} running instance(s) for this AppHost, stopping them first.", existingSockets.Length);
            var manager = new RunningInstanceManager(logger, interactionService, timeProvider);
            var stopTasks = existingSockets.Select(socket =>
                manager.StopRunningInstanceAsync(socket, cancellationToken));
            await Task.WhenAll(stopTasks).ConfigureAwait(false);
        }
    }

    private (string ExecutablePath, List<string> ChildArgs) BuildChildProcessArgs(
        FileInfo effectiveAppHostFile,
        string childLogFile,
        bool isolated,
        IEnumerable<string> globalArgs,
        IEnumerable<string> additionalArgs)
    {
        var args = new List<string>
        {
            "run",
            "--non-interactive",
            s_appHostOption.Name,
            effectiveAppHostFile.FullName,
            "--log-file",
            childLogFile
        };

        args.AddRange(globalArgs);

        if (isolated)
        {
            args.Add(s_isolatedOption.Name);
        }

        foreach (var token in additionalArgs)
        {
            args.Add(token);
        }

        var dotnetPath = Environment.ProcessPath ?? "dotnet";
        var isDotnetHost = dotnetPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                           dotnetPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

        var entryAssemblyPath = Environment.GetCommandLineArgs().FirstOrDefault();

        var childArgs = new List<string>();
        if (isDotnetHost && !string.IsNullOrEmpty(entryAssemblyPath) && entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            childArgs.Add(entryAssemblyPath);
        }

        childArgs.AddRange(args);

        logger.LogDebug("Spawning child CLI: {Executable} (isDotnetHost={IsDotnetHost}) with args: {Args}",
            dotnetPath, isDotnetHost, string.Join(" ", childArgs));
        logger.LogDebug("Working directory: {WorkingDirectory}", executionContext.WorkingDirectory.FullName);

        return (dotnetPath, childArgs);
    }

    /// <summary>
    /// Prefix for environment variables that configure extension-host mode.
    /// Any environment variable starting with this prefix is removed from
    /// detached child processes to prevent them from entering extension mode.
    /// Keep the DEBUG_SESSION_* and DCP session variables intact because the launched AppHost
    /// still relies on them for IDE execution and dashboard integration.
    /// </summary>
    internal const string ExtensionEnvironmentVariablePrefix = "ASPIRE_EXTENSION_";

    /// <summary>
    /// Returns <see langword="true"/> if the specified environment variable name
    /// should be removed from detached child CLI processes.
    /// </summary>
    internal static bool IsExtensionEnvironmentVariable(string name) =>
        name.StartsWith(ExtensionEnvironmentVariablePrefix, StringComparison.OrdinalIgnoreCase);

    internal static Dictionary<string, string> CreateDetachedChildEnvironment(Activity? activity)
    {
        var environment = new Dictionary<string, string> { [KnownConfigNames.CliRunDetached] = "true" };

        ProfilingTelemetry.AddActivityContextToEnvironment(activity, environment);
        ProfileCaptureEnvironment.AddCurrentToEnvironment(environment);
        return environment;
    }

    private record LaunchResult(Process? ChildProcess, IAppHostAuxiliaryBackchannel? Backchannel, DashboardUrlsState? DashboardUrls, bool ChildExitedEarly, int ChildExitCode, DateTimeOffset? ChildStartedAt = null);

    private async Task<LaunchResult> LaunchAndWaitForBackchannelAsync(
        string executablePath,
        List<string> childArgs,
        string expectedHash,
        IReadOnlyList<string> legacyHashes,
        CancellationToken cancellationToken)
    {
        Process childProcess;

        using (var spawnActivity = profilingTelemetry.StartDetachedSpawnChild(executablePath, childArgs, "run"))
        {
            try
            {
                childProcess = detachedProcessLauncher.Start(
                    executablePath,
                    childArgs,
                    executionContext.WorkingDirectory.FullName,
                    IsExtensionEnvironmentVariable,
                    CreateDetachedChildEnvironment(Activity.Current));
                spawnActivity.SetProcessId(childProcess.Id);
            }
            catch (Exception ex)
            {
                spawnActivity.SetError(ex.Message);
                logger.LogError(ex, "Failed to start child CLI process");
                return new LaunchResult(null, null, null, false, 0);
            }
        }

        var childStartedAt = new DateTimeOffset(childProcess.StartTime);
        logger.LogDebug("Child CLI process started with PID: {PID}", childProcess.Id);

        var startTime = timeProvider.GetUtcNow();
        var timeout = TimeSpan.FromSeconds(120);
        using var waitForBackchannelActivity = profilingTelemetry.StartDetachedWaitForBackchannel(childProcess.Id, expectedHash, legacyHashes.Count > 0);
        var scanCount = 0;
        IAppHostAuxiliaryBackchannel? connection = null;
        DashboardUrlsState? dashboardUrls = null;
        string? launchFailureMessage = null;

        try
        {
            while (timeProvider.GetUtcNow() - startTime < timeout)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (childProcess.HasExited)
                {
                    return CreateChildExitedLaunchResult(childProcess, waitForBackchannelActivity, childStartedAt);
                }

                await backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
                scanCount++;

                connection ??= backchannelMonitor.GetConnectionsByHash(expectedHash).FirstOrDefault()
                    ?? legacyHashes.SelectMany(backchannelMonitor.GetConnectionsByHash).FirstOrDefault();
                if (connection is not null)
                {
                    waitForBackchannelActivity.SetBackchannelScanCount(scanCount);
                    waitForBackchannelActivity.AddStartAppHostBackchannelConnectedEvent();
                    if (dashboardUrls is null)
                    {
                        using var getDashboardUrlsActivity = profilingTelemetry.StartDetachedGetDashboardUrls();
                        try
                        {
                            dashboardUrls = await connection.GetDashboardUrlsAsync(cancellationToken).ConfigureAwait(false);
                            getDashboardUrlsActivity.SetAppHostDashboardUrls(dashboardUrls);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            getDashboardUrlsActivity.SetError(ex.Message);
                            logger.LogDebug(ex, "Failed to retrieve dashboard URLs from backchannel connection. Continuing without dashboard URLs.");
                        }
                    }

                    var remainingTimeout = timeout - (timeProvider.GetUtcNow() - startTime);
                    if (remainingTimeout <= TimeSpan.Zero)
                    {
                        break;
                    }

                    using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var readinessTask = WaitForAppHostReadyAsync(connection, readinessCts.Token);
                    var childExitTask = childProcess.WaitForExitAsync(cancellationToken);
                    var timeoutTask = Task.Delay(remainingTimeout, timeProvider, cancellationToken);

                    var completedTask = await Task.WhenAny(readinessTask, childExitTask, timeoutTask).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (completedTask == readinessTask)
                    {
                        bool? appHostReady;
                        try
                        {
                            appHostReady = await readinessTask.ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            launchFailureMessage = "Failed while waiting for AppHost startup readiness.";
                            logger.LogDebug(ex, "Failed while waiting for AppHost startup readiness from auxiliary backchannel.");
                            if (childProcess.HasExited)
                            {
                                return CreateChildExitedLaunchResult(childProcess, waitForBackchannelActivity, childStartedAt);
                            }

                            break;
                        }

                        if (appHostReady is null)
                        {
                            logger.LogDebug(
                                "AppHost does not support startup readiness RPC. Probing legacy startup state for {StabilityWindow} before detaching.",
                                s_legacyDetachedStartupStabilityWindow);

                            if (!await WaitForLegacyDetachedStartupStabilityAsync(connection, childExitTask, remainingTimeout, timeProvider, cancellationToken).ConfigureAwait(false))
                            {
                                await childExitTask.ConfigureAwait(false);
                                return CreateChildExitedLaunchResult(childProcess, waitForBackchannelActivity, childStartedAt);
                            }

                            return new LaunchResult(childProcess, connection, dashboardUrls, false, 0, childStartedAt);
                        }

                        if (appHostReady == true)
                        {
                            return new LaunchResult(childProcess, connection, dashboardUrls, false, 0, childStartedAt);
                        }
                    }
                    else
                    {
                        readinessCts.Cancel();
                        ObserveFaults(readinessTask);

                        if (completedTask == childExitTask)
                        {
                            await childExitTask.ConfigureAwait(false);
                            return CreateChildExitedLaunchResult(childProcess, waitForBackchannelActivity, childStartedAt);
                        }

                        break;
                    }
                }

                try
                {
                    await childProcess.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Expected - the 500ms delay elapsed without the process exiting
                }
            }
        }
        catch (OperationCanceledException)
        {
            await RequestGracefulShutdownThenForceKillAsync(childProcess, childStartedAt).ConfigureAwait(false);
            throw;
        }

        waitForBackchannelActivity.SetBackchannelScanCount(scanCount);
        waitForBackchannelActivity.SetError(launchFailureMessage ?? "Timed out waiting for AppHost startup readiness.");
        await RequestGracefulShutdownThenForceKillAsync(childProcess, childStartedAt).ConfigureAwait(false);
        return new LaunchResult(childProcess, null, dashboardUrls, false, 0, childStartedAt);
    }

    private Task RequestGracefulShutdownThenForceKillAsync(Process childProcess, DateTimeOffset childStartedAt)
    {
        return processShutdownService.StopProcessTreeAsync(
            childProcess.Id,
            childStartedAt,
            includeStartTimeForDcp: true,
            CancellationToken.None);
    }

    private LaunchResult CreateChildExitedLaunchResult(Process childProcess, ProfilingTelemetry.ActivityScope waitForBackchannelActivity, DateTimeOffset childStartedAt)
    {
        var exitCode = childProcess.ExitCode;
        waitForBackchannelActivity.SetProcessExitCode(exitCode);
        if (IsSuccessfulDetachedEarlyExit(exitCode))
        {
            logger.LogInformation("Child CLI process exited successfully before AppHost readiness was observed.");
        }
        else
        {
            waitForBackchannelActivity.SetError($"Child CLI exited with code {exitCode}.");
            logger.LogWarning("Child CLI process exited with code {ExitCode}", exitCode);
        }

        return new LaunchResult(childProcess, null, null, true, exitCode, ChildStartedAt: childStartedAt);
    }

    internal static async Task<bool?> WaitForAppHostReadyAsync(IAppHostAuxiliaryBackchannel connection, CancellationToken cancellationToken)
    {
        var startupState = await connection.WaitForAppHostReadyAsync(cancellationToken).ConfigureAwait(false);
        return startupState?.IsReady;
    }

    internal static async Task<bool> WaitForLegacyDetachedStartupStabilityAsync(
        IAppHostAuxiliaryBackchannel connection,
        Task childExitTask,
        TimeSpan remainingTimeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var stabilityWindow = remainingTimeout < s_legacyDetachedStartupStabilityWindow
            ? remainingTimeout
            : s_legacyDetachedStartupStabilityWindow;

        if (connection.SupportsV2)
        {
            return await WaitForLegacyDetachedStartupResourceSnapshotProbeAsync(
                connection,
                childExitTask,
                stabilityWindow,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
        }

        var completedTask = await Task.WhenAny(
            childExitTask,
            Task.Delay(stabilityWindow, timeProvider, cancellationToken)).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return completedTask != childExitTask;
    }

    private static async Task<bool> WaitForLegacyDetachedStartupResourceSnapshotProbeAsync(
        IAppHostAuxiliaryBackchannel connection,
        Task childExitTask,
        TimeSpan stabilityWindow,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Older AppHosts do not expose the explicit readiness RPC. Resource snapshots are the
        // best available V2 probe because this call depends on the AppHost model/notification
        // services being available, unlike dashboard URL or process-info calls that can succeed
        // as soon as the auxiliary server socket is listening.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutTask = Task.Delay(stabilityWindow, timeProvider, cancellationToken);

        while (true)
        {
            Task<List<ResourceSnapshot>> probeTask;
            try
            {
                probeTask = connection.GetResourceSnapshotsAsync(includeHidden: false, probeCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                probeTask = Task.FromException<List<ResourceSnapshot>>(ex);
            }

            var completedTask = await Task.WhenAny(probeTask, childExitTask, timeoutTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (completedTask == childExitTask)
            {
                probeCts.Cancel();
                ObserveFaults(probeTask);
                return false;
            }

            if (completedTask == timeoutTask)
            {
                probeCts.Cancel();
                ObserveFaults(probeTask);
                return true;
            }

            try
            {
                await probeTask.ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                var delayTask = Task.Delay(s_legacyDetachedStartupProbeInterval, timeProvider, cancellationToken);
                completedTask = await Task.WhenAny(delayTask, childExitTask, timeoutTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (completedTask == childExitTask)
                {
                    return false;
                }

                if (completedTask == timeoutTask)
                {
                    return true;
                }
            }
        }
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private CommandResult HandleLaunchFailure(LaunchResult result, string childLogFile)
    {
        if (result.ChildProcess is null)
        {
            interactionService.DisplayError(RunCommandStrings.FailedToStartAppHost);
            return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost);
        }

        if (result.ChildExitedEarly && IsSuccessfulDetachedEarlyExit(result.ChildExitCode))
        {
            return CommandResult.Success();
        }

        string? failureMessage;
        if (result.ChildExitedEarly)
        {
            failureMessage = GetDetachedFailureMessage(result.ChildExitCode);
        }
        else
        {
            failureMessage = RunCommandStrings.TimeoutWaitingForAppHost;
        }

        interactionService.DisplayError(RunCommandStrings.FailedToStartAppHost);
        DisplayChildLogTail(childLogFile, result.ChildProcess.Id);
        if (failureMessage is not null && !string.Equals(failureMessage, RunCommandStrings.FailedToStartAppHost, StringComparison.Ordinal))
        {
            interactionService.DisplayError(failureMessage);
        }

        return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost);
    }

    private void DisplayChildLogTail(string childLogFile, int childProcessId)
    {
        IReadOnlyList<CliLogFormat.FileLogEntry> replayEntries;
        IReadOnlyList<string> displayLines;
        try
        {
            replayEntries = ReadChildLogReplayTail(childLogFile, MaxParentLogReplayLines);
            displayLines = ReadChildLogTail(childLogFile, MaxDisplayedChildLogLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Failed to read child CLI log file {ChildLogFile}", childLogFile);
            return;
        }

        if (replayEntries.Count == 0 && displayLines.Count == 0)
        {
            return;
        }

        if (displayLines.Count > 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, $"{RunCommandStrings.RecentAppHostStartupOutput}:");
            interactionService.DisplayLines(displayLines.Select(line => (OutputLineStream.StdOut, line)));
        }

        if (replayEntries.Count > 0)
        {
            ReplayChildLogTailToParentLog(childLogFile, childProcessId, replayEntries);
        }
    }

    private void ReplayChildLogTailToParentLog(string childLogFile, int childProcessId, IReadOnlyList<CliLogFormat.FileLogEntry> entries)
    {
        fileLoggerProvider.WriteLog(
            timeProvider.GetUtcNow(),
            LogLevel.Information,
            nameof(AppHostLauncher),
            $"Begin detached AppHost startup log excerpt from child process {childProcessId}.");

        foreach (var entry in entries)
        {
            if (CliLogFormat.TryGetLogLevelFromFileToken(entry.Level, out var logLevel))
            {
                fileLoggerProvider.WriteLog(timeProvider.GetUtcNow(), logLevel, CliLogFormat.GetDetachedAppHostCategory(entry.Category), entry.Message);
            }
        }

        fileLoggerProvider.WriteLog(
            timeProvider.GetUtcNow(),
            LogLevel.Information,
            nameof(AppHostLauncher),
            $"End detached AppHost startup log excerpt. Child log: {childLogFile}");
    }

    private void DisplayLaunchResult(
        LaunchResult result,
        FileInfo effectiveAppHostFile,
        string childLogFile,
        OutputFormat? format,
        bool isExtensionHost)
    {
        var appHostInfo = result.Backchannel!.AppHostInfo;
        var dashboardUrls = result.DashboardUrls;
        var pid = appHostInfo?.ProcessId ?? result.ChildProcess!.Id;

        if (format == OutputFormat.Json)
        {
            var jsonResult = new DetachOutputInfo(
                effectiveAppHostFile.FullName,
                pid,
                result.ChildProcess!.Id,
                dashboardUrls?.BaseUrlWithLoginToken,
                childLogFile);
            var json = JsonSerializer.Serialize(jsonResult, RunCommandJsonContext.RelaxedEscaping.DetachOutputInfo);
            interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            var appHostRelativePath = Path.GetRelativePath(executionContext.WorkingDirectory.FullName, effectiveAppHostFile.FullName);
            RunCommand.RenderAppHostSummary(
                interactionService,
                appHostRelativePath,
                dashboardUrls?.BaseUrlWithLoginToken,
                codespacesUrl: null,
                childLogFile,
                isExtensionHost,
                pid);
            interactionService.DisplayEmptyLine();

            interactionService.DisplaySuccess(RunCommandStrings.AppHostStartedSuccessfully);
        }
    }

    /// <summary>
    /// Creates a user-facing error message for detached child process failures.
    /// </summary>
    internal static string GetDetachedFailureMessage(int childExitCode)
    {
        return childExitCode switch
        {
            CliExitCodes.FailedToBuildArtifacts => RunCommandStrings.AppHostFailedToBuild,
            _ => string.Format(CultureInfo.CurrentCulture, RunCommandStrings.AppHostExitedWithCode, childExitCode)
        };
    }

    internal static bool IsSuccessfulDetachedEarlyExit(int childExitCode)
        => childExitCode == CliExitCodes.Success;

    /// <summary>
    /// Generates a unique log file path for a detached child CLI process.
    /// </summary>
    internal static string GenerateChildLogFilePath(string logsDirectory, TimeProvider timeProvider)
    {
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
        var uniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var fileName = $"cli_{timestamp}_detach-child_{uniqueId}.log";
        return Path.Combine(logsDirectory, fileName);
    }

    internal static IReadOnlyList<string> ReadChildLogTail(string childLogFile, int maxLines = 80)
    {
        if (maxLines <= 0 || !File.Exists(childLogFile))
        {
            return [];
        }

        var lines = new Queue<string>(maxLines);
        var guestCommandLines = new Queue<string>(maxLines);
        IReadOnlyList<string>? failedGuestCommandLines = null;
        var trackingGuestCommand = false;
        using var reader = File.OpenText(childLogFile);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!CliLogFormat.TryParseFileLogLine(line, out var entry))
            {
                continue;
            }

            if (IsGuestCommandStart(entry))
            {
                trackingGuestCommand = true;
                guestCommandLines.Clear();
                continue;
            }

            if (trackingGuestCommand && TryFormatGuestCommandOutputForDisplay(entry, out var guestCommandLine))
            {
                EnqueueBounded(guestCommandLines, guestCommandLine, maxLines);
                continue;
            }

            if (IsGuestAppHostExit(entry))
            {
                if (trackingGuestCommand && guestCommandLines.Count > 0)
                {
                    failedGuestCommandLines = guestCommandLines.ToArray();
                }

                trackingGuestCommand = false;
                guestCommandLines.Clear();
                continue;
            }

            if (!TryFormatChildLogEntryForDisplay(entry, out var displayLine))
            {
                continue;
            }

            EnqueueBounded(lines, displayLine, maxLines);
        }

        if (failedGuestCommandLines is not null)
        {
            return failedGuestCommandLines;
        }

        if (trackingGuestCommand && guestCommandLines.Count > 0)
        {
            return guestCommandLines.ToArray();
        }

        return lines.ToArray();
    }

    internal static IReadOnlyList<CliLogFormat.FileLogEntry> ReadChildLogReplayTail(string childLogFile, int maxLines = 200)
    {
        if (maxLines <= 0 || !File.Exists(childLogFile))
        {
            return [];
        }

        var entries = new Queue<CliLogFormat.FileLogEntry>(maxLines);
        using var reader = File.OpenText(childLogFile);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!TryParseChildLogLineForReplay(line, out var entry))
            {
                continue;
            }

            if (entries.Count == maxLines)
            {
                entries.Dequeue();
            }

            entries.Enqueue(entry);
        }

        return entries.ToArray();
    }

    private static bool TryParseChildLogLineForReplay(string line, out CliLogFormat.FileLogEntry entry)
    {
        if (!CliLogFormat.TryParseFileLogLine(line, out entry))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.Message))
        {
            return false;
        }

        if (entry.Category is CliLogFormat.Categories.Stdout or CliLogFormat.Categories.Stderr)
        {
            return false;
        }

        if (entry.Category is CliLogFormat.Categories.Build or CliLogFormat.Categories.AppHost || entry.Category.StartsWith(CliLogFormat.Categories.AppHostPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        if (entry.Category is CliLogFormat.Categories.GuestAppHostProject
            && entry.Message.StartsWith(CliLogFormat.MessagePrefixes.Executing, StringComparison.Ordinal))
        {
            return true;
        }

        if (entry.Level is CliLogFormat.FileLevelTokens.Warning or CliLogFormat.FileLevelTokens.Error or CliLogFormat.FileLevelTokens.Critical
            && entry.Category is not CliLogFormat.Categories.AspireCliTelemetry)
        {
            return true;
        }

        return false;
    }

    private static bool TryFormatChildLogEntryForDisplay(CliLogFormat.FileLogEntry entry, out string displayLine)
    {
        displayLine = string.Empty;

        if (string.IsNullOrWhiteSpace(entry.Message))
        {
            return false;
        }

        if (entry.Category is CliLogFormat.Categories.Stdout or CliLogFormat.Categories.Stderr)
        {
            return false;
        }

        if (entry.Category is CliLogFormat.Categories.Build or CliLogFormat.Categories.AppHost || entry.Category.StartsWith(CliLogFormat.Categories.AppHostPrefix, StringComparison.Ordinal))
        {
            displayLine = entry.Message;
            return true;
        }

        if (entry.Category is CliLogFormat.Categories.GuestAppHostProject
            && entry.Message.StartsWith("AppHost server process has exited.", StringComparison.Ordinal))
        {
            return false;
        }

        if (entry.Level is CliLogFormat.FileLevelTokens.Warning or CliLogFormat.FileLevelTokens.Error or CliLogFormat.FileLevelTokens.Critical
            && entry.Category is not CliLogFormat.Categories.AspireCliTelemetry)
        {
            displayLine = $"{entry.Category}: {entry.Message}";
            return true;
        }

        return false;
    }

    private static bool TryFormatGuestCommandOutputForDisplay(CliLogFormat.FileLogEntry entry, out string displayLine)
    {
        displayLine = string.Empty;

        if (string.IsNullOrWhiteSpace(entry.Message))
        {
            return false;
        }

        if (entry.Category is CliLogFormat.Categories.AppHost)
        {
            displayLine = entry.Message;
            return true;
        }

        return false;
    }

    private static bool IsGuestCommandStart(CliLogFormat.FileLogEntry entry)
        => entry.Category is CliLogFormat.Categories.GuestAppHostProject
            && entry.Level is CliLogFormat.FileLevelTokens.Debug
            && entry.Message.StartsWith(CliLogFormat.MessagePrefixes.Executing, StringComparison.Ordinal);

    private static bool IsGuestAppHostExit(CliLogFormat.FileLogEntry entry)
        => entry.Category is CliLogFormat.Categories.GuestAppHostProject
            && entry.Message.Contains(" apphost exited with code ", StringComparison.Ordinal);

    private static void EnqueueBounded(Queue<string> lines, string line, int maxLines)
    {
        if (lines.Count == maxLines)
        {
            lines.Dequeue();
        }

        lines.Enqueue(line);
    }

}
