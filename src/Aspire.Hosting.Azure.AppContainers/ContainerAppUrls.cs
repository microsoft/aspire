// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Azure.Core;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure.AppContainers;

internal static class ContainerAppUrls
{
    private const string ContainerAppResourceType = "/providers/Microsoft.App/containerApps/";

    internal static async Task<MarkdownString> GetPortalLinkAsync(AzureContainerAppEnvironmentResource containerAppEnv, string containerAppName, CancellationToken cancellationToken)
    {
        var environmentIdValue = await containerAppEnv.ContainerAppEnvironmentId.GetValueAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Missing container app environment id output for '{containerAppEnv.Name}'.");
        var (subscriptionId, resourceGroupName) = GetSubscriptionAndResourceGroup(environmentIdValue);
        var resourceId = $"{AzurePortalUrls.GetResourceGroupResourceId(subscriptionId, resourceGroupName)}{ContainerAppResourceType}{containerAppName}";

        return AzurePortalUrls.GetResourceLink(resourceId);
    }

    private static (string SubscriptionId, string ResourceGroupName) GetSubscriptionAndResourceGroup(string containerAppEnvironmentId)
    {
        var environmentId = new ResourceIdentifier(containerAppEnvironmentId);
        var subscriptionId = environmentId.SubscriptionId ?? throw new InvalidOperationException($"Container app environment id '{containerAppEnvironmentId}' does not contain a subscription id.");
        var resourceGroupName = environmentId.ResourceGroupName ?? throw new InvalidOperationException($"Container app environment id '{containerAppEnvironmentId}' does not contain a resource group name.");

        return (subscriptionId, resourceGroupName);
    }
}
