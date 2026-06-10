// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is raised after a resource has stopped.
/// </summary>
/// <param name="resource">The resource that has stopped.</param>
/// <param name="services">The <see cref="IServiceProvider"/> for the app host.</param>
/// <param name="resourceEvent">The <see cref="ResourceEvent"/> containing the current state information.</param>
/// <remarks>
/// This event allows for cleanup or unregistration logic when a resource is stopped by an orchestrator.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class ResourceStoppedEvent(IResource resource, IServiceProvider services, ResourceEvent resourceEvent) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource { get; } = resource;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// The <see cref="ResourceEvent"/> containing the current state information.
    /// </summary>
    public ResourceEvent ResourceEvent { get; } = resourceEvent;
}
