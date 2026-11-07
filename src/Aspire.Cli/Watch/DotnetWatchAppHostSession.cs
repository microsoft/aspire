// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
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
        Action restarting,
        CancellationToken cancellationToken)
    {
        var backchannelEstablished = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var generation = new DotnetWatchAppHostGeneration(shutdown, shutdownBudget, homeDirectory, logger)
            {
                // The foreground detached launcher departs after initial readiness. Never rearm
                // its liveness check once this long-lived child has taken ownership.
                RequiresLauncherMonitoring = Volatile.Read(ref backchannelEstablished) == 0,
                OnBackchannelEstablished = () => Interlocked.Exchange(ref backchannelEstablished, 1)
            };
            using var stopGeneration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = runGeneration(generation, stopGeneration.Token);
            try
            {
                await Task.WhenAny(run, generation.Invalidated, generation.Failure).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (run.IsCompleted || !generation.IsEnabled)
                {
                    return await run.ConfigureAwait(false);
                }

                if (generation.Failure.IsCompletedSuccessfully)
                {
                    var failure = await generation.Failure.ConfigureAwait(false);
                    logger.LogError(failure, "The AppHost watch notification channel failed.");
                    return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost,
                        string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostChannelFailed, failure.Message));
                }

                // Metadata handlers can notify more than once per edit. Coalesce within this
                // session, then fully drain it before constructing any new model or consumers.
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                var result = await DrainAsync(run, stopGeneration.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is { ExitCode: not CliExitCodes.Success and not CliExitCodes.Cancelled })
                {
                    return result;
                }

                restarting();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CommandResult.Cancelled(CliExitCodes.Success);
            }
            finally
            {
                // Wrapper exit is not interchangeable with AppHost exit: upstream host can stay
                // alive after its child disconnects. Await the whole owned run's cleanup, not a
                // bounded fire-and-forget drain that could overlap the next generation.
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                await DrainAsync(run, stopGeneration.Token).ConfigureAwait(false);
            }
        }

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
