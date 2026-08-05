// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for distributed application eventing operations.
/// </summary>
internal static class EventingExports
{
    /// <summary>
    /// Adds an ATS-friendly eventing subscriber callback to the distributed-application builder.
    /// </summary>
    /// <param name="builder">The distributed-application builder.</param>
    /// <param name="subscribe">The callback that registers the event subscriptions.</param>
    [AspireExport]
    public static void AddEventingSubscriber(this IDistributedApplicationBuilder builder, Func<EventingSubscriberRegistrationContext, Task> subscribe)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subscribe);

        builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(new CallbackEventingSubscriber(subscribe));
    }

    /// <summary>
    /// Attempts to add an ATS-friendly eventing subscriber callback to the distributed-application builder.
    /// </summary>
    /// <param name="builder">The distributed-application builder.</param>
    /// <param name="subscribe">The callback that registers the event subscriptions.</param>
    [AspireExport]
    public static void TryAddEventingSubscriber(this IDistributedApplicationBuilder builder, Func<EventingSubscriberRegistrationContext, Task> subscribe)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(subscribe);

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationEventingSubscriber) &&
                                               descriptor.ImplementationInstance is CallbackEventingSubscriber existing &&
                                               existing.Matches(subscribe)))
        {
            return;
        }

        builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(new CallbackEventingSubscriber(subscribe));
    }

    /// <summary>
    /// Gets the distributed application eventing service from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>The distributed application eventing handle.</returns>
    [AspireExport]
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
    [AspireExport]
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
    [AspireExport]
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
    [AspireExport]
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
    [AspireExport]
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
    [AspireExport]
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
    [AspireExport]
    internal static IResourceBuilder<T> OnResourceReady<T>(this IResourceBuilder<T> builder, Func<ResourceReadyEvent, Task> callback)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return DistributedApplicationEventingExtensions.OnResourceReady(builder, (_, @event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the BeforeStart event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnBeforeStart", MethodName = "onBeforeStart")]
    public static DistributedApplicationEventSubscription OnBeforeStart(this EventingSubscriberRegistrationContext context, Func<BeforeStartEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<BeforeStartEvent>((@event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the BeforePublish event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnBeforePublish", MethodName = "onBeforePublish")]
    public static DistributedApplicationEventSubscription OnBeforePublish(this EventingSubscriberRegistrationContext context, Func<BeforePublishEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<BeforePublishEvent>((@event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the AfterPublish event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnAfterPublish", MethodName = "onAfterPublish")]
    public static DistributedApplicationEventSubscription OnAfterPublish(this EventingSubscriberRegistrationContext context, Func<AfterPublishEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<AfterPublishEvent>((@event, _) => callback(@event));
    }

    /// <summary>
    /// Subscribes to the AfterResourcesCreated event from an eventing subscriber registration context.
    /// </summary>
    /// <param name="context">The eventing subscriber registration context.</param>
    /// <param name="callback">The callback to invoke when the event fires.</param>
    /// <returns>The event subscription.</returns>
    [AspireExport("eventingSubscriberOnAfterResourcesCreated", MethodName = "onAfterResourcesCreated")]
    public static DistributedApplicationEventSubscription OnAfterResourcesCreated(this EventingSubscriberRegistrationContext context, Func<AfterResourcesCreatedEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);

        return context.Eventing.Subscribe<AfterResourcesCreatedEvent>((@event, _) => callback(@event));
    }

    private sealed class CallbackEventingSubscriber(Func<EventingSubscriberRegistrationContext, Task> subscribe) : IDistributedApplicationEventingSubscriber
    {
        public bool Matches(Func<EventingSubscriberRegistrationContext, Task> otherSubscribe)
        {
            return subscribe == otherSubscribe;
        }

        public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            return subscribe(new EventingSubscriberRegistrationContext(eventing, executionContext, cancellationToken));
        }
    }
}

/// <summary>
/// Context passed to ATS-friendly eventing subscriber registrations.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class EventingSubscriberRegistrationContext(
    IDistributedApplicationEventing eventing,
    DistributedApplicationExecutionContext executionContext,
    CancellationToken cancellationToken)
{
    internal IDistributedApplicationEventing Eventing { get; } = eventing;

    /// <summary>
    /// The execution context for the AppHost invocation.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; } = executionContext;

    /// <summary>
    /// The cancellation token associated with the subscriber registration.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
