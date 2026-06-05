// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Eventing;

/// <summary>
/// Represents a subscription to an event that is published during the lifecycle of the AppHost.
/// </summary>
/// <param name="callback">Callback to invoke when the event is published.</param>
[AspireExport]
public class DistributedApplicationEventSubscription(Func<IDistributedApplicationEvent, CancellationToken, Task> callback)
{
    /// <summary>
    /// The callback to be executed when the event is published.
    /// </summary>
    public Func<IDistributedApplicationEvent, CancellationToken, Task> Callback { get; } = callback;
}

/// <summary>
/// Represents a subscription to an event that is published during the lifecycle of the AppHost for a specific resource.
/// </summary>
[AspireExport]
public class DistributedApplicationResourceEventSubscription(IResource? resource, Func<IDistributedApplicationResourceEvent, CancellationToken, Task> callback)
    : DistributedApplicationEventSubscription((@event, cancellationToken) => callback((IDistributedApplicationResourceEvent)@event, cancellationToken))
{
    /// <summary>
    /// Resource associated with this subscription.
    /// </summary>
    public IResource? Resource { get; } = resource;
}
