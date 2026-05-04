// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Processes;
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
    ILogger<AppHostLauncher> logger,
    TimeProvider timeProvider)
{

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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code indicating success or failure.</returns>
    public async Task<int> LaunchDetachedAsync(
        FileInfo? passedAppHostProjectFile,
        OutputFormat? format,
        bool isolated,
        bool isExtensionHost,
        bool waitForDebugger,
        IEnumerable<string> globalArgs,
        IEnumerable<string> additionalArgs,
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
            return ExitCodeConstants.FailedToFindProject;
        }

        logger.LogDebug("Starting AppHost in background: {AppHostPath}", effectiveAppHostFile.FullName);

        // Check for running instance and stop it if found (same behavior as regular run)
        await StopExistingInstancesAsync(effectiveAppHostFile, cancellationToken);

        // Build child process arguments
        var childLogFile = GenerateChildLogFilePath(executionContext.LogsDirectory.FullName, timeProvider);
        var (executablePath, childArgs) = BuildChildProcessArgs(effectiveAppHostFile, childLogFile, isolated, globalArgs, additionalArgs);
        // Only create cross-process startup correlation when the profiling harness opted in.
        var startupTelemetryContext = profilingTelemetry.CreateContextFromCurrentActivity();

        // Compute the expected socket prefix for backchannel detection
        var expectedSocketPrefix = AppHostHelper.ComputeAuxiliarySocketPrefix(
            effectiveAppHostFile.FullName,
            executionContext.HomeDirectory.FullName);
        var expectedHash = AppHostHelper.ExtractHashFromSocketPath(expectedSocketPrefix)!;
        var legacyHash = AppHostHelper.ComputeLegacyHash(effectiveAppHostFile.FullName);

        logger.LogDebug("Waiting for socket with prefix: {SocketPrefix}, Hash: {Hash}", expectedSocketPrefix, expectedHash);
        if (legacyHash is not null)
        {
            logger.LogDebug("Also searching for legacy hash: {LegacyHash}", legacyHash);
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
        var launchResult = await interactionService.ShowStatusAsync(
            RunCommandStrings.StartingAppHostInBackground,
            () => LaunchAndWaitForBackchannelAsync(executablePath, childArgs, expectedHash, legacyHash, startupTelemetryContext, cancellationToken));

        // Handle failure cases
        if (launchResult.Backchannel is null || launchResult.ChildProcess is null)
        {
            return HandleLaunchFailure(launchResult, childLogFile);
        }

        // Display results
        DisplayLaunchResult(launchResult, effectiveAppHostFile, childLogFile, format, isExtensionHost);

        return ExitCodeConstants.Success;
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

    internal static Dictionary<string, string> CreateDetachedChildEnvironment(StartupTelemetryContext? startupTelemetryContext)
    {
        var environment = new Dictionary<string, string> { [KnownConfigNames.CliRunDetached] = "true" };
        startupTelemetryContext?.AddToEnvironment(environment);
        return environment;
    }

    private record LaunchResult(Process? ChildProcess, IAppHostAuxiliaryBackchannel? Backchannel, DashboardUrlsState? DashboardUrls, bool ChildExitedEarly, int ChildExitCode);

    private async Task<LaunchResult> LaunchAndWaitForBackchannelAsync(
        string executablePath,
        List<string> childArgs,
        string expectedHash,
        string? legacyHash,
        StartupTelemetryContext? startupTelemetryContext,
        CancellationToken cancellationToken)
    {
        Process childProcess;

        using (var spawnActivity = profilingTelemetry.StartDetachedSpawnChild(executablePath, childArgs.Count, "run", startupTelemetryContext))
        {
            try
            {
                childProcess = DetachedProcessLauncher.Start(
                    executablePath,
                    childArgs,
                    executionContext.WorkingDirectory.FullName,
                    IsExtensionEnvironmentVariable,
                    CreateDetachedChildEnvironment(startupTelemetryContext));
                spawnActivity.SetProcessId(childProcess.Id);
            }
            catch (Exception ex)
            {
                spawnActivity.SetError(ex.Message);
                logger.LogError(ex, "Failed to start child CLI process");
                return new LaunchResult(null, null, null, false, 0);
            }
        }

        logger.LogDebug("Child CLI process started with PID: {PID}", childProcess.Id);

        var startTime = timeProvider.GetUtcNow();
        var timeout = TimeSpan.FromSeconds(120);
        using var waitForBackchannelActivity = profilingTelemetry.StartDetachedWaitForBackchannel(childProcess.Id, expectedHash, legacyHash is not null, startupTelemetryContext);
        var scanCount = 0;

        while (timeProvider.GetUtcNow() - startTime < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (childProcess.HasExited)
            {
                var exitCode = childProcess.ExitCode;
                waitForBackchannelActivity.SetProcessExitCode(exitCode);
                waitForBackchannelActivity.SetError($"Child CLI exited with code {exitCode}.");
                logger.LogWarning("Child CLI process exited with code {ExitCode}", exitCode);
                return new LaunchResult(childProcess, null, null, true, exitCode);
            }

            await backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);
            scanCount++;

            var connection = backchannelMonitor.GetConnectionsByHash(expectedHash).FirstOrDefault()
                ?? (legacyHash is not null ? backchannelMonitor.GetConnectionsByHash(legacyHash).FirstOrDefault() : null);
            if (connection is not null)
            {
                waitForBackchannelActivity.SetBackchannelScanCount(scanCount);
                waitForBackchannelActivity.AddStartAppHostBackchannelConnectedEvent();
                DashboardUrlsState? dashboardUrls = null;
                using (var getDashboardUrlsActivity = profilingTelemetry.StartDetachedGetDashboardUrls(startupTelemetryContext))
                {
                    try
                    {
                        dashboardUrls = await connection.GetDashboardUrlsAsync(cancellationToken).ConfigureAwait(false);
                        getDashboardUrlsActivity.SetAppHostDashboardUrls(dashboardUrls);
                    }
                    catch (Exception ex)
                    {
                        getDashboardUrlsActivity.SetError(ex.Message);
                        logger.LogDebug(ex, "Failed to retrieve dashboard URLs from backchannel connection. Continuing without dashboard URLs.");
                    }
                }

                return new LaunchResult(childProcess, connection, dashboardUrls, false, 0);
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

        waitForBackchannelActivity.SetBackchannelScanCount(scanCount);
        waitForBackchannelActivity.SetError("Timed out waiting for AppHost backchannel.");
        return new LaunchResult(childProcess, null, null, false, 0);
    }

    private int HandleLaunchFailure(LaunchResult result, string childLogFile)
    {
        if (result.ChildProcess is null)
        {
            interactionService.DisplayError(RunCommandStrings.FailedToStartAppHost);
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }

        if (result.ChildExitedEarly)
        {
            interactionService.DisplayError(GetDetachedFailureMessage(result.ChildExitCode));
        }
        else
        {
            interactionService.DisplayError(RunCommandStrings.TimeoutWaitingForAppHost);

            if (!result.ChildProcess.HasExited)
            {
                try
                {
                    result.ChildProcess.Kill();
                }
                catch
                {
                    // Ignore errors when killing
                }
            }
        }

        interactionService.DisplayMessage(KnownEmojis.MagnifyingGlassTiltedLeft, string.Format(
            CultureInfo.CurrentCulture,
            RunCommandStrings.CheckLogsForDetails,
            childLogFile));

        return ExitCodeConstants.FailedToDotnetRunAppHost;
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
            ExitCodeConstants.FailedToBuildArtifacts => RunCommandStrings.AppHostFailedToBuild,
            _ => string.Format(CultureInfo.CurrentCulture, RunCommandStrings.AppHostExitedWithCode, childExitCode)
        };
    }

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
}
