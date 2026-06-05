// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is raised by orchestrators to signal to resources that their endpoints have been allocated.
/// </summary>
/// <remarks>
/// Any resources that customize their URLs via a <see cref="ResourceUrlsCallbackAnnotation"/> will have their callbacks invoked during this event.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class ResourceEndpointsAllocatedEvent(IResource resource, IServiceProvider services) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource { get; } = resource;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services;
}
