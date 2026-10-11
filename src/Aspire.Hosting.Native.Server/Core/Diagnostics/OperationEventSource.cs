// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Diagnostics;

/// <summary>Publishes typed diagnostic events without reflection-based payload discovery.</summary>
internal sealed class OperationEventSource : IObservable<OperationEvent>
{
    private ImmutableArray<Subscription> _subscriptions = [];

    public bool HasObservers => !_subscriptions.IsEmpty;

    public IDisposable Subscribe(IObserver<OperationEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var subscription = new Subscription(this, observer);
        ImmutableInterlocked.Update(ref _subscriptions, subscriptions => subscriptions.Add(subscription));

        return subscription;
    }

    public void Publish(OperationEvent value)
    {
        // DiagnosticSource.Write discovers payload properties reflectively and
        // is annotated for trimming. Keep this feed typed for native consumers:
        // https://learn.microsoft.com/dotnet/api/system.diagnostics.diagnosticsource.write
        foreach (var subscription in _subscriptions)
        {
            subscription.Observer.OnNext(value);
        }
    }

    private sealed class Subscription(OperationEventSource owner, IObserver<OperationEvent> observer) : IDisposable
    {
        public IObserver<OperationEvent> Observer { get; } = observer;

        public void Dispose() => ImmutableInterlocked.Update(ref owner._subscriptions, subscriptions => subscriptions.Remove(this));
    }
}
