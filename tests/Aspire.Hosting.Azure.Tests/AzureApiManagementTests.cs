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
    public async Task AddAzureApiManagementWithInternalContainerAppBackendGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var containerAppsSubnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        var apimSubnet = vnet.AddSubnet("apim-subnet", "10.0.2.0/24");
        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(containerAppsSubnet)
            .WithInternalLoadBalancer(vnet);
        var backend = builder.AddProject<Project>("catalog-backend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            Sku = AzureApiManagementSku.Premium,
        }).WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
        apim.AddApi("catalog-api", backend, "catalog");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("param catalog_api_url string", bicep);
        Assert.Contains("catalog-backend.internal", manifest.ToJsonString());
    }

    [Fact]
    public async Task InternalBackendRequiresApiManagementVirtualNetworkConnectivity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddProject<Project>("catalog-backend", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });
        apim.AddApi("catalog-api", backend, "catalog");

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("exposes only internal HTTP endpoints", exception.Message);
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

    [Fact]
    public async Task AddOpenAIApiWithFoundryBackendsGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var primaryFoundry = builder.AddFoundry("foundry-primary");
        var primary = primaryFoundry.AddDeployment("chat-primary", "gpt-5-mini", "2025-08-07", "OpenAI");
        var secondaryFoundry = builder.AddFoundry("foundry-secondary");
        var secondary = secondaryFoundry.AddDeployment("chat-secondary", "gpt-5-mini", "2025-08-07", "OpenAI");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            Sku = AzureApiManagementSku.StandardV2,
        });
        apim.AddOpenAIApi("openai-api", "openai")
            .WithFoundryBackend(primary, priority: 1, weight: 3)
            .WithFoundryBackend(secondary, priority: 2, weight: 1);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("openai/deployments/chat-primary", manifest.ToJsonString());
        Assert.Contains("openai/deployments/chat-secondary", manifest.ToJsonString());
        Assert.Contains("foundry-primary.outputs.endpoint", manifest.ToJsonString());
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AzureOpenAIBackendUsesAccountEndpointAndDeduplicatesRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var account = builder.AddAzureOpenAI("openai");
        var deployment = account.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });
        apim.AddOpenAIApi("chat-api", "chat")
            .WithAzureOpenAIBackend(deployment);
        apim.AddOpenAIApi("responses-api", "responses")
            .WithAzureOpenAIBackend(deployment);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);
        var manifestJson = manifest.ToJsonString();

        Assert.Contains("openai.outputs.endpoint", manifestJson);
        Assert.Contains("openai/deployments/chat", manifestJson);
        Assert.Single(bicep.Split("Microsoft.Authorization/roleAssignments@", StringSplitOptions.None).Skip(1));
    }

    [Fact]
    public void FoundryBackendMustUseOpenAIFormat()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var foundry = builder.AddFoundry("foundry");
        var model = foundry.AddDeployment("embedding", "embedding-model", "1", "Microsoft");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
        });
        var api = apim.AddOpenAIApi("openai-api", "openai");

        var exception = Assert.Throws<InvalidOperationException>(() => api.WithFoundryBackend(model));

        Assert.Contains("Only OpenAI-format deployments", exception.Message);
    }

    [Fact]
    public async Task InternalContainerAppEnvironmentGeneratesPrivateDns()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var subnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(subnet)
            .WithInternalLoadBalancer(vnet);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var privateDns = builder.Resources
            .OfType<AzureProvisioningResource>()
            .Single(resource => resource.Name == "env-private-dns");

        var (_, environmentBicep) = await GetManifestWithBicep(environment.Resource);
        var (_, privateDnsBicep) = await GetManifestWithBicep(privateDns);

        Assert.Contains("internal: true", environmentBicep);
        Assert.Contains("output AZURE_CONTAINER_APPS_ENVIRONMENT_STATIC_IP string", environmentBicep);
        Assert.Contains("Microsoft.Network/privateDnsZones@2024-06-01", privateDnsBicep);
        Assert.Contains("name: '*'", privateDnsBicep);
        Assert.Contains("ipv4Address:", privateDnsBicep);
        Assert.Contains("Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01", privateDnsBicep);
    }

    [Fact]
    public void InternalContainerAppEnvironmentRejectsDifferentVirtualNetwork()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var otherVnet = builder.AddAzureVirtualNetwork("other-vnet");
        var subnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(subnet);

        var exception = Assert.Throws<InvalidOperationException>(
            () => environment.WithInternalLoadBalancer(otherVnet));

        Assert.Contains("must belong to virtual network 'other-vnet'", exception.Message);
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
