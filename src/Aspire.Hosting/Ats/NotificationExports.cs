// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for resource notification operations.
/// </summary>
internal static class NotificationExports
{
    /// <summary>
    /// Gets the resource notification service from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>A resource notification service handle.</returns>
    [AspireExport("getResourceNotificationService", Description = "Gets the resource notification service from the service provider")]
    public static ResourceNotificationService GetResourceNotificationService(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<ResourceNotificationService>();
    }

    /// <summary>
    /// Waits for a resource to reach a specified state.
    /// </summary>
    [AspireExport("waitForResourceState", Description = "Waits for a resource to reach a specified state")]
    public static Task WaitForResourceState(
        this ResourceNotificationService notificationService,
        string resourceName,
        string? targetState = null)
    {
        return notificationService.WaitForResourceAsync(resourceName, targetState);
    }

    /// <summary>
    /// Waits for a resource to reach one of the specified states.
    /// </summary>
    [AspireExport("waitForResourceStates", Description = "Waits for a resource to reach one of the specified states")]
    public static Task<string> WaitForResourceStates(
        this ResourceNotificationService notificationService,
        string resourceName,
        string[] targetStates)
    {
        return notificationService.WaitForResourceAsync(resourceName, targetStates);
    }

    /// <summary>
    /// Waits for a resource to become healthy.
    /// </summary>
    [AspireExport("waitForResourceHealthy", Description = "Waits for a resource to become healthy")]
    public static async Task<ResourceEventDto> WaitForResourceHealthy(
        this ResourceNotificationService notificationService,
        string resourceName)
    {
        var resourceEvent = await notificationService.WaitForResourceHealthyAsync(resourceName).ConfigureAwait(false);
        return ResourceEventDto.FromResourceEvent(resourceEvent);
    }

    /// <summary>
    /// Waits for all dependencies of a resource to be ready.
    /// </summary>
    [AspireExport("waitForDependencies", Description = "Waits for all dependencies of a resource to be ready")]
    public static Task WaitForDependencies(
        this ResourceNotificationService notificationService,
        IResourceBuilder<IResource> resource)
    {
        return notificationService.WaitForDependenciesAsync(resource.Resource, CancellationToken.None);
    }

    /// <summary>
    /// Tries to get the current state of a resource.
    /// </summary>
    [AspireExport("tryGetResourceState", Description = "Tries to get the current state of a resource")]
    public static ResourceEventDto? TryGetResourceState(
        this ResourceNotificationService notificationService,
        string resourceName)
    {
        if (notificationService.TryGetCurrentState(resourceName, out var resourceEvent))
        {
            return ResourceEventDto.FromResourceEvent(resourceEvent);
        }
        return null;
    }

    /// <summary>
    /// Publishes an update for a resource's state.
    /// </summary>
    [AspireExport("publishResourceUpdate", Description = "Publishes an update for a resource's state")]
    public static Task PublishResourceUpdate(
        this ResourceNotificationService notificationService,
        IResourceBuilder<IResource> resource,
        string? state = null,
        string? stateStyle = null)
    {
        return notificationService.PublishUpdateAsync(resource.Resource, snapshot =>
        {
            if (state != null)
            {
                snapshot = snapshot with { State = new ResourceStateSnapshot(state, stateStyle) };
            }
            return snapshot;
        });
    }
}

/// <summary>
/// DTO for resource events returned from notification service.
/// </summary>
[AspireDto]
internal sealed class ResourceEventDto
{
    /// <summary>
    /// The resource name.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// The unique resource ID.
    /// </summary>
    public required string ResourceId { get; init; }

    /// <summary>
    /// The current state text.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// The state style (e.g., "success", "warn", "error").
    /// </summary>
    public string? StateStyle { get; init; }

    /// <summary>
    /// The health status of the resource.
    /// </summary>
    public string? HealthStatus { get; init; }

    /// <summary>
    /// The exit code if the resource has exited.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Creates a DTO from a ResourceEvent.
    /// </summary>
    internal static ResourceEventDto FromResourceEvent(ResourceEvent resourceEvent)
    {
        return new ResourceEventDto
        {
            ResourceName = resourceEvent.Resource.Name,
            ResourceId = resourceEvent.ResourceId,
            State = resourceEvent.Snapshot.State?.Text,
            StateStyle = resourceEvent.Snapshot.State?.Style,
            HealthStatus = resourceEvent.Snapshot.HealthStatus?.ToString(),
            ExitCode = resourceEvent.Snapshot.ExitCode
        };
    }
}
