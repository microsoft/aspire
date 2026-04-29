// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Shares one browser-level CDP transport across multiple page sessions. Chromium pipe exposes one duplex connection per
// browser process, so pipe-backed hosts use lightweight per-session leases instead of opening one transport per tab.
internal sealed class BrowserLogsCdpConnectionMultiplexer : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly IBrowserLogsCdpConnection _innerConnection;
    private readonly Dictionary<long, Subscription> _subscriptions = [];
    private int _disposed;
    private long _nextSubscriptionId;

    public BrowserLogsCdpConnectionMultiplexer(
        IBrowserLogsCdpTransport transport,
        ILogger<BrowserLogsSessionManager> logger)
        : this(eventHandler => BrowserLogsCdpConnection.Create(transport, eventHandler, logger), logger)
    {
    }

    internal BrowserLogsCdpConnectionMultiplexer(
        Func<Func<BrowserLogsCdpProtocolEvent, ValueTask>, IBrowserLogsCdpConnection> connectionFactory,
        ILogger<BrowserLogsSessionManager> logger)
    {
        _logger = logger;
        _innerConnection = connectionFactory(DispatchEventAsync);
    }

    public Task Completion => _innerConnection.Completion;

    public IBrowserLogsCdpConnection CreateConnection(Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ThrowIfInnerConnectionCompleted();

        var subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
        var subscription = new Subscription(subscriptionId, eventHandler);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ThrowIfInnerConnectionCompleted();
            _subscriptions.Add(subscriptionId, subscription);
        }

        return new LeasedConnection(this, subscription);
    }

    public async ValueTask DisposeAsync()
    {
        Subscription[] subscriptions;

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            subscriptions = [.. _subscriptions.Values];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.SetCompleted();
        }

        await _innerConnection.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask DispatchEventAsync(BrowserLogsCdpProtocolEvent protocolEvent)
    {
        Subscription[] subscriptions;

        lock (_lock)
        {
            subscriptions = [.. _subscriptions.Values];
        }

        foreach (var subscription in subscriptions)
        {
            if (subscription.Completion.IsCompleted)
            {
                continue;
            }

            try
            {
                await subscription.EventHandler(protocolEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var connectionException = new InvalidOperationException("Tracked browser CDP event handler failed.", ex);
                if (TryRemoveSubscription(subscription))
                {
                    subscription.SetException(connectionException);
                }

                _logger.LogError(ex, "Tracked browser CDP event handler failed for subscription '{SubscriptionId}'.", subscription.Id);
            }
        }
    }

    private bool TryRemoveSubscription(Subscription subscription)
    {
        lock (_lock)
        {
            return _subscriptions.Remove(subscription.Id);
        }
    }

    private void ThrowIfInnerConnectionCompleted()
    {
        if (_innerConnection.Completion.IsCompleted)
        {
            throw new InvalidOperationException("Tracked browser CDP pipe is no longer active.");
        }
    }

    private ValueTask DisposeSubscriptionAsync(Subscription subscription)
    {
        if (TryRemoveSubscription(subscription))
        {
            subscription.SetCompleted();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class LeasedConnection(BrowserLogsCdpConnectionMultiplexer owner, Subscription subscription) : IBrowserLogsCdpConnection
    {
        private readonly Task _completion = CompleteWhenLeaseOrInnerConnectionCompletesAsync(owner._innerConnection.Completion, subscription.Completion);
        private int _disposed;

        public Task Completion => _completion;

        public Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.CreateTargetAsync(cancellationToken);
        }

        public Task<BrowserLogsGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.GetTargetsAsync(cancellationToken);
        }

        public Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.AttachToTargetAsync(targetId, cancellationToken);
        }

        public Task<BrowserLogsCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.CloseTargetAsync(targetId, cancellationToken);
        }

        public Task<BrowserLogsCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.EnableTargetDiscoveryAsync(cancellationToken);
        }

        public Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.EnablePageInstrumentationAsync(sessionId, cancellationToken);
        }

        public Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.CaptureScreenshotAsync(sessionId, cancellationToken);
        }

        public Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.NavigateAsync(sessionId, url, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await owner.DisposeSubscriptionAsync(subscription).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (subscription.Completion.IsCompleted)
            {
                throw new InvalidOperationException("Tracked browser CDP connection subscription is no longer active.");
            }
        }

        private static async Task CompleteWhenLeaseOrInnerConnectionCompletesAsync(Task innerCompletion, Task leaseCompletion)
        {
            var completedTask = await Task.WhenAny(innerCompletion, leaseCompletion).ConfigureAwait(false);
            await completedTask.ConfigureAwait(false);
        }
    }

    private sealed class Subscription(long id, Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler)
    {
        private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Id { get; } = id;

        public Func<BrowserLogsCdpProtocolEvent, ValueTask> EventHandler { get; } = eventHandler;

        public Task Completion => _completionSource.Task;

        public void SetCompleted()
        {
            _completionSource.TrySetResult();
        }

        public void SetException(Exception exception)
        {
            _completionSource.TrySetException(exception);
        }
    }
}
