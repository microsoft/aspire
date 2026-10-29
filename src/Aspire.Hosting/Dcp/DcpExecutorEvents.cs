// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Dcp;

internal record ResourceStatus(string? State, DateTime? StartupTimestamp, DateTime? FinishedTimestamp);
internal record OnEndpointsAllocatedContext(CancellationToken CancellationToken);

/// <summary>
/// Raised for a start attempt before connection-string availability is published, allowing the orchestrator
/// to report startup blockers even when resolving a connection string or property waits for a parameter.
/// This does not imply that parameter values or launch configuration are resolved, or that the resource is running.
/// </summary>
internal record OnResourceStartingContext(CancellationToken CancellationToken, string ResourceType, IResource Resource, string? DcpResourceName);

/// <summary>
/// Raised after connection-string event handlers complete and before the ensuing DCP creation or start-patching
/// operation. Handlers are awaited and may delay or cancel startup. The orchestrator publishes
/// BeforeResourceStartedEvent here, preserving its ordering after ConnectionStringAvailableEvent.
/// This is not an immutability boundary: launch configuration can still change. DcpResourceName is null for a grouped start.
/// </summary>
internal record OnResourceBeforeStartContext(CancellationToken CancellationToken, IResource Resource, string? DcpResourceName);

internal record OnConnectionStringAvailableContext(CancellationToken CancellationToken, IResource Resource);
internal record OnResourcesPreparedContext(CancellationToken CancellationToken);
internal record OnResourceChangedContext(CancellationToken CancellationToken, string ResourceType, IResource Resource, string DcpResourceName, ResourceStatus Status, string? PreviousState, Func<CustomResourceSnapshot, CustomResourceSnapshot> UpdateSnapshot);
internal record OnResourceFailedToStartContext(CancellationToken CancellationToken, string ResourceType, IResource Resource, string? DcpResourceName, string? ErrorMessage = null);

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
