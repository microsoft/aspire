// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Event that is raised when a resource transitions to a ready state after starting.
/// </summary>
/// <param name="resource">The resource that is in a ready state.</param>
/// <param name="services">The service provider for the app host.</param>
/// <remarks>
/// <para>
/// This event is fired once for the resource's initial ready transition and again after a full
/// resource restart or when a restarted resource instance transitions to ready while other replicas
/// remain running.
/// </para>
/// <para>
/// A handler that does not observe cancellation can outlive the resource instance that triggered it,
/// so handlers for different generations can overlap. Handlers should be idempotent and safe to
/// execute concurrently.
/// </para>
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class ResourceReadyEvent(IResource resource, IServiceProvider services) : IDistributedApplicationResourceEvent
{
    /// <summary>
    /// The resource that is in a healthy state.
    /// </summary>
    public IResource Resource => resource;

    /// <inheritdoc />
    public IServiceProvider Services => services;
}
