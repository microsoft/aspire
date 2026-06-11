// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui.Annotations;

namespace Aspire.Hosting.Maui.Lifecycle;

/// <summary>
/// Event subscriber that sets the "Unsupported" state for MAUI platform resources 
/// marked with <see cref="UnsupportedPlatformAnnotation"/>.
/// </summary>
/// <remarks>
/// This subscriber handles all MAUI platform resources (Windows, Android, iOS, Mac Catalyst)
/// by checking for the <see cref="IMauiPlatformResource"/> marker interface.
/// </remarks>
/// <param name="notificationService">The notification service for publishing resource state updates.</param>
internal sealed class UnsupportedPlatformEventSubscriber(ResourceNotificationService notificationService) : IDistributedApplicationEventingSubscriber
{
    /// <inheritdoc/>
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<AfterResourcesCreatedEvent>(async (@event, ct) =>
        {
            // Find all MAUI platform resources with the UnsupportedPlatformAnnotation
            foreach (var resource in @event.Model.Resources)
            {
                if (resource is IMauiPlatformResource && 
                    resource.TryGetLastAnnotation<UnsupportedPlatformAnnotation>(out var annotation))
                {
                    // Set the state to "Unsupported" with a warning style and the reason
                    await notificationService.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot($"Unsupported: {annotation.Reason}", KnownResourceStateStyles.Warn)
                    }).ConfigureAwait(false);
                }
            }
        });

        return Task.CompletedTask;
    }
}
