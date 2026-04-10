// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.Tests;

public class ExistingAzureExtensionsResourceTests
{
    [Fact]
    public void RunAsExistingInPublishModeNoOps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .RunAsExisting(nameParameter, resourceGroupParameter);

        Assert.False(serviceBus.Resource.IsExisting());
    }

    [Fact]
    public void RunAsExistingInRunModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .RunAsExisting(nameParameter, resourceGroupParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
    }

    [Fact]
    public void MultipleRunAsExistingInRunModeUsesLast()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var nameParameter1 = builder.AddParameter("name1", "existingName");
        var resourceGroupParameter1 = builder.AddParameter("resourceGroup1", "existingResourceGroup");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .RunAsExisting(nameParameter, resourceGroupParameter)
            .RunAsExisting(nameParameter1, resourceGroupParameter1);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name1", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup1", existingResourceGroupParameter.Name);
    }

    [Fact]
    public void PublishAsExistingInPublishModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .PublishAsExisting(nameParameter, resourceGroupParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
    }

    [Fact]
    public void MultiplePublishAsExistingInRunModeUsesLast()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var nameParameter1 = builder.AddParameter("name1", "existingName");
        var resourceGroupParameter1 = builder.AddParameter("resourceGroup1", "existingResourceGroup");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .PublishAsExisting(nameParameter, resourceGroupParameter)
            .PublishAsExisting(nameParameter1, resourceGroupParameter1);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name1", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup1", existingResourceGroupParameter.Name);
    }

    public static TheoryData<Func<string, string, string, IResourceBuilder<IAzureResource>>> AsExistingMethodsWithString =>
        new()
        {
            { (name, resourceGroup, type) => TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).AddAzureServiceBus(type).RunAsExisting(name, resourceGroup) },
            { (name, resourceGroup, type) => TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).AddAzureServiceBus(type).PublishAsExisting(name, resourceGroup) }
        };

    [Theory]
    [MemberData(nameof(AsExistingMethodsWithString))]
    public void CanCallAsExistingWithStringArguments(Func<string, string, string, IResourceBuilder<IAzureResource>> runAsExisting)
    {
        var serviceBus = runAsExisting("existingName", "existingResourceGroup", "sb");

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        Assert.Equal("existingName", existingAzureResourceAnnotation.Name);
        Assert.Equal("existingResourceGroup", existingAzureResourceAnnotation.ResourceGroup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsExistingInBothModesWorks(bool isPublishMode)
    {
        using var builder = TestDistributedApplicationBuilder.Create(isPublishMode ? DistributedApplicationOperation.Publish : DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExisting(nameParameter, resourceGroupParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        Assert.Null(existingAzureResourceAnnotation.Subscription);
        Assert.Null(existingAzureResourceAnnotation.Tenant);
    }

    // ====== Subscription Support Tests ======

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsExistingInSubscriptionInBothModesWorks(bool isPublishMode)
    {
        using var builder = TestDistributedApplicationBuilder.Create(isPublishMode ? DistributedApplicationOperation.Publish : DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExistingInSubscription(nameParameter, subscriptionParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        Assert.Null(existingAzureResourceAnnotation.ResourceGroup);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
        Assert.Null(existingAzureResourceAnnotation.Tenant);
    }

    [Fact]
    public void CanCallAsExistingInSubscriptionWithStringArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExistingInSubscription("existingName", "12345678-1234-1234-1234-123456789012");

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        Assert.Equal("existingName", existingAzureResourceAnnotation.Name);
        Assert.Null(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("12345678-1234-1234-1234-123456789012", existingAzureResourceAnnotation.Subscription);
        Assert.Null(existingAzureResourceAnnotation.Tenant);
    }

    [Fact]
    public void RunAsExistingInSubscriptionInRunModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .RunAsExistingInSubscription(nameParameter, subscriptionParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        Assert.Null(existingAzureResourceAnnotation.ResourceGroup);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
    }

    [Fact]
    public void PublishAsExistingInSubscriptionInPublishModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("name", "existingName");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .PublishAsExistingInSubscription(nameParameter, subscriptionParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        Assert.Null(existingAzureResourceAnnotation.ResourceGroup);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
    }

    // ====== Resource Group with Subscription Support Tests ======

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsExistingInResourceGroupInBothModesWorks(bool isPublishMode)
    {
        using var builder = TestDistributedApplicationBuilder.Create(isPublishMode ? DistributedApplicationOperation.Publish : DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExistingInResourceGroup(nameParameter, resourceGroupParameter, subscriptionParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
        Assert.Null(existingAzureResourceAnnotation.Tenant);
    }

    [Fact]
    public void CanCallAsExistingInResourceGroupWithStringArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExistingInResourceGroup("existingName", "existingResourceGroup", "12345678-1234-1234-1234-123456789012");

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        Assert.Equal("existingName", existingAzureResourceAnnotation.Name);
        Assert.Equal("existingResourceGroup", existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("12345678-1234-1234-1234-123456789012", existingAzureResourceAnnotation.Subscription);
        Assert.Null(existingAzureResourceAnnotation.Tenant);
    }

    [Fact]
    public void RunAsExistingInResourceGroupInRunModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .RunAsExistingInResourceGroup(nameParameter, resourceGroupParameter, subscriptionParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
    }

    [Fact]
    public void PublishAsExistingInResourceGroupInPublishModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .PublishAsExistingInResourceGroup(nameParameter, resourceGroupParameter, subscriptionParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
    }

    // ====== Tenant Support Tests ======

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsExistingWithTenantInBothModesWorks(bool isPublishMode)
    {
        using var builder = TestDistributedApplicationBuilder.Create(isPublishMode ? DistributedApplicationOperation.Publish : DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");
        var tenantParameter = builder.AddParameter("tenant", "87654321-4321-4321-4321-210987654321");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExistingWithTenant(nameParameter, resourceGroupParameter, subscriptionParameter, tenantParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
        var existingTenantParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Tenant);
        Assert.Equal("tenant", existingTenantParameter.Name);
    }

    [Fact]
    public void CanCallAsExistingWithTenantWithStringArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var serviceBus = builder.AddAzureServiceBus("sb")
            .AsExistingWithTenant("existingName", "existingResourceGroup", "12345678-1234-1234-1234-123456789012", "87654321-4321-4321-4321-210987654321");

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        Assert.Equal("existingName", existingAzureResourceAnnotation.Name);
        Assert.Equal("existingResourceGroup", existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("12345678-1234-1234-1234-123456789012", existingAzureResourceAnnotation.Subscription);
        Assert.Equal("87654321-4321-4321-4321-210987654321", existingAzureResourceAnnotation.Tenant);
    }

    [Fact]
    public void RunAsExistingWithTenantInRunModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");
        var tenantParameter = builder.AddParameter("tenant", "87654321-4321-4321-4321-210987654321");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .RunAsExistingWithTenant(nameParameter, resourceGroupParameter, subscriptionParameter, tenantParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
        var existingTenantParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Tenant);
        Assert.Equal("tenant", existingTenantParameter.Name);
    }

    [Fact]
    public void PublishAsExistingWithTenantInPublishModeWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var nameParameter = builder.AddParameter("name", "existingName");
        var resourceGroupParameter = builder.AddParameter("resourceGroup", "existingResourceGroup");
        var subscriptionParameter = builder.AddParameter("subscription", "12345678-1234-1234-1234-123456789012");
        var tenantParameter = builder.AddParameter("tenant", "87654321-4321-4321-4321-210987654321");

        var serviceBus = builder.AddAzureServiceBus("sb")
            .PublishAsExistingWithTenant(nameParameter, resourceGroupParameter, subscriptionParameter, tenantParameter);

        Assert.True(serviceBus.Resource.TryGetLastAnnotation<ExistingAzureResourceAnnotation>(out var existingAzureResourceAnnotation));
        var existingNameParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Name);
        Assert.Equal("name", existingNameParameter.Name);
        var existingResourceGroupParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.ResourceGroup);
        Assert.Equal("resourceGroup", existingResourceGroupParameter.Name);
        var existingSubscriptionParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Subscription);
        Assert.Equal("subscription", existingSubscriptionParameter.Name);
        var existingTenantParameter = Assert.IsType<ParameterResource>(existingAzureResourceAnnotation.Tenant);
        Assert.Equal("tenant", existingTenantParameter.Name);
    }
}
