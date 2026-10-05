// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Maintains one live resource health subscription for one discovered AppHost lifetime.
/// </summary>
internal sealed class TrayResourceHealthSubscription : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _watchTask;
    private string? _health;

    public TrayResourceHealthSubscription(
        IAppHostAuxiliaryBackchannel connection,
        TrayAppHost host,
        Action onChanged,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Host = host;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _watchTask = WatchAsync(connection, onChanged, logger, _cancellation.Token);
    }

    public TrayAppHost Host { get; }
    public string? Health => Volatile.Read(ref _health);

    private async Task WatchAsync(
        IAppHostAuxiliaryBackchannel connection,
        Action onChanged,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var watcher = new ResourceSnapshotWatcher(connection, logger, bufferUpdates: true);
            await watcher.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);
            var initial = watcher.CaptureAllResources();
            SetHealth(TrayResourceHealth.Aggregate(initial.Resources), onChanged);
            await foreach (var _ in watcher.WatchResourceSnapshotBatchesAsync(initial.UpdateSequence, cancellationToken).ConfigureAwait(false))
            {
                SetHealth(TrayResourceHealth.Aggregate(watcher.GetResources()), onChanged);
            }
            logger.LogDebug("Resource health stream ended for AppHost PID {Pid}.", Host.AppHostPid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Resource health unavailable for AppHost PID {Pid}.", Host.AppHostPid);
        }
        finally
        {
            // A live process and a cached healthy snapshot do not establish current health
            // after the resource stream ends. A new connection creates a new subscription.
            SetHealth(null, onChanged);
        }
    }

    private void SetHealth(string? health, Action onChanged)
    {
        if (Interlocked.Exchange(ref _health, health) != health)
        {
            onChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await _watchTask.ConfigureAwait(false);
        _cancellation.Dispose();
    }
}
