// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// This event is raised by orchestrators before they have started a new resource.
/// </summary>
/// <param name="resource">The resource that is being created.</param>
/// <param name="services">The <see cref="IServiceProvider"/> for the app host.</param>
/// <remarks>
/// Resources that are created by orchestrators may not yet be ready to handle requests.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class BeforeResourceStartedEvent(IResource resource, IServiceProvider services) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource { get; } = resource;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// The id of the specific resource instance that is being started, which is the same value as
    /// <see cref="ResourceEvent.ResourceId"/> for that instance.
    /// </summary>
    /// <remarks>
    /// <see langword="null" /> when the event covers the resource as a whole rather than one instance of it: either
    /// because the resource has no DCP instances (it drives its own startup), or because a replicated resource is
    /// being started as a group, in which case every replica shares this one event.
    /// </remarks>
    internal string? ResourceInstanceId { get; init; }
}
