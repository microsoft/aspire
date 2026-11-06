// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
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
            Task<CommandResult>? observedRun = null;
            Task<CommandResult> ObserveRunAsync() => observedRun ??= ObserveCoreAsync();
            async Task<CommandResult> ObserveCoreAsync()
            {
                try
                {
                    return await run.ConfigureAwait(false);
                }
                catch (Exception exception) when (DcpForkShutdownException.ContainsFailure(exception))
                {
                    logger.LogError(exception, "Watch generation retirement failed; no replacement will be launched.");
                    return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost, RunCommandStrings.WatchDcpShutdownFailed);
                }
                catch (OperationCanceledException) when (stopGeneration.IsCancellationRequested)
                {
                    return CommandResult.Cancelled(CliExitCodes.Success);
                }
            }
            try
            {
                await Task.WhenAny(run, generation.Invalidated).ConfigureAwait(false);
                if (!generation.Invalidated.IsCompleted || !generation.IsEnabled)
                {
                    return await ObserveRunAsync().ConfigureAwait(false);
                }

                // A compilation can notify multiple metadata handlers. One bounded debounce per
                // generation coalesces them; a new private session rejects all late old messages.
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken).ConfigureAwait(false);
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                var result = await ObserveRunAsync().ConfigureAwait(false);
                if (result.ExitCode != CliExitCodes.Success)
                {
                    return result;
                }
                cancellationToken.ThrowIfCancellationRequested();
                restarting();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                return await ObserveRunAsync().ConfigureAwait(false);
            }
            finally
            {
                // Await complete teardown before launching another wrapper. Merely exiting the
                // AppHost leaves the pinned upstream wrapper waiting for the next file edit.
                await stopGeneration.CancelAsync().ConfigureAwait(false);
                await ObserveRunAsync().ConfigureAwait(false);
            }
        }
    }
}
