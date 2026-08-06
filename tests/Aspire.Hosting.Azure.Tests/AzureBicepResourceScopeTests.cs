// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Tests;

public class AzureBicepResourceScopeTests
{
    [Fact]
    public void ResourceGroupReturnsConstructorValue()
    {
        var scope = new AzureBicepResourceScope("test-rg");

        Assert.Equal("test-rg", scope.ResourceGroup);
    }

    [Fact]
    public void ResourceGroupThrowsForSubscriptionScope()
    {
        var scope = AzureBicepResourceScope.ForSubscription("12345678-1234-1234-1234-123456789012");

        var exception = Assert.Throws<InvalidOperationException>(() => scope.ResourceGroup);

        Assert.Equal("The Azure Bicep resource scope does not target a resource group.", exception.Message);
    }

    [Fact]
    public void ResourceGroupThrowsForTenantScope()
    {
        var scope = AzureBicepResourceScope.ForTenant();

        var exception = Assert.Throws<InvalidOperationException>(() => scope.ResourceGroup);

        Assert.Equal("The Azure Bicep resource scope does not target a resource group.", exception.Message);
    }
}
