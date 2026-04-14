// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Tests;

public class AzurePortalUrlsTests
{
    [Fact]
    public void GetResourceGroupUrl_WithoutTenantId_ReturnsCorrectUrl()
    {
        var url = AzurePortalUrls.GetResourceGroupUrl("sub-123", "rg-myapp");

        Assert.Equal("https://portal.azure.com/#/resource/subscriptions/sub-123/resourceGroups/rg-myapp/overview", url);
    }

    [Fact]
    public void GetResourceGroupUrl_WithTenantId_IncludesTenantInUrl()
    {
        var tenantId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111");
        var url = AzurePortalUrls.GetResourceGroupUrl("sub-123", "rg-myapp", tenantId);

        Assert.Equal("https://portal.azure.com/#@aaaabbbb-cccc-dddd-eeee-ffff00001111/resource/subscriptions/sub-123/resourceGroups/rg-myapp/overview", url);
    }

    [Fact]
    public void GetResourceUrl_WithoutTenantId_ReturnsCorrectUrl()
    {
        var resourceId = "/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.App/containerApps/myapi";
        var url = AzurePortalUrls.GetResourceUrl(resourceId);

        Assert.Equal("https://portal.azure.com/#/resource/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.App/containerApps/myapi", url);
    }

    [Fact]
    public void GetResourceUrl_WithTenantId_IncludesTenantInUrl()
    {
        var tenantId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111");
        var resourceId = "/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.App/containerApps/myapi";
        var url = AzurePortalUrls.GetResourceUrl(resourceId, tenantId);

        Assert.Equal("https://portal.azure.com/#@aaaabbbb-cccc-dddd-eeee-ffff00001111/resource/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.App/containerApps/myapi", url);
    }

    [Fact]
    public void GetResourceUrl_WithAppServiceSite_ReturnsCorrectUrl()
    {
        var resourceId = "/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.Web/sites/mywebapp";
        var url = AzurePortalUrls.GetResourceUrl(resourceId);

        Assert.Contains("Microsoft.Web/sites/mywebapp", url);
    }

    [Fact]
    public void GetResourceUrl_WithAppServiceSlot_ReturnsCorrectUrl()
    {
        var resourceId = "/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.Web/sites/mywebapp/slots/staging";
        var url = AzurePortalUrls.GetResourceUrl(resourceId);

        Assert.Contains("Microsoft.Web/sites/mywebapp/slots/staging", url);
    }

    [Fact]
    public void GetDeploymentUrl_WithComponents_ReturnsUrlEncodedPath()
    {
        var url = AzurePortalUrls.GetDeploymentUrl("/subscriptions/sub-123", "rg-myapp", "deploy-001");

        Assert.StartsWith("https://portal.azure.com/#view/HubsExtension/DeploymentDetailsBlade/~/overview/id/", url);
        Assert.Contains(Uri.EscapeDataString("/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.Resources/deployments/deploy-001"), url);
    }

    [Fact]
    public void GetDeploymentUrl_WithResourceIdentifier_ReturnsUrlEncodedPath()
    {
        var deploymentId = new global::Azure.Core.ResourceIdentifier(
            "/subscriptions/sub-123/resourceGroups/rg-myapp/providers/Microsoft.Resources/deployments/deploy-001");
        var url = AzurePortalUrls.GetDeploymentUrl(deploymentId);

        Assert.StartsWith("https://portal.azure.com/#view/HubsExtension/DeploymentDetailsBlade/~/overview/id/", url);
    }
}
