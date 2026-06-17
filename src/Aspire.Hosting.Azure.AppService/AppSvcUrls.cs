// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Azure.Core;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure;

internal static class AppSvcUrls
{
    private const string SiteResourceType = "/providers/Microsoft.Web/sites/";
    private const string SlotPathPrefix = "/slots/";

    internal static async Task<MarkdownString> GetPortalLinkAsync(AzureAppServiceEnvironmentResource computerEnv, string siteName, string? deploymentSlot, CancellationToken cancellationToken)
    {
        var planIdValue = await computerEnv.PlanIdOutputReference.GetValueAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Missing app service plan id output for '{computerEnv.Name}'.");
        var (subscriptionId, resourceGroupName) = GetSubscriptionAndResourceGroup(planIdValue);
        var resourceId = $"{AzurePortalUrls.GetResourceGroupResourceId(subscriptionId, resourceGroupName)}{SiteResourceType}{siteName}";

        if (!string.IsNullOrWhiteSpace(deploymentSlot))
        {
            resourceId += $"{SlotPathPrefix}{deploymentSlot}";
        }

        return AzurePortalUrls.GetResourceLink(resourceId);
    }

    private static (string SubscriptionId, string ResourceGroupName) GetSubscriptionAndResourceGroup(string appServicePlanId)
    {
        var planId = new ResourceIdentifier(appServicePlanId);
        var subscriptionId = planId.SubscriptionId ?? throw new InvalidOperationException($"App Service plan id '{appServicePlanId}' does not contain a subscription id.");
        var resourceGroupName = planId.ResourceGroupName ?? throw new InvalidOperationException($"App Service plan id '{appServicePlanId}' does not contain a resource group name.");

        return (subscriptionId, resourceGroupName);
    }
}
