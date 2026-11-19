// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for publishing resource allocation and subscribing to application events.
/// </summary>
public static class DistributedApplicationEventingExtensions
{
    /// <summary>
    /// Completes a resource's endpoint allocation before notifying subscribers.
    /// </summary>
    /// <param name="eventing">The event dispatcher.</param>
    /// <param name="event">The resource allocation notification.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after URL customization and notification dispatch.</returns>
    /// <remarks>
    /// Call this after assigning the resource's physical endpoints. The host awaits URL customization
    /// and finalizes browser origins before dispatching the allocation notification.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    [AspireExportIgnore(Reason = "Endpoint allocation completion is used by .NET resource integrations.")]
    public static Task PublishResourceEndpointsAllocatedAsync(this IDistributedApplicationEventing eventing, ResourceEndpointsAllocatedEvent @event, CancellationToken cancellationToken) =>
        eventing.PublishResourceEndpointsAllocatedAsync(@event, EventDispatchBehavior.BlockingSequential, cancellationToken);

    /// <summary>
    /// Completes a resource's endpoint allocation before dispatching its notification with the specified behavior.
    /// </summary>
    /// <param name="eventing">The event dispatcher.</param>
    /// <param name="event">The resource allocation notification.</param>
    /// <param name="dispatchBehavior">The dispatch behavior for notification subscribers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after URL customization and the requested notification dispatch.</returns>
    /// <remarks>
    /// URL customization is always awaited before notification dispatch, including when subscribers
    /// use a non-blocking dispatch behavior.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    [AspireExportIgnore(Reason = "Endpoint allocation completion is used by .NET resource integrations.")]
    public static Task PublishResourceEndpointsAllocatedAsync(this IDistributedApplicationEventing eventing, ResourceEndpointsAllocatedEvent @event, EventDispatchBehavior dispatchBehavior, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventing);
        ArgumentNullException.ThrowIfNull(@event);

        return @event.Services.GetRequiredService<ApplicationOrchestrator>()
            .PublishEndpointsAllocatedAsync([@event], eventing, dispatchBehavior, cancellationToken);
    }

    /// <summary>
    /// Subscribes a callback to the <see cref="BeforeStartEvent"/> event within the AppHost.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <remarks>If you need to ensure you only subscribe to the event once, see <see cref="Lifecycle.IDistributedApplicationEventingSubscriber"/>.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static T OnBeforeStart<T>(this T builder, Func<BeforeStartEvent, CancellationToken, Task> callback)
        where T : IDistributedApplicationBuilder
        => builder.OnApplicationEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="BeforePublishEvent"/> event within the AppHost.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <remarks>If you need to ensure you only subscribe to the event once, see <see cref="Lifecycle.IDistributedApplicationEventingSubscriber"/>.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static T OnBeforePublish<T>(this T builder, Func<BeforePublishEvent, CancellationToken, Task> callback)
        where T : IDistributedApplicationBuilder
        => builder.OnApplicationEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="AfterPublishEvent"/> event within the AppHost.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <paramref name="builder"/> for chaining.</returns>
    /// <remarks>If you need to ensure you only subscribe to the event once, see <see cref="Lifecycle.IDistributedApplicationEventingSubscriber"/>.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static T OnAfterPublish<T>(this T builder, Func<AfterPublishEvent, CancellationToken, Task> callback)
        where T : IDistributedApplicationBuilder
        => builder.OnApplicationEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="BeforeResourceStartedEvent"/> event within the AppHost.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnBeforeResourceStarted<T>(this IResourceBuilder<T> builder, Func<T, BeforeResourceStartedEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnResourceEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ResourceStoppedEvent"/> event for <paramref name="builder"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnResourceStopped<T>(this IResourceBuilder<T> builder, Func<T, ResourceStoppedEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnResourceEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ConnectionStringAvailableEvent"/> event for <paramref name="builder"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnConnectionStringAvailable<T>(this IResourceBuilder<T> builder, Func<T, ConnectionStringAvailableEvent, CancellationToken, Task> callback)
        where T : IResourceWithConnectionString
        => builder.OnResourceEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="InitializeResourceEvent"/> event for <paramref name="builder"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnInitializeResource<T>(this IResourceBuilder<T> builder, Func<T, InitializeResourceEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnResourceEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ResourceEndpointsAllocatedEvent"/> event for <paramref name="builder"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnResourceEndpointsAllocated<T>(this IResourceBuilder<T> builder, Func<T, ResourceEndpointsAllocatedEvent, CancellationToken, Task> callback)
        where T : IResourceWithEndpoints
        => builder.OnResourceEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ResourceReadyEvent"/> event for <paramref name="builder"/>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnResourceReady<T>(this IResourceBuilder<T> builder, Func<T, ResourceReadyEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnResourceEvent(callback);

    private static T OnApplicationEvent<T, TEvent>(this T builder, Func<TEvent, CancellationToken, Task> callback)
        where T : IDistributedApplicationBuilder
        where TEvent : IDistributedApplicationEvent
    {
        builder.Eventing.Subscribe(callback);
        return builder;
    }

    private static IResourceBuilder<TResource> OnResourceEvent<TResource, TEvent>(this IResourceBuilder<TResource> builder, Func<TResource, TEvent, CancellationToken, Task> callback)
        where TResource : IResource
        where TEvent : IDistributedApplicationResourceEvent
    {
        builder.ApplicationBuilder.Eventing.Subscribe<TEvent>(builder.Resource, (evt, ct) => callback(builder.Resource, evt, ct));
        return builder;
    }
}
