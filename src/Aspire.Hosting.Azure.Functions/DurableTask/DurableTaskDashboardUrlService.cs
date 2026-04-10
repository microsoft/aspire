// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Watches resource state updates for Durable Task hub resources and adds dashboard URL
/// annotations when sufficient provisioning data is available.
/// </summary>
internal sealed class DurableTaskDashboardUrlService(
    DistributedApplicationModel appModel,
    ResourceNotificationService notificationService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Track which hub resources already have their dashboard URL annotation.
        var hubsWithUrls = new HashSet<string>();

        await foreach (var resourceEvent in notificationService.WatchAsync(stoppingToken).ConfigureAwait(false))
        {
            if (resourceEvent.Resource is not DurableTaskHubResource hub)
            {
                continue;
            }

            if (hubsWithUrls.Contains(hub.Name))
            {
                continue;
            }

            var url = TryBuildDashboardUrl(hub);
            if (url is null)
            {
                continue;
            }

            // Add the annotation so the URL survives all future state re-publishes.
            hub.Annotations.Add(new ResourceUrlAnnotation { Url = url, DisplayText = "Task Hub Dashboard" });
            hubsWithUrls.Add(hub.Name);

            // Publish an update to make the URL visible immediately.
            await notificationService.PublishUpdateAsync(hub, snapshot => snapshot with
            {
                Urls = [.. snapshot.Urls, new("dashboard", url, false) { DisplayProperties = new() { DisplayName = "Task Hub Dashboard" } }]
            }).ConfigureAwait(false);

            // Stop watching once every hub has a URL.
            if (hubsWithUrls.Count == appModel.Resources.OfType<DurableTaskHubResource>().Count())
            {
                break;
            }
        }
    }

    private static string? TryBuildDashboardUrl(DurableTaskHubResource hub)
    {
        var scheduler = hub.Parent;

        if (scheduler.IsEmulator)
        {
            return TryBuildEmulatorDashboardUrl(hub);
        }

        return TryBuildAzureDashboardUrl(hub, scheduler);
    }

    private static string? TryBuildEmulatorDashboardUrl(DurableTaskHubResource hub)
    {
        var scheduler = hub.Parent;

        // The emulator dashboard endpoint is available once the container's "dashboard" endpoint is allocated.
        if (!scheduler.TryGetEndpoints(out var endpoints))
        {
            return null;
        }

        var dashboardEndpoint = endpoints.FirstOrDefault(e => e.Name == "dashboard");
        if (dashboardEndpoint?.AllocatedEndpoint is not { } allocated)
        {
            return null;
        }

        var hubName = hub.HubName;
        return $"{allocated.UriString}/subscriptions/default/schedulers/default/taskhubs/{hubName}";
    }

    private static string? TryBuildAzureDashboardUrl(DurableTaskHubResource hub, DurableTaskSchedulerResource scheduler)
    {
        if (!scheduler.Outputs.TryGetValue("subscriptionId", out var subObj) ||
            !scheduler.Outputs.TryGetValue("schedulerEndpoint", out var endpointObj) ||
            !scheduler.Outputs.TryGetValue("name", out var nameObj))
        {
            return null;
        }

        var subscriptionId = subObj?.ToString();
        var endpoint = endpointObj?.ToString();
        var schedulerName = nameObj?.ToString();

        if (subscriptionId is null || endpoint is null || schedulerName is null)
        {
            return null;
        }

        scheduler.Outputs.TryGetValue("tenantId", out var tenantObj);
        var tenantId = tenantObj?.ToString();
        var taskHubName = hub.HubName;

        var encodedEndpoint = Uri.EscapeDataString(endpoint);
        var url = $"https://dashboard.durabletask.io/subscriptions/{subscriptionId}/schedulers/{schedulerName}/taskhubs/{taskHubName}?endpoint={encodedEndpoint}";
        if (tenantId is not null)
        {
            url += $"&tenantId={tenantId}";
        }

        return url;
    }
}
