// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Azure.Core;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Helpers for generating Azure portal URLs.
/// </summary>
internal static class AzurePortalUrls
{
    private const string PortalRootUrl = "https://portal.azure.com";
    private const string PortalDeploymentOverviewUrl = "https://portal.azure.com/#view/HubsExtension/DeploymentDetailsBlade/~/overview/id";
    private const string AzurePortalLinkText = "Azure Portal";

    /// <summary>
    /// Gets the Azure portal URL for a resource group overview page.
    /// </summary>
    internal static string GetResourceGroupUrl(string subscriptionId, string resourceGroupName, Guid? tenantId = null)
    {
        return $"{PortalRootUrl}/{GetTenantSegment(tenantId)}/resource/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/overview";
    }

    /// <summary>
    /// Gets a markdown link to a resource group overview page in the Azure portal.
    /// </summary>
    internal static MarkdownString GetResourceGroupLink(string subscriptionId, string resourceGroupName, Guid? tenantId = null)
    {
        return GetMarkdownLink(resourceGroupName, GetResourceGroupUrl(subscriptionId, resourceGroupName, tenantId));
    }

    /// <summary>
    /// Gets the Azure portal URL for a resource group's deployments page.
    /// </summary>
    internal static string GetResourceGroupDeploymentsUrl(string subscriptionId, string resourceGroupName, Guid? tenantId = null)
    {
        return $"{PortalRootUrl}/{GetTenantSegment(tenantId)}/resource/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/deployments";
    }

    /// <summary>
    /// Gets a markdown link to a resource group's deployments page in the Azure portal.
    /// </summary>
    internal static MarkdownString GetResourceGroupDeploymentsLink(string subscriptionId, string resourceGroupName, Guid? tenantId = null)
    {
        return GetMarkdownLink(AzurePortalLinkText, GetResourceGroupDeploymentsUrl(subscriptionId, resourceGroupName, tenantId));
    }

    /// <summary>
    /// Gets the ARM resource ID for a resource group.
    /// </summary>
    internal static string GetResourceGroupResourceId(string subscriptionId, string resourceGroupName)
    {
        return $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}";
    }

    /// <summary>
    /// Gets the Azure portal URL for a resource overview page.
    /// </summary>
    internal static string GetResourceUrl(string resourceId, Guid? tenantId = null)
    {
        return $"{PortalRootUrl}/{GetTenantSegment(tenantId)}/resource{resourceId}/overview";
    }

    /// <summary>
    /// Gets the Azure portal URL for a resource overview page using a full resource ID.
    /// </summary>
    internal static string GetResourceUrl(ResourceIdentifier resourceId, Guid? tenantId = null)
    {
        return GetResourceUrl(resourceId.ToString(), tenantId);
    }

    /// <summary>
    /// Gets a markdown link to a resource overview page in the Azure portal.
    /// </summary>
    internal static MarkdownString GetResourceLink(string resourceId, string linkText = AzurePortalLinkText, Guid? tenantId = null)
    {
        return GetMarkdownLink(linkText, GetResourceUrl(resourceId, tenantId));
    }

    /// <summary>
    /// Gets a markdown link to a resource overview page in the Azure portal using a full resource ID.
    /// </summary>
    internal static MarkdownString GetResourceLink(ResourceIdentifier resourceId, string linkText = AzurePortalLinkText, Guid? tenantId = null)
    {
        return GetMarkdownLink(linkText, GetResourceUrl(resourceId, tenantId));
    }

    /// <summary>
    /// Gets the Azure portal URL for a deployment details page.
    /// </summary>
    internal static string GetDeploymentUrl(string subscriptionResourceId, string resourceGroupName, string deploymentName)
    {
        var path = $"{subscriptionResourceId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/{deploymentName}";
        var encodedPath = Uri.EscapeDataString(path);
        return $"{PortalDeploymentOverviewUrl}/{encodedPath}";
    }

    /// <summary>
    /// Gets the Azure portal URL for a deployment details page using a full deployment resource ID.
    /// </summary>
    internal static string GetDeploymentUrl(ResourceIdentifier deploymentId)
    {
        return $"{PortalDeploymentOverviewUrl}/{Uri.EscapeDataString(deploymentId.ToString())}";
    }

    private static string GetTenantSegment(Guid? tenantId)
    {
        return tenantId.HasValue ? $"#@{tenantId.Value}" : "#";
    }

    private static MarkdownString GetMarkdownLink(string linkText, string url)
    {
        return new($"[{linkText}]({url})");
    }
}
