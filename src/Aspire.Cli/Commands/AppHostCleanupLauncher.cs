// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

internal sealed class AppHostCleanupLauncher(
    CliExecutionContext executionContext,
    IInteractionService interactionService,
    FileLoggerProvider fileLoggerProvider,
    ILogger<AppHostCleanupLauncher> logger,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan s_appHostCleanupCancellationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_cleanupShutdownTimeout = TimeSpan.FromSeconds(10);

    public async Task<int> CleanupAsync(IAppHostProject project, FileInfo appHostFile, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var buildCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannelCompletionSource = new TaskCompletionSource<IAppHostCliBackchannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var environmentVariables = new Dictionary<string, string>
        {
            [KnownConfigNames.DcpResourceCleanupMode] = "true"
        };
        ProfilingTelemetry.AddCurrentContextToEnvironment(environmentVariables);

        var context = new AppHostProjectContext
        {
            AppHostFile = appHostFile,
            Watch = false,
            Debug = false,
            NoBuild = false,
            NoRestore = false,
            WaitForDebugger = false,
            Isolated = false,
            StartDebugSession = false,
            EnvironmentVariables = environmentVariables,
            UnmatchedTokens = [],
            WorkingDirectory = executionContext.WorkingDirectory,
            BuildCompletionSource = buildCompletionSource,
            BackchannelCompletionSource = backchannelCompletionSource,
        };

        var startupTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startupStartTimestamp = timeProvider.GetTimestamp();
        using var runCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        logger.LogDebug("Starting AppHost resource cleanup mode for {AppHostPath}", appHostFile.FullName);
        var pendingRun = project.RunAsync(context, runCancellationTokenSource.Token);

        bool buildSuccess;
        try
        {
            buildSuccess = await buildCompletionSource.Task.WaitAsync(GetRemainingStartupTimeout(startupStartTimestamp, startupTimeout), timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await CancelAppHostCleanupAsync(runCancellationTokenSource, pendingRun, cancellationToken).ConfigureAwait(false);
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, RunCommandStrings.TimeoutWaitingForAppHost, timeoutSeconds, CliConfigNames.AppHostStartupTimeout));
            return CliExitCodes.FailedToDotnetRunAppHost;
        }

        if (!buildSuccess)
        {
            if (context.OutputCollector is { } outputCollector)
            {
                interactionService.DisplayLines(outputCollector.GetLines());
            }

            return await pendingRun.ConfigureAwait(false);
        }

        var appHostStartupOutputStartIndex = context.OutputCollector?.GetLines().Count() ?? 0;
        using var logCaptureCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pendingLogCapture = Task.CompletedTask;

        try
        {
            var backchannel = await WaitForBackchannelAsync(
                pendingRun,
                backchannelCompletionSource,
                context.OutputCollector,
                appHostStartupOutputStartIndex,
                startupStartTimestamp,
                startupTimeout,
                cancellationToken).ConfigureAwait(false);

            if (backchannel is null)
            {
                return await pendingRun.ConfigureAwait(false);
            }

            pendingLogCapture = RunCommand.CaptureAppHostLogsAsync(fileLoggerProvider, backchannel, interactionService, logCaptureCancellationSource.Token);

            await backchannel.GetDashboardUrlsAsync(cancellationToken).ConfigureAwait(false);
            await backchannel.NotifyAppHostReadyAsync(cancellationToken).ConfigureAwait(false);
            await RequestAppHostCleanupStopAsync(backchannel, pendingRun, cancellationToken).ConfigureAwait(false);

            return await WaitForAppHostCleanupShutdownAsync(runCancellationTokenSource, pendingRun, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await CancelAppHostCleanupAsync(runCancellationTokenSource, pendingRun, cancellationToken).ConfigureAwait(false);
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, RunCommandStrings.TimeoutWaitingForAppHost, timeoutSeconds, CliConfigNames.AppHostStartupTimeout));
            return CliExitCodes.FailedToDotnetRunAppHost;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await CancelAppHostCleanupAsync(runCancellationTokenSource, pendingRun, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await logCaptureCancellationSource.CancelAsync().ConfigureAwait(false);
            await pendingLogCapture.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task<bool> RequestAppHostCleanupStopAsync(
        IAppHostCliBackchannel backchannel,
        Task<int> pendingRun,
        CancellationToken cancellationToken)
    {
        if (!pendingRun.IsCompleted)
        {
            await backchannel.RequestStopAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<IAppHostCliBackchannel?> WaitForBackchannelAsync(
        Task<int> pendingRun,
        TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource,
        OutputCollector? outputCollector,
        int appHostStartupOutputStartIndex,
        long startupStartTimestamp,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        var backchannelTask = backchannelCompletionSource.Task.WaitAsync(GetRemainingStartupTimeout(startupStartTimestamp, startupTimeout), timeProvider, cancellationToken);
        if (await Task.WhenAny(backchannelTask, pendingRun).ConfigureAwait(false) == pendingRun)
        {
            ObserveFaults(backchannelTask);
            DisplayRecentAppHostStartupOutput(outputCollector, appHostStartupOutputStartIndex);
            return null;
        }

        return await backchannelTask.ConfigureAwait(false);
    }

    private async Task<int> WaitForAppHostCleanupShutdownAsync(
        CancellationTokenSource runCancellationTokenSource,
        Task<int> pendingRun,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pendingRun.WaitAsync(s_cleanupShutdownTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await CancelAppHostCleanupAsync(runCancellationTokenSource, pendingRun, cancellationToken).ConfigureAwait(false);
            interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, StopCommandStrings.TimeoutWaitingForAppHostCleanupShutdown, (int)s_cleanupShutdownTimeout.TotalSeconds));
            return CliExitCodes.FailedToDotnetRunAppHost;
        }
    }

    private void DisplayRecentAppHostStartupOutput(OutputCollector? outputCollector, int appHostStartupOutputStartIndex)
    {
        if (outputCollector is null)
        {
            return;
        }

        var lines = outputCollector.GetLines().Skip(appHostStartupOutputStartIndex).ToArray();
        if (lines.Length > 0)
        {
            interactionService.DisplayLines(lines);
        }
    }

    private TimeSpan GetRemainingStartupTimeout(long startupStartTimestamp, TimeSpan startupTimeout)
    {
        var elapsed = timeProvider.GetElapsedTime(startupStartTimestamp);
        return elapsed >= startupTimeout ? TimeSpan.Zero : startupTimeout - elapsed;
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CancelAppHostCleanupAsync(CancellationTokenSource runCancellationTokenSource, Task<int> pendingRun, CancellationToken cancellationToken)
    {
        await runCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await pendingRun.WaitAsync(s_appHostCleanupCancellationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (runCancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (TimeoutException ex)
        {
            logger.LogDebug(ex, "Timed out waiting for AppHost resource cleanup cancellation to complete.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AppHost resource cleanup failed after cancellation.");
        }
    }
}
