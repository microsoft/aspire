// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Helpers for generating Azure portal URLs.
/// </summary>
internal static class AzurePortalUrls
{
    private const string PortalDeploymentOverviewUrl = "https://portal.azure.com/#view/HubsExtension/DeploymentDetailsBlade/~/overview/id";

    /// <summary>
    /// Gets the Azure portal URL for a resource group overview page.
    /// </summary>
    internal static string GetResourceGroupUrl(string subscriptionId, string resourceGroupName, Guid? tenantId = null)
    {
        var tenantSegment = tenantId.HasValue ? $"#@{tenantId.Value}" : "#";
        return $"https://portal.azure.com/{tenantSegment}/resource/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/overview";
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
    internal static string GetDeploymentUrl(global::Azure.Core.ResourceIdentifier deploymentId)
    {
        return $"{PortalDeploymentOverviewUrl}/{Uri.EscapeDataString(deploymentId.ToString())}";
    }
}
