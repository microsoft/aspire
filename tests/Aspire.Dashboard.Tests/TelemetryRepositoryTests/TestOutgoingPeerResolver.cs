// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public sealed class TestOutgoingPeerResolver : IOutgoingPeerResolver, IDisposable
{
    private readonly Func<KeyValuePair<string, string>[], (string? Name, ResourceViewModel? Resource)>? _onResolve;
    private readonly List<Func<Task>> _callbacks;

    public TestOutgoingPeerResolver(Func<KeyValuePair<string, string>[], (string? Name, ResourceViewModel? Resource)>? onResolve = null)
    {
        _onResolve = onResolve;
        _callbacks = new();
    }

    public int SubscriptionCount
    {
        get
        {
            lock (_callbacks)
            {
                return _callbacks.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_callbacks)
        {
            _callbacks.Clear();
        }
    }

    public IDisposable OnPeerChanges(Func<Task> callback)
    {
        lock (_callbacks)
        {
            _callbacks.Add(callback);
        }
        return new Subscription(this, callback);
    }

    public async Task InvokePeerChanges()
    {
        Func<Task>[] callbacks;
        lock (_callbacks)
        {
            callbacks = _callbacks.ToArray();
        }
        foreach (var callback in callbacks)
        {
            await callback();
        }
    }

    public bool TryResolvePeer(KeyValuePair<string, string>[] attributes, out string? name, out ResourceViewModel? matchedResourced)
    {
        if (_onResolve != null)
        {
            (name, matchedResourced) = _onResolve(attributes);
            return (name != null);
        }

        name = "TestPeer";
        matchedResourced = ModelTestHelpers.CreateResource(resourceName: "TestPeer");
        return true;
    }
    private sealed class Subscription(TestOutgoingPeerResolver owner, Func<Task> callback) : IDisposable
    {
        private TestOutgoingPeerResolver? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
            {
                lock (owner._callbacks)
                {
                    owner._callbacks.Remove(callback);
                }
            }
        }
    }
}
