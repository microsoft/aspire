// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Otlp.Storage;

[DebuggerDisplay("Name = {Name}, ResourceKey = {ResourceKey}, SubscriptionId = {SubscriptionId}")]
public sealed class Subscription : IDisposable
{
    private const int StateNone = 0;
    private const int StateDisposed = 1;

    private static int s_subscriptionId;

    private readonly CallbackThrottler _callbackThrottler;
    private readonly Action _unsubscribe;
    private readonly int _subscriptionId = Interlocked.Increment(ref s_subscriptionId);
    private int _disposed;

    public int SubscriptionId => _subscriptionId;
    public ResourceKey? ResourceKey { get; }
    public SubscriptionType SubscriptionType { get; }
    public string Name { get; }

    public Subscription(string name, ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback, Action unsubscribe, ExecutionContext? executionContext, ILogger logger, TimeSpan minExecuteInterval)
    {
        Name = name;
        ResourceKey = resourceKey;
        SubscriptionType = subscriptionType;
        _callbackThrottler = new CallbackThrottler(name, logger, minExecuteInterval, callback, executionContext);
        _unsubscribe = unsubscribe;
    }

    public void Execute()
    {
        _callbackThrottler.Execute();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, StateDisposed, StateNone) == StateDisposed)
        {
            return;
        }

        _unsubscribe();
        _callbackThrottler.Dispose();
    }
}
