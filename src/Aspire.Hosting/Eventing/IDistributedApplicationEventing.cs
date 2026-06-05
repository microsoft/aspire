// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Eventing;

/// <summary>
/// Supports publishing and subscribing to events which are executed during the AppHost lifecycle.
/// </summary>
[AspireExport(ExposeMethods = true)]
public interface IDistributedApplicationEventing
{
    /// <summary>
    /// Subscribes a callback to a specific event type within the AppHost.
    /// </summary>
    /// <typeparam name="T">The type of the event.</typeparam>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>A subscription instance which can be used to unsubscribe </returns>
    DistributedApplicationEventSubscription Subscribe<T>(Func<T, CancellationToken, Task> callback) where T : IDistributedApplicationEvent;

    /// <summary>
    /// Subscribes a callback to a specific event type 
    /// </summary>
    /// <typeparam name="T">The type of the event.</typeparam>
    /// <param name="resource">The resource instance associated with the event.</param>
    /// <param name="callback">A callback to handle the event.</param>
    /// <returns>A subscription instance which can be used to unsubscribe.</returns>
    DistributedApplicationEventSubscription Subscribe<T>(IResource resource, Func<T, CancellationToken, Task> callback) where T : IDistributedApplicationResourceEvent;

    /// <summary>
    /// Unsubscribe from an event.
    /// </summary>
    /// <param name="subscription">The specific subscription to unsubscribe.</param>
    void Unsubscribe(DistributedApplicationEventSubscription subscription);

    /// <summary>
    /// Publishes an event to all subscribes of the specific event type.
    /// </summary>
    /// <typeparam name="T">The type of the event</typeparam>
    /// <param name="event">The event.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that can be awaited.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Cancellation token")]
    Task PublishAsync<T>(T @event, CancellationToken cancellationToken = default) where T : IDistributedApplicationEvent;

    /// <summary>
    /// Publishes an event to all subscribes of the specific event type.
    /// </summary>
    /// <typeparam name="T">The type of the event</typeparam>
    /// <param name="event">The event.</param>
    /// <param name="dispatchBehavior">The dispatch behavior for the event.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that can be awaited.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Cancellation token")]
    Task PublishAsync<T>(T @event, EventDispatchBehavior dispatchBehavior, CancellationToken cancellationToken = default) where T : IDistributedApplicationEvent;
}
