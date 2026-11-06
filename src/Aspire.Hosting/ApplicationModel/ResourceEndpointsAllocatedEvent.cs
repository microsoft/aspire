// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is raised by orchestrators to signal to resources that their endpoints have been allocated.
/// </summary>
/// <remarks>
/// Resources that customize their URLs via a <see cref="ResourceUrlsCallbackAnnotation"/> have their callbacks
/// invoked before event handlers run. Browser URL references contain the finalized values at that point.
/// Endpoint and URL configuration must be registered before this event is published.
/// Browser-facing resource allocators should complete allocation through
/// <see cref="DistributedApplicationEventingExtensions.PublishResourceEndpointsAllocatedAsync(IDistributedApplicationEventing, ResourceEndpointsAllocatedEvent, CancellationToken)"/>
/// so URL customization completes before the notification is dispatched.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class ResourceEndpointsAllocatedEvent(IResource resource, IServiceProvider services) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource { get; } = resource;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services;
}
