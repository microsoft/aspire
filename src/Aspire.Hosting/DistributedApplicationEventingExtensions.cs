// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for subscribing to <see cref="IDistributedApplicationResourceEvent"/> events on resources.
/// </summary>
public static class DistributedApplicationEventingExtensions
{
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
        => builder.OnEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ResourceStoppedEvent"/> event within the AppHost.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnResourceStopped<T>(this IResourceBuilder<T> builder, Func<T, ResourceStoppedEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ConnectionStringAvailableEvent"/> event within the AppHost.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnConnectionStringAvailable<T>(this IResourceBuilder<T> builder, Func<T, ConnectionStringAvailableEvent, CancellationToken, Task> callback)
        where T : IResourceWithConnectionString
        => builder.OnEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="InitializeResourceEvent"/> event within the AppHost.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnInitializeResource<T>(this IResourceBuilder<T> builder, Func<T, InitializeResourceEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ResourceEndpointsAllocatedEvent"/> event within the AppHost.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnResourceEndpointsAllocated<T>(this IResourceBuilder<T> builder, Func<T, ResourceEndpointsAllocatedEvent, CancellationToken, Task> callback)
        where T : IResourceWithEndpoints
        => builder.OnEvent(callback);

    /// <summary>
    /// Subscribes a callback to the <see cref="ResourceReadyEvent"/> event within the AppHost.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "Complex generic delegates with event/CancellationToken types — not ATS-compatible.")]
    public static IResourceBuilder<T> OnResourceReady<T>(this IResourceBuilder<T> builder, Func<T, ResourceReadyEvent, CancellationToken, Task> callback)
        where T : IResource
        => builder.OnEvent(callback);

    private static IResourceBuilder<TResource> OnEvent<TResource, TEvent>(this IResourceBuilder<TResource> builder, Func<TResource, TEvent, CancellationToken, Task> callback)
        where TResource : IResource
        where TEvent : IDistributedApplicationResourceEvent
    {
        builder.ApplicationBuilder.Eventing.Subscribe<TEvent>(builder.Resource, (evt, ct) => callback(builder.Resource, evt, ct));
        return builder;
    }
}
