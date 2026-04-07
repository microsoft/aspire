// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for distributed application eventing operations.
/// </summary>
internal static class EventingExports
{
    /// <summary>
    /// Gets the distributed application eventing service from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>The distributed application eventing handle.</returns>
    [AspireExport(Description = "Gets the distributed application eventing service from the service provider")]
    internal static IDistributedApplicationEventing GetEventing(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<IDistributedApplicationEventing>();
    }

    /// <summary>
    /// Subscribes to the BeforeResourceStarted event.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Subscribes to the BeforeResourceStarted event")]
    internal static IResourceBuilder<T> OnBeforeResourceStarted<T>(this IResourceBuilder<T> builder, Func<BeforeResourceStartedEvent, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnBeforeResourceStarted(builder, (_, @event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the ResourceStopped event.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Subscribes to the ResourceStopped event")]
    internal static IResourceBuilder<T> OnResourceStopped<T>(this IResourceBuilder<T> builder, Func<ResourceStoppedEvent, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnResourceStopped(builder, (_, @event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the ConnectionStringAvailable event.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Subscribes to the ConnectionStringAvailable event")]
    internal static IResourceBuilder<T> OnConnectionStringAvailable<T>(this IResourceBuilder<T> builder, Func<ConnectionStringAvailableEvent, Task> callback)
        where T : IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnConnectionStringAvailable(builder, (_, @event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the InitializeResource event.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Subscribes to the InitializeResource event")]
    internal static IResourceBuilder<T> OnInitializeResource<T>(this IResourceBuilder<T> builder, Func<InitializeResourceEvent, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnInitializeResource(builder, (_, @event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the ResourceEndpointsAllocated event.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Subscribes to the ResourceEndpointsAllocated event")]
    internal static IResourceBuilder<T> OnResourceEndpointsAllocated<T>(this IResourceBuilder<T> builder, Func<ResourceEndpointsAllocatedEvent, Task> callback)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnResourceEndpointsAllocated(builder, (_, @event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the ResourceReady event.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport(Description = "Subscribes to the ResourceReady event")]
    internal static IResourceBuilder<T> OnResourceReady<T>(this IResourceBuilder<T> builder, Func<ResourceReadyEvent, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnResourceReady(builder, (_, @event, _) => callback(@event));
    }
}
