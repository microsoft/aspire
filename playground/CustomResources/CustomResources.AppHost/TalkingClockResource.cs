// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace CustomResources.AppHost;

// Define the custom resource type. It inherits from the base Aspire 'Resource' class.
// This class is primarily a data container; Aspire behavior is added via eventing and extension methods.
public sealed class TalkingClockResource(string name, ClockHandResource tickHand, ClockHandResource tockHand) : Resource(name)
{
    public ClockHandResource TickHand { get; } = tickHand; // The tick hand resource instance.
    public ClockHandResource TockHand { get; } = tockHand; // The tock hand resource instance.
}

public sealed class ClockHandResource(string name) : Resource(name);

// Define Aspire extension methods for adding the TalkingClockResource to the application builder.
// This provides a fluent API for users to add the custom resource.
public static class TalkingClockExtensions
{
    // The main Aspire extension method to add a TalkingClockResource.
    [AspireExportIgnore(Reason = "Playground-only custom resource sample; not part of the supported ATS surface.")]
    public static IResourceBuilder<TalkingClockResource> AddTalkingClock(
        this IDistributedApplicationBuilder builder, // Extends the Aspire application builder.
        string name)                                 // The name for this resource instance.
    {
        // Create a new instance of the TalkingClockResource.
        var tickHandResource = new ClockHandResource(name + "-tick-hand");
        var tockHandResource = new ClockHandResource(name + "-tock-hand");
        var clockResource = new TalkingClockResource(name, tickHandResource, tockHandResource);

        // Add the resource instance to the Aspire application builder and configure it using fluent APIs.
        var clockBuilder = builder.AddResource(clockResource)
            // Use Aspire's ExcludeFromManifest to prevent this resource from being included in deployment manifests.
            .ExcludeFromManifest()
            // Set a URL for the resource, which will be displayed in the Aspire dashboard.
            .WithUrl("https://www.speaking-clock.com/", "Speaking Clock")
            // Use Aspire's WithInitialState to set an initial state snapshot for the resource.
            // This provides initial metadata visible in the Aspire dashboard.
            .WithInitialState(new CustomResourceSnapshot // Aspire type for custom resource state.
            {
                ResourceType = "TalkingClock", // A string identifying the type of resource for Aspire, this shows in the dashboard.
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted, // Use an Aspire well-known state.
                // Add custom properties displayed in the Aspire dashboard's resource details.
                Properties =
                [
                    // Use Aspire's known property key for source information.
                    new(CustomResourceKnownProperties.Source, "Talking Clock")
                ]
            });

        clockBuilder.OnInitializeResource(static async (resource, @event, token) =>
        {
            // This event is published when the resource is initialized.
            // You add custom logic here to establish the lifecycle for your custom resource.

            var log = @event.Logger; // Get the logger for this resource instance.
            var eventing = @event.Eventing; // Get the eventing service for publishing events.
            var notification = @event.Notifications; // Get the notification service for state updates.
            var services = @event.Services; // Get the service provider for dependency injection.

            // Publish an Aspire event indicating that this resource is about to start.
            // Other components could subscribe to this event for pre-start actions.
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), token);
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource.TickHand, services), token);
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource.TockHand, services), token);

            // Log an informational message associated with the resource.
            log.LogInformation("Starting Talking Clock...");

            // Publish an initial state update to the Aspire notification service.
            // This sets the resource's state to 'Running' and records the start time.
            // The Aspire dashboard and other orchestrators observe these state updates.
            await notification.PublishUpdateAsync(resource, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Running // Use an Aspire well-known state.
            });
            await notification.PublishUpdateAsync(resource.TickHand, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = "Waiting on clock tick" // Custom state string for the tick hand.
            });
            await notification.PublishUpdateAsync(resource.TockHand, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = "Waiting on clock tock" // Custom state string for the tock hand.
            });

            // Enter the main loop that runs as long as cancellation is not requested.
            while (!token.IsCancellationRequested)
            {
                // Log the current time, associated with the resource.
                log.LogInformation("The time is {time}", DateTime.UtcNow);

                // Publish a custom state update "Tick" using Aspire's ResourceStateSnapshot.
                // This demonstrates using custom state strings and styles in the Aspire dashboard.
                await notification.PublishUpdateAsync(resource,
                    s => s with { State = new ResourceStateSnapshot("Tick", KnownResourceStateStyles.Success) });
                await notification.PublishUpdateAsync(resource.TickHand,
                    s => s with { State = new ResourceStateSnapshot("On", KnownResourceStateStyles.Success) });
                await notification.PublishUpdateAsync(resource.TockHand,
                    s => s with { State = new ResourceStateSnapshot("Off", KnownResourceStateStyles.Info) });

                await Task.Delay(1000, token);

                // Publish another custom state update "Tock" using Aspire's ResourceStateSnapshot.
                await notification.PublishUpdateAsync(resource,
                    s => s with { State = new ResourceStateSnapshot("Tock", KnownResourceStateStyles.Success) });
                await notification.PublishUpdateAsync(resource.TickHand,
                    s => s with { State = new ResourceStateSnapshot("Off", KnownResourceStateStyles.Info) });
                await notification.PublishUpdateAsync(resource.TockHand,
                    s => s with { State = new ResourceStateSnapshot("On", KnownResourceStateStyles.Success) });

                await Task.Delay(1000, token);
            }
        });

        AddHandResource(tickHandResource);
        AddHandResource(tockHandResource);

        return clockBuilder;

        void AddHandResource(ClockHandResource clockHand)
        {
            builder.AddResource(clockHand)
                .WithParentRelationship(clockBuilder) // Establish a parent-child relationship with the TalkingClockResource.
                .WithInitialState(new()
                {
                    ResourceType = "ClockHand",
                    CreationTimeStamp = DateTime.UtcNow,
                    State = KnownResourceStates.NotStarted,
                    Properties =
                    [
                        new(CustomResourceKnownProperties.Source, "Talking Clock")
                    ]
                });
        }
    }
}
