// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Shares one browser-level CDP transport across multiple page sessions. Chromium pipe exposes one duplex connection per
// browser process, so pipe-backed hosts use lightweight per-session leases instead of opening one transport per tab.
internal sealed class BrowserCdpConnectionMultiplexer : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly ILogger<BrowserSessionManager> _logger;
    private readonly IBrowserCdpConnection _innerConnection;
    private readonly Dictionary<long, Subscription> _subscriptions = [];
    private int _disposed;
    private long _nextSubscriptionId;

    public BrowserCdpConnectionMultiplexer(
        IBrowserCdpTransport transport,
        ILogger<BrowserSessionManager> logger)
        : this(eventHandler => BrowserCdpConnection.Create(transport, eventHandler, logger), logger)
    {
    }

    internal BrowserCdpConnectionMultiplexer(
        Func<Func<BrowserCdpProtocolEvent, ValueTask>, IBrowserCdpConnection> connectionFactory,
        ILogger<BrowserSessionManager> logger)
    {
        _logger = logger;
        _innerConnection = connectionFactory(DispatchEventAsync);
    }

    public Task Completion => _innerConnection.Completion;

    public IBrowserCdpConnection CreateConnection(Func<BrowserCdpProtocolEvent, ValueTask> eventHandler)
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

    private async ValueTask DispatchEventAsync(BrowserCdpProtocolEvent protocolEvent)
    {
        Subscription[] subscriptions;

        lock (_lock)
        {
            // Snapshot subscriptions before invoking handlers. Handlers can dispose their page session, and holding the
            // registry lock across arbitrary event callbacks would deadlock that disposal path.
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
                // A failing page-session handler means that lease can no longer make reliable routing decisions. Remove
                // only that subscription; the shared pipe and other page sessions may still be healthy.
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

    private sealed class LeasedConnection(BrowserCdpConnectionMultiplexer owner, Subscription subscription) : IBrowserCdpConnection
    {
        private readonly Task _completion = CompleteWhenLeaseOrInnerConnectionCompletesAsync(owner._innerConnection.Completion, subscription.Completion);
        private int _disposed;

        public Task Completion => _completion;

        public Task<BrowserCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.CreateTargetAsync(cancellationToken);
        }

        public Task<BrowserGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.GetTargetsAsync(cancellationToken);
        }

        public Task<BrowserAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.AttachToTargetAsync(targetId, cancellationToken);
        }

        public Task<BrowserCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.CloseTargetAsync(targetId, cancellationToken);
        }

        public Task<BrowserCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.EnableTargetDiscoveryAsync(cancellationToken);
        }

        public Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.EnablePageInstrumentationAsync(sessionId, cancellationToken);
        }

        public Task<BrowserCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.CaptureScreenshotAsync(sessionId, options, cancellationToken);
        }

        public Task<BrowserNavigateResult> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.NavigateAsync(sessionId, url, cancellationToken);
        }

        public Task<BrowserRuntimeEvaluateResult> EvaluateAsync(string sessionId, string expression, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.EvaluateAsync(sessionId, expression, timeout, cancellationToken);
        }

        public Task<string> SendRawCommandAsync(string? sessionId, string method, string? parametersJson, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return owner._innerConnection.SendRawCommandAsync(sessionId, method, parametersJson, cancellationToken);
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

    private sealed class Subscription(long id, Func<BrowserCdpProtocolEvent, ValueTask> eventHandler)
    {
        private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Id { get; } = id;

        public Func<BrowserCdpProtocolEvent, ValueTask> EventHandler { get; } = eventHandler;

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
