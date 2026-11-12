// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using System.Diagnostics;

namespace Aspire.Hosting.Native.Diagnostics;

/// <summary>Fans out diagnostics without running consumer code on the producer.</summary>
internal sealed class OperationEventSource : IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<OperationEventSubscription> _subscriptions = [];
    private bool _disposed;

    public OperationEventSubscription Subscribe(int capacity = 1024, ActivityTraceId? traceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new OperationEventSubscription(this, capacity, traceId);
            _subscriptions.Add(subscription);

            return subscription;
        }
    }

    public void Publish(OperationEvent value)
    {
        lock (_gate)
        {
            foreach (var subscription in _subscriptions.ToArray())
            {
                if (subscription.TraceId is { } traceId && !string.Equals(value.TraceId, traceId, StringComparison.Ordinal))
                {
                    continue;
                }
                // Wait mode makes TryWrite report overflow instead of silently
                // dropping events. Only the lagging subscriber loses the feed;
                // its retained prefix drains before the classified error.
                if (!subscription.Writer.TryWrite(value))
                {
                    _subscriptions.Remove(subscription);
                    subscription.Writer.TryComplete(new DiagnosticOverflowException());
                }
            }
        }
    }

    internal void Unsubscribe(OperationEventSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
            subscription.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var subscription in _subscriptions)
            {
                subscription.Writer.TryComplete();
            }
            _subscriptions.Clear();
        }
    }
}

/// <summary>Owns one bounded diagnostic queue; disposal completes its retained prefix.</summary>
internal sealed class OperationEventSubscription : IDisposable
{
    private readonly OperationEventSource _owner;
    private readonly Channel<OperationEvent> _channel;

    internal OperationEventSubscription(OperationEventSource owner, int capacity, ActivityTraceId? traceId)
    {
        _owner = owner;
        TraceId = traceId?.ToString();
        _channel = Channel.CreateBounded<OperationEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<OperationEvent> Reader => _channel.Reader;
    internal string? TraceId { get; }
    internal ChannelWriter<OperationEvent> Writer => _channel.Writer;
    public void Dispose() => _owner.Unsubscribe(this);
}

/// <summary>Reports a diagnostic subscriber exceeding its independent queue capacity.</summary>
internal sealed class DiagnosticOverflowException() : InvalidOperationException("The diagnostic subscriber exceeded its queue capacity.");
