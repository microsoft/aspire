// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Watch;

internal sealed class DotnetWatchAppHostSession(
    IGracefulShutdownWindow shutdown,
    TimeSpan shutdownBudget,
    TimeProvider timeProvider,
    ILogger logger)
{
    internal async Task<CommandResult> RunAsync(
        Func<DotnetWatchAppHostGeneration, CancellationToken, Task<CommandResult>> runGeneration,
        Action restarting,
        CancellationToken cancellationToken)
    {
        var backchannelEstablished = 0;
        while (true)
        {
            await using var generation = new DotnetWatchAppHostGeneration(shutdown, shutdownBudget, logger)
            {
                // The foreground launcher exits normally after the first successful startup.
                // Later generations belong to this long-lived CLI, not that departed launcher.
                // Keep the guard armed if a generation was replaced before ever connecting.
                RequiresLauncherMonitoring = Volatile.Read(ref backchannelEstablished) == 0,
                OnBackchannelEstablished = () => Interlocked.Exchange(ref backchannelEstablished, 1)
            };
            using var stopGeneration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = runGeneration(generation, stopGeneration.Token);
            try
            {
                await Task.WhenAny(run, generation.Invalidated).ConfigureAwait(false);
                if (!generation.Invalidated.IsCompleted || !generation.IsEnabled)
                {
                    return await run.ConfigureAwait(false);
                }

                // A compilation can notify multiple metadata handlers. One bounded debounce per
                // generation coalesces them; a new private session rejects all late old messages.
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopGeneration.IsCancellationRequested)
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
                restarting();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CommandResult.Cancelled(CliExitCodes.Success);
            }
            finally
            {
                // Await complete teardown before launching another wrapper. Merely exiting the
                // AppHost leaves the pinned upstream wrapper waiting for the next file edit.
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopGeneration.IsCancellationRequested)
                {
                }
            }
        }
    }
}
