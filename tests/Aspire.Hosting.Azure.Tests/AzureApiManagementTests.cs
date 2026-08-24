// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAPIM001
#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.Utils;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureApiManagementTests
{
    [Fact]
    public void AddAzureApiManagementCreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });

        Assert.Equal("apim", apim.Resource.Name);
        Assert.Equal(AzureApiManagementSku.Developer, apim.Resource.Options.Sku);
        Assert.Equal(1, apim.Resource.Options.Capacity);
        Assert.Equal("gatewayUrl", apim.Resource.GatewayUrl.Name);
        Assert.Equal("id", apim.Resource.Id.Name);
        Assert.Equal("principalId", apim.Resource.PrincipalId.Name);
    }

    [Fact]
    public void ApiManagementResourcesAreNotAddedInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var backend = builder.AddProject<Project>("backend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });

        var api = apim.AddApi("catalog-api", backend, "catalog");
        var operation = api.AddOperation("get-product", "get", "/products/{id}");

        Assert.Equal(["backend", "backend-rebuilder"], builder.Resources.Select(resource => resource.Name));
        Assert.Single(apim.Resource.Apis);
        Assert.Single(api.Resource.Operations);
        Assert.Equal("get-product", operation.Resource.Name);
    }

    [Theory]
    [InlineData(AzureApiManagementSku.Consumption, 1)]
    [InlineData(AzureApiManagementSku.Developer, 0)]
    [InlineData(AzureApiManagementSku.Basic, 3)]
    [InlineData(AzureApiManagementSku.BasicV2, 11)]
    [InlineData(AzureApiManagementSku.Standard, 5)]
    [InlineData(AzureApiManagementSku.StandardV2, 11)]
    [InlineData(AzureApiManagementSku.Premium, 13)]
    [InlineData(AzureApiManagementSku.PremiumV2, 31)]
    public void AddAzureApiManagementRejectsInvalidCapacity(AzureApiManagementSku sku, int capacity)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var exception = Assert.Throws<ArgumentException>(() =>
            builder.AddAzureApiManagement("apim", new()
            {
                PublisherEmail = "api-owners@example.com",
                Sku = sku,
                Capacity = capacity,
            }));

        Assert.Equal("capacity", exception.ParamName);
    }

    [Fact]
    public void AddApiAndOperationCreateParentedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddProject<Project>("backend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });

        var api = apim.AddApi("catalog-api", backend, "/catalog/", "Catalog", subscriptionRequired: true);
        var operation = api.AddOperation("get-product", "get", "/products/{id}", "Get product");

        Assert.Equal("catalog", api.Resource.Path);
        Assert.Equal("Catalog", api.Resource.DisplayName);
        Assert.True(api.Resource.SubscriptionRequired);
        Assert.Same(apim.Resource, api.Resource.Parent);
        Assert.Equal("GET", operation.Resource.Method);
        Assert.Same(api.Resource, operation.Resource.Parent);
        Assert.Contains(api.Resource, builder.Resources);
        Assert.Contains(operation.Resource, builder.Resources);
    }

    [Fact]
    public void PolicyHelpersValidateAndPreserveScopeSemantics()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddProject<Project>("backend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });
        var api = apim.AddApi("catalog-api", backend, "catalog");
        var operation = api.AddOperation("get-products", "get", "/products");

        apim.WithInboundPolicy("<set-header name=\"x-service\" exists-action=\"override\"><value>service</value></set-header>");
        api.WithInboundPolicy("<rate-limit calls=\"10\" renewal-period=\"60\" />");
        operation.WithPolicy("<policies><inbound><base /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>");

        Assert.Single(apim.Resource.InboundPolicyStatements);
        Assert.Single(api.Resource.InboundPolicyStatements);
        Assert.NotNull(operation.Resource.PolicyXml);
        Assert.Throws<ArgumentException>(() => apim.WithInboundPolicy("<policies />"));
        Assert.Throws<ArgumentException>(() => api.WithPolicy("<rate-limit calls=\"10\" renewal-period=\"60\" />"));
    }

    [Fact]
    public void AzureApiManagementResourceImplementsPrivateEndpointTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });

        var target = Assert.IsAssignableFrom<IAzurePrivateEndpointTarget>(apim.Resource);

        Assert.Equal(["Gateway"], target.GetPrivateLinkGroupIds());
        Assert.Equal(["privatelink.azure-api.net"], target.GetPrivateDnsZoneNames());
    }

    [Fact]
    public void WithClassicVirtualNetworkRejectsUnsupportedSku()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("apim-subnet", "10.0.0.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            Sku = AzureApiManagementSku.StandardV2,
        });

        Assert.Throws<InvalidOperationException>(() =>
            apim.WithClassicVirtualNetwork(subnet, AzureApiManagementVirtualNetworkMode.External));
    }

    [Fact]
    public void WithClassicVirtualNetworkRejectsDelegatedSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("apim-subnet", "10.0.0.0/24")
            .WithServiceDelegation(AzureSubnetServiceDelegations.ContainerAppEnvironments);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });

        Assert.Throws<InvalidOperationException>(() =>
            apim.WithClassicVirtualNetwork(subnet, AzureApiManagementVirtualNetworkMode.External));
    }

    [Fact]
    public async Task ClassicVirtualNetworkCannotBeCombinedWithPrivateEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var apimSubnet = vnet.AddSubnet("apim-subnet", "10.0.0.0/24");
        var privateEndpointSubnet = vnet.AddSubnet("private-endpoint-subnet", "10.0.1.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        }).WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
        privateEndpointSubnet.AddPrivateEndpoint(apim);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("cannot combine a private endpoint with classic virtual network injection", exception.Message);
    }

    [Fact]
    public async Task AddAzureApiManagementWithApiAndPoliciesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddProject<Project>("catalog-backend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
            Capacity = 2,
        }).WithInboundPolicy(
            "<set-header name=\"x-gateway\" exists-action=\"override\"><value>apim</value></set-header>");
        var api = apim.AddApi("catalog-api", backend, "/catalog", "Catalog API", subscriptionRequired: true)
            .WithInboundPolicy("<rate-limit calls=\"100\" renewal-period=\"60\" />");
        api.AddOperation("get-product", "get", "/products/{id}", "Get product")
            .WithInboundPolicy("<set-query-parameter name=\"source\" exists-action=\"override\"><value>apim</value></set-query-parameter>");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AddAzureApiManagementWithClassicVirtualNetworkGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("apim-subnet", "10.0.0.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            Sku = AzureApiManagementSku.Premium,
            Capacity = 2,
        }).WithClassicVirtualNetwork(subnet, AzureApiManagementVirtualNetworkMode.Internal);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        await Verify(bicep, "bicep");
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
