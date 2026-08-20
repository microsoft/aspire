// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURERG001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureResourceGroupTests
{
    [Fact]
    public async Task AddAzureResourceGroupGeneratesSubscriptionScopedBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resourceGroup = builder.AddAzureResourceGroup("app-east-rg", "eastus2");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(resourceGroup.Resource);

        Assert.Contains("targetScope = 'subscription'", bicep);
        Assert.Contains("param location string = 'eastus2'", bicep);
        Assert.Contains("param resourceGroupName string", bicep);
        Assert.Contains("resource app_east_rg 'Microsoft.Resources/resourceGroups@", bicep);
        Assert.Contains("name: resourceGroupName", bicep);
        Assert.Contains("location: location", bicep);
        Assert.Contains("output name string = app_east_rg.name", bicep);
        Assert.Contains("output id string = app_east_rg.id", bicep);
        Assert.True(resourceGroup.Resource.Scope?.IsSubscriptionScope);
        var physicalName = Assert.IsType<ReferenceExpression>(resourceGroup.Resource.ResourceGroupName);
        Assert.EndsWith("-app-east-rg", physicalName.ValueExpression, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithResourceGroupNameUsesParameter()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var name = builder.AddParameter("east-resource-group-name");
        var resourceGroup = builder.AddAzureResourceGroup("east-group", "eastus2")
            .WithResourceGroupName(name);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(resourceGroup.Resource);

        Assert.Equal("{east-resource-group-name.value}", manifest["params"]!["resourceGroupName"]!.GetValue<string>());
        Assert.Contains("param resourceGroupName string", bicep);
        Assert.Contains("name: resourceGroupName", bicep);
    }

    [Fact]
    public void WithResourceGroupRejectsAnotherApplicationBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var otherBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var resourceGroup = otherBuilder.AddAzureResourceGroup("other-rg", "eastus2");
        var storage = builder.AddAzureStorage("storage");

        Assert.Throws<ArgumentException>(() => storage.WithResourceGroup(resourceGroup));
    }

    [Fact]
    public void WithResourceGroupRejectsAnExistingExplicitScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var first = builder.AddAzureResourceGroup("first-rg", "eastus2");
        var second = builder.AddAzureResourceGroup("second-rg", "westus3");
        var storage = builder.AddAzureStorage("storage")
            .WithResourceGroup(first);

        var exception = Assert.Throws<InvalidOperationException>(() => storage.WithResourceGroup(second));

        Assert.Contains("already has an explicit scope", exception.Message);
    }

    [Fact]
    public void WithResourceGroupNameRejectsMutationAfterResourceGroupIsReferenced()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var resourceGroup = builder.AddAzureResourceGroup("east-rg", "eastus2");
        builder.AddAzureStorage("storage")
            .WithResourceGroup(resourceGroup);

        var exception = Assert.Throws<InvalidOperationException>(
            () => resourceGroup.WithResourceGroupName("new-east-rg"));

        Assert.Contains("already referenced", exception.Message);
        Assert.Contains("before calling 'WithResourceGroup'", exception.Message);
    }

    [Fact]
    public void AddAzureResourceGroupDoesNotAddResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var resourceGroup = builder.AddAzureResourceGroup("east-rg", "eastus2");

        Assert.DoesNotContain(resourceGroup.Resource, builder.Resources);
    }

    [Fact]
    public void WithResourceGroupRejectsExistingResourceConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var resourceGroup = builder.AddAzureResourceGroup("east-rg", "eastus2");
        var storage = builder.AddAzureStorage("storage")
            .PublishAsExisting("existing-storage", "existing-rg");

        var exception = Assert.Throws<InvalidOperationException>(
            () => storage.WithResourceGroup(resourceGroup));

        Assert.Contains("already has an explicit scope", exception.Message);
    }
}
