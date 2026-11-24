// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Watch;

internal sealed class DotnetWatchAppHostSession(
    IGracefulShutdownWindow shutdown,
    TimeSpan shutdownBudget,
    string homeDirectory,
    TimeProvider timeProvider,
    ILogger logger)
{
    internal async Task<CommandResult> RunAsync(
        Func<DotnetWatchAppHostGeneration, CancellationToken, Task<CommandResult>> runGeneration,
        Action<bool> restarting,
        CancellationToken cancellationToken)
    {
        var backchannelEstablished = 0;
        var isReplacement = false;
        var startedByReload = false;
        RunReloadActivity? pendingReload = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var generation = new DotnetWatchAppHostGeneration(shutdown, shutdownBudget, homeDirectory, logger)
            {
                IsReplacement = isReplacement,
                StartedByReload = startedByReload,
                PendingReload = pendingReload,
                // The foreground detached launcher departs after initial readiness. Never rearm
                // its liveness check once this long-lived child has taken ownership.
                RequiresLauncherMonitoring = Volatile.Read(ref backchannelEstablished) == 0,
                OnBackchannelEstablished = () => Interlocked.Exchange(ref backchannelEstablished, 1)
            };
            // The new generation's run owns the reload that started it.
            pendingReload = null;
            using var stopGeneration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = runGeneration(generation, stopGeneration.Token);
            // The result that ends the session in this generation, if one does.
            CommandResult? ending = null;
            try
            {
                await Task.WhenAny(run, generation.Invalidated, generation.Failure).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (run.IsCompleted || (!generation.IsEnabled && !generation.IsReplacement))
                {
                    ending = await run.ConfigureAwait(false);
                    return ending;
                }

                if (generation.Failure.IsCompletedSuccessfully)
                {
                    var failure = await generation.Failure.ConfigureAwait(false);
                    logger.LogError(failure, "The AppHost watch notification channel failed.");
                    ending = CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost,
                        string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostChannelFailed, failure.Message));
                    return ending;
                }

                // Metadata handlers can notify more than once per edit. Coalesce within this
                // session, then fully drain it before constructing any new model or consumers.
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                var result = await DrainAsync(run, stopGeneration.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is { ExitCode: not CliExitCodes.Success and not CliExitCodes.Cancelled })
                {
                    // The AppHost being replaced stopped with an error. Its processes were cleaned up (a cleanup failure
                    // throws instead), but it may not have finished removing its resources, so no replacement starts.
                    // Its run often ends without a message of its own, so say why the run ends; keep its message too.
                    var stopFailed = string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostReplacedAppHostFailed, result.ExitCode);
                    ending = CommandResult.Failure(result.ExitCode,
                        result.ErrorMessage is { } detail ? $"{stopFailed}{Environment.NewLine}{detail}" : stopFailed);
                    return ending;
                }

                isReplacement = true;
                startedByReload = generation.ReloadRequested;
                pendingReload = generation.RequestedReload;
                restarting(startedByReload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CommandResult.Cancelled(CliExitCodes.Success);
            }
            finally
            {
                var drained = false;
                try
                {
                    // Wrapper exit is not interchangeable with AppHost exit: upstream host can stay
                    // alive after its child disconnects. Await the whole owned run's cleanup, not a
                    // bounded fire-and-forget drain that could overlap the next generation.
                    await stopGeneration.CancelAsync().ConfigureAwait(false);
                    await DrainAsync(run, stopGeneration.Token).ConfigureAwait(false);
                    drained = true;
                }
                finally
                {
                    if (pendingReload is null)
                    {
                        // No replacement will complete a reload that this generation accepted, because the session ends
                        // here. The reload ends as the run does: canceled when the run is stopping or ends without an
                        // error, run_failed when it ends with an error or an exception, for example because this
                        // AppHost failed to shut down. A cleanup exception propagates after the reload is recorded.
                        generation.RequestedReload?.Complete(
                            drained && (cancellationToken.IsCancellationRequested || ending is { ExitCode: CliExitCodes.Success or CliExitCodes.Cancelled })
                                ? TelemetryConstants.RunReloadOutcomes.Canceled
                                : TelemetryConstants.RunReloadOutcomes.RunFailed);
                    }
                }
            }
        }

        // Cancellation can end the loop after a reload was handed over but before its replacement was created.
        pendingReload?.Complete(TelemetryConstants.RunReloadOutcomes.Canceled);
        return CommandResult.Cancelled(CliExitCodes.Success);
    }

    private static async Task<CommandResult?> DrainAsync(Task<CommandResult> run, CancellationToken cancellationToken)
    {
        try
        {
            return await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
