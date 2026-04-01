// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dcp;

internal record ResourceStatus(string? State, DateTime? StartupTimestamp, DateTime? FinishedTimestamp);
internal record OnEndpointsAllocatedContext(CancellationToken CancellationToken);
internal record OnResourceStartingContext(CancellationToken CancellationToken, string ResourceType, IResource Resource, string? DcpResourceName);
internal record OnConnectionStringAvailableContext(CancellationToken CancellationToken, IResource Resource);
internal record OnResourcesPreparedContext(CancellationToken CancellationToken);
internal record OnResourceChangedContext(CancellationToken CancellationToken, string ResourceType, IResource Resource, string DcpResourceName, ResourceStatus Status, Func<CustomResourceSnapshot, CustomResourceSnapshot> UpdateSnapshot);
internal record OnResourceFailedToStartContext(CancellationToken CancellationToken, string ResourceType, IResource Resource, string? DcpResourceName);

internal sealed class DcpExecutorEvents
{
    private readonly ConcurrentDictionary<Type, Func<object, Task>> _eventSubscriptionListLookup = new();

    public void Subscribe<T>(Func<T, Task> callback) where T : notnull
    {
        var success = _eventSubscriptionListLookup.TryAdd(typeof(T), (obj) => callback((T)obj));
        if (!success)
        {
            throw new InvalidOperationException($"Failed to add subscription for event type {typeof(T)} because a subscription already exists.");
        }
    }

    public async Task PublishAsync<T>(T context) where T : notnull
    {
        if (_eventSubscriptionListLookup.TryGetValue(typeof(T), out var callback))
        {
            await callback(context).ConfigureAwait(false);
        }
    }
}
