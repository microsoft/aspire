// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable AZPROVISION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Azure.Provisioning.ApiManagement;
using Azure.Provisioning.Network;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureApiManagementTests(ITestOutputHelper output)
{
    [Fact]
    public void AddAzureApiManagementCreatesResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        Assert.Equal("apim", apim.Resource.Name);
        Assert.Equal("Contoso APIs", apim.Resource.Options.PublisherName);
        Assert.Equal(AzureApiManagementSku.Developer, apim.Resource.Options.Sku);
        Assert.Equal(1, apim.Resource.Options.Capacity);
        Assert.Equal("gatewayUrl", apim.Resource.GatewayUrl.Name);
        Assert.Equal("id", apim.Resource.Id.Name);
        Assert.Equal("principalId", apim.Resource.PrincipalId.Name);
        Assert.Same(apim.Resource.Identity, apim.Resource.PrincipalId.Resource);
        Assert.Contains(apim.Resource.Identity, apim.Resource.References);
    }

    [Fact]
    public void ApiManagementResourcesAreNotAddedInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var resources = builder.Resources.ToArray();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        var api = apim.AddApi("catalog-api", backend, "catalog");
        var operation = api.AddOperation("get-product", "get", "/products/{id}");

        Assert.Equal(resources, builder.Resources);
        Assert.Single(apim.Resource.Apis);
        Assert.Single(api.Resource.Operations);
        Assert.Equal("get-product", operation.Resource.Name);
    }

    [Fact]
    public void InternalContainerAppEnvironmentIsIgnoredInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var subnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(subnet)
            .WithInternalLoadBalancer(vnet);

        Assert.Equal("env", environment.Resource.Name);
        Assert.Equal(["azure-environment"], builder.Resources.Select(resource => resource.Name));
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
                PublisherName = "Contoso APIs",
                Sku = sku,
                Capacity = capacity,
            }));

        Assert.Equal("capacity", exception.ParamName);
    }

    [Fact]
    public void ConsumptionSkuDefaultsToZeroCapacity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Consumption,
        });

        Assert.Equal(0, apim.Resource.Options.Capacity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AddAzureApiManagementRejectsMissingPublisherName(string? publisherName)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            builder.AddAzureApiManagement("apim", new()
            {
                PublisherEmail = "api-owners@example.com",
                PublisherName = publisherName!,
            }));

        Assert.Equal("options.PublisherName", exception.ParamName);
    }

    [Fact]
    public void AddAzureApiManagementRejectsPublisherFieldsOverMaximumLength()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var emailException = Assert.Throws<ArgumentException>(() =>
            builder.AddAzureApiManagement("email-apim", new()
            {
                PublisherEmail = new string('a', 101),
                PublisherName = "Contoso APIs",
            }));
        var nameException = Assert.Throws<ArgumentException>(() =>
            builder.AddAzureApiManagement("name-apim", new()
            {
                PublisherEmail = "api-owners@example.com",
                PublisherName = new string('a', 101),
            }));

        Assert.Contains("publisher email address", emailException.Message);
        Assert.Contains("publisher name", nameException.Message);
    }

    [Fact]
    public void AddApiAndOperationCreateParentedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
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
    public void ApiAndOperationSupportDistinctPhysicalNamesAndSecureDefaults()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        var api = apim.AddApi("catalog-api", backend, "catalog", apiName: "physical-api");
        var operation = api.AddOperation(
            "get-product",
            "get",
            "/products/{id}",
            operationName: "physical-operation");

        Assert.Equal("physical-api", api.Resource.ApiName);
        Assert.True(api.Resource.SubscriptionRequired);
        Assert.Equal("physical-operation", operation.Resource.OperationName);
    }

    [Fact]
    public void ApiAndOperationValidatePhysicalIdentifierConstraints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        var longApiName = new string('a', 256);
        var api = apim.AddApi("catalog-api", backend, "catalog", apiName: longApiName);

        Assert.Equal(longApiName, api.Resource.ApiName);
        Assert.Throws<ArgumentException>(() =>
            apim.AddApi("invalid-api", backend, "invalid", apiName: "invalid?api"));
        Assert.Throws<ArgumentException>(() =>
            api.AddOperation("invalid-operation", "GET", "/", operationName: new string('o', 81)));
    }

    [Fact]
    public void ApiAndOperationValidatePropertyLengthConstraints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var api = apim.AddApi("catalog-api", backend, "catalog");

        Assert.Throws<ArgumentException>(() =>
            apim.AddApi("long-path-api", backend, new string('p', 401)));
        Assert.Throws<ArgumentException>(() =>
            apim.AddApi("long-display-api", backend, "long-display", new string('d', 301)));
        Assert.Throws<ArgumentException>(() =>
            apim.AddApi("blank-display-api", backend, "blank-display", " "));
        Assert.Throws<ArgumentException>(() =>
            api.AddOperation("long-template", "GET", new string('u', 1001)));
        Assert.Throws<ArgumentException>(() =>
            api.AddOperation("long-display", "GET", "/", new string('d', 301)));
        Assert.Throws<ArgumentException>(() =>
            api.AddOperation("blank-display", "GET", "/", " "));
    }

    [Fact]
    public void ApiPhysicalIdentifiersAndPathsMustBeUnique()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddApi("catalog-api", backend, "/catalog/", apiName: "physical-api");

        var duplicateName = Assert.Throws<InvalidOperationException>(
            () => apim.AddOpenAIApi("openai-api", "openai", apiName: "PHYSICAL-API"));
        var duplicatePath = Assert.Throws<InvalidOperationException>(
            () => apim.AddOpenAIApi("other-api", "CATALOG"));

        Assert.Contains("physical identifier 'PHYSICAL-API'", duplicateName.Message);
        Assert.Contains("path 'CATALOG'", duplicatePath.Message);
        Assert.Single(apim.Resource.Apis);
    }

    [Fact]
    public async Task ComputeBackendPhysicalNamesMustNotConflictWithDeclaredBackends()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var target = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddBackend("declared-backend", ReferenceExpression.Create($"https://example.com"), backendName: "catalog_apiBackend");
        apim.AddApi("catalog-api", target, "catalog");

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("generates backend physical name 'catalog_apiBackend'", exception.Message);
    }

    [Fact]
    public void OperationPhysicalIdentifiersMustBeUniqueAndCannotUseGeneratedProxyNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var api = apim.AddApi("catalog-api", backend, "catalog");
        api.AddOperation("get-product", "GET", "/products/{id}", operationName: "physical-operation");

        var duplicateName = Assert.Throws<InvalidOperationException>(
            () => api.AddOperation("get-other-product", "GET", "/products/other", operationName: "PHYSICAL-OPERATION"));
        var reservedName = Assert.Throws<ArgumentException>(
            () => api.AddOperation("custom-proxy", "GET", "/proxy", operationName: "PrOxY"));
        var reservedMethodName = Assert.Throws<ArgumentException>(
            () => api.AddOperation("custom-proxy-get", "GET", "/proxy", operationName: "PrOxY-GeT"));
        var wildcardMethod = Assert.Throws<ArgumentException>(
            () => api.AddOperation("wildcard-operation", "*", "/{*path}"));

        Assert.Contains("physical identifier 'PHYSICAL-OPERATION'", duplicateName.Message);
        Assert.Contains("'PrOxY' is reserved", reservedName.Message);
        Assert.Contains("'PrOxY-GeT' is reserved", reservedMethodName.Message);
        Assert.Contains("wildcard HTTP method", wildcardMethod.Message);
        Assert.Single(api.Resource.Operations);
    }

    [Theory]
    [InlineData("/products/{id}", "/products/{productId}")]
    [InlineData("/products/{id}", "/products/{id}")]
    [InlineData("/products/{id}/{*path}", "/products/{key}/{*rest}")]
    [InlineData("/products/{id}?expand={fields}&version={v}", "/products/{key}?version={ver}&expand={selection}")]
    public void OperationRoutesRejectEquivalentSignatures(string first, string second)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var api = apim.AddApi("catalog-api", "catalog");
        api.AddOperation("first", "GET", first);
        var resourceCount = builder.Resources.Count;

        var exception = Assert.Throws<InvalidOperationException>(() => api.AddOperation("second", "get", second));

        Assert.Contains("HTTP method and URL template must be unique", exception.Message);
        Assert.Single(api.Resource.Operations);
        Assert.Equal(resourceCount, builder.Resources.Count);
    }

    [Theory]
    [InlineData("GET", "/{*other}")]
    [InlineData("post", "/{*path}")]
    [InlineData("GET", "/")]
    public async Task OperationRoutesRejectGeneratedProxyConflicts(string method, string template)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var backend = apim.AddBackend("backend", ReferenceExpression.Create($"https://example.com"));
        var api = apim.AddApi("catalog-api", "catalog").WithBackend(backend);
        api.AddOperation("custom", method, template);

        // The conflict is checked against the API's final configuration during infrastructure
        // generation, not at AddOperation call time, so that call order relative to
        // WithOpenApiDocument does not change the validation outcome.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => GetManifestWithBicep(apim.Resource));

        Assert.Contains("conflicts with a generated proxy", exception.Message);
    }

    [Theory]
    [InlineData("GET", "/{*other}")]
    [InlineData("post", "/{*path}")]
    [InlineData("GET", "/")]
    public async Task OperationRoutesRejectGeneratedProxyConflictsRegardlessOfAddOperationCallOrder(string method, string template)
    {
        using var temporaryWorkspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(temporaryWorkspace.Path, "catalog.json");
        File.WriteAllText(documentPath, """{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}""");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var backend = apim.AddBackend("backend", ReferenceExpression.Create($"https://example.com"));
        var api = apim.AddApi("catalog-api", "catalog").WithBackend(backend);
        api.AddOperation("custom", method, template);
        api.WithOpenApiDocument(documentPath);

        // Once an OpenAPI import is configured, no proxy routes are generated, so the same
        // route that previously conflicted must now be allowed, regardless of whether
        // WithOpenApiDocument was called before or after AddOperation.
        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Single(api.Resource.Operations);
    }

    [Fact]
    public void OperationRoutesAllowDifferentMethodsLiteralsAndApis()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var api = apim.AddApi("catalog-api", "catalog");
        api.AddOperation("get-product", "GET", "/products/{id}");
        api.AddOperation("post-product", "POST", "/products/{id}");
        api.AddOperation("get-category", "GET", "/categories/{id}");
        apim.AddApi("other-api", "other").AddOperation("other-product", "GET", "/products/{id}");

        Assert.Equal(3, api.Resource.Operations.Count);
        Assert.Single(apim.Resource.Apis[1].Operations);
    }

    [Fact]
    public void ImportedApiAllowsExplicitRootAndWildcardRoutes()
    {
        using var temporaryWorkspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(temporaryWorkspace.Path, "catalog.json");
        File.WriteAllText(documentPath, """{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}""");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var api = apim.AddApi("catalog-api", "catalog").WithOpenApiDocument(documentPath);
        api.AddOperation("root", "GET", "/");
        api.AddOperation("wildcard", "GET", "/{*path}");

        Assert.Equal(2, api.Resource.Operations.Count);
    }

    [Fact]
    public void PolicyHelpersValidateAndPreserveScopeSemantics()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
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
        Assert.Throws<InvalidOperationException>(() => operation.WithInboundPolicy("<rate-limit calls=\"10\" renewal-period=\"60\" />"));
        Assert.Throws<InvalidOperationException>(() => apim.WithPolicy("<policies><inbound /></policies>"));
    }

    [Fact]
    public void AzureApiManagementResourceImplementsPrivateEndpointTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
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
            PublisherName = "Contoso APIs",
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
            PublisherName = "Contoso APIs",
        });

        Assert.Throws<InvalidOperationException>(() =>
            apim.WithClassicVirtualNetwork(subnet, AzureApiManagementVirtualNetworkMode.External));
    }

    [Fact]
    public async Task PrivateEndpointsPreservePublicAccess()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var firstPrivateEndpointSubnet = vnet.AddSubnet("private-endpoint-subnet-1", "10.0.1.0/24");
        var secondPrivateEndpointSubnet = vnet.AddSubnet("private-endpoint-subnet-2", "10.0.2.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        firstPrivateEndpointSubnet.AddPrivateEndpoint(apim);
        secondPrivateEndpointSubnet.AddPrivateEndpoint(apim);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var modules = new List<string>();
        foreach (var resource in builder.Resources.OfType<AzureProvisioningResource>())
        {
            var (_, bicep) = await GetManifestWithBicep(resource);
            modules.Add($"// Resource: {resource.Name}\n{bicep}");
        }

        await Verify(string.Join("\n\n", modules), "bicep");
    }

    [Fact]
    public async Task PrivateEndpointRejectsUnsupportedSku()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("private-endpoint-subnet", "10.0.0.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Consumption,
            Capacity = 0,
        });
        subnet.AddPrivateEndpoint(apim);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("does not support private endpoints", exception.Message);
    }

    [Fact]
    public async Task PrivateEndpointSupportsExistingServiceWithoutMutation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("private-endpoint-subnet", "10.0.0.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("existing-apim", resourceGroup: null);
        var privateEndpoint = subnet.AddPrivateEndpoint(apim);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);
        var (_, privateEndpointBicep) = await GetManifestWithBicep(privateEndpoint.Resource);
        await Verify($"{bicep}\n{privateEndpointBicep}", "bicep");
    }

    [Fact]
    public async Task PrivateEndpointRejectsVirtualNetworkInjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var virtualNetwork = builder.AddAzureVirtualNetwork("vnet");
        var injectionSubnet = virtualNetwork.AddSubnet("apim-subnet", "10.0.0.0/24");
        var privateEndpointSubnet = virtualNetwork.AddSubnet("private-endpoint-subnet", "10.0.1.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.PremiumV2,
        }).WithVirtualNetworkInjection(injectionSubnet);
        privateEndpointSubnet.AddPrivateEndpoint(apim);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("cannot combine a private endpoint with virtual network injection", exception.Message);
    }

    [Fact]
    public async Task StandardV2VirtualNetworkIntegrationGeneratesDelegatedSubnet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var virtualNetwork = builder.AddAzureVirtualNetwork("vnet");
        var subnet = virtualNetwork.AddSubnet("apim-subnet", "10.0.0.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        }).WithVirtualNetworkIntegration(subnet);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);
        var (_, virtualNetworkBicep) = await GetManifestWithBicep(virtualNetwork.Resource);
        var (_, networkSecurityGroupBicep) = await GetManifestWithBicep(subnet.Resource.NetworkSecurityGroup!);

        Assert.Contains("virtualNetworkType: 'External'", apimBicep);
        Assert.Contains("Microsoft.Web/serverFarms", virtualNetworkBicep);
        Assert.Contains("destinationAddressPrefix: 'AzureKeyVault'", networkSecurityGroupBicep);
        Assert.Contains("destinationPortRange: '443'", networkSecurityGroupBicep);
        await Verify(apimBicep, "bicep");
    }

    [Fact]
    public async Task PremiumV2VirtualNetworkInjectionGeneratesPrivateService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var virtualNetwork = builder.AddAzureVirtualNetwork("vnet");
        var subnet = virtualNetwork.AddSubnet("apim-subnet", "10.0.0.0/24");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.PremiumV2,
        }).WithVirtualNetworkInjection(subnet);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);
        var (_, virtualNetworkBicep) = await GetManifestWithBicep(virtualNetwork.Resource);
        var (_, networkSecurityGroupBicep) = await GetManifestWithBicep(subnet.Resource.NetworkSecurityGroup!);

        Assert.Contains("virtualNetworkType: 'Internal'", apimBicep);
        Assert.DoesNotContain("publicNetworkAccess:", apimBicep);
        Assert.Contains("Microsoft.Web/hostingEnvironments", virtualNetworkBicep);
        Assert.Contains("destinationAddressPrefix: 'AzureKeyVault'", networkSecurityGroupBicep);
        Assert.Contains("destinationPortRange: '443'", networkSecurityGroupBicep);
        await Verify(apimBicep, "bicep");
    }

    [Theory]
    [InlineData(AzureApiManagementSku.StandardV2, "microsoft.web/serverfarms")]
    [InlineData(AzureApiManagementSku.PremiumV2, "MICROSOFT.WEB/HOSTINGENVIRONMENTS")]
    public void V2VirtualNetworkAcceptsCaseInsensitiveDelegation(AzureApiManagementSku sku, string delegation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet").AddSubnet("subnet", "10.0.0.0/24")
            .WithServiceDelegation(delegation);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = sku,
        });

        if (sku == AzureApiManagementSku.StandardV2)
        {
            apim.WithVirtualNetworkIntegration(subnet);
        }
        else
        {
            apim.WithVirtualNetworkInjection(subnet);
        }

        Assert.Equal(delegation, subnet.Resource.Annotations.OfType<AzureSubnetServiceDelegationAnnotation>().Single().ServiceName);
        Assert.NotNull(apim.Resource.VirtualNetworkConfiguration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2KeyVaultAllowPrecedesExistingOutboundDeny(bool injection)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet").AddSubnet("subnet", "10.0.0.0/24")
            .AllowInbound(port: "443", priority: 100)
            .DenyOutbound(priority: 200);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = injection ? AzureApiManagementSku.PremiumV2 : AzureApiManagementSku.StandardV2,
        });

        if (injection)
        {
            apim.WithVirtualNetworkInjection(subnet).WithVirtualNetworkInjection(subnet);
        }
        else
        {
            apim.WithVirtualNetworkIntegration(subnet).WithVirtualNetworkIntegration(subnet);
        }

        var rules = subnet.Resource.NetworkSecurityGroup!.SecurityRules;
        Assert.Equal(101, rules.Single(rule => rule.Name == "allow-apim-key-vault").Priority);
        Assert.Equal(3, rules.Count);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var (_, bicep) = await GetManifestWithBicep(subnet.Resource.NetworkSecurityGroup);
        await Verify(bicep, "bicep").UseParameters(injection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V2KeyVaultAllowRejectsUnavoidableOutboundDeny(bool injection)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet").AddSubnet("subnet", "10.0.0.0/24")
            .DenyOutbound(port: "400-500", priority: 100);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = injection ? AzureApiManagementSku.PremiumV2 : AzureApiManagementSku.StandardV2,
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            if (injection)
            {
                apim.WithVirtualNetworkInjection(subnet);
            }
            else
            {
                apim.WithVirtualNetworkIntegration(subnet);
            }
        });

        Assert.Contains("no free NSG priority", exception.Message);
        Assert.Null(apim.Resource.VirtualNetworkConfiguration);
        Assert.Single(subnet.Resource.NetworkSecurityGroup!.SecurityRules);
    }

    [Fact]
    public async Task V2KeyVaultAllowRejectsLaterShadowingRule()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet").AddSubnet("subnet", "10.0.0.0/24")
            .AllowInbound(port: "443", priority: 100);
        builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        }).WithVirtualNetworkIntegration(subnet);
        var firstRule = subnet.Resource.NetworkSecurityGroup!.SecurityRules[0];
        firstRule.Direction = SecurityRuleDirection.Outbound;
        firstRule.Access = SecurityRuleAccess.Deny;

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("must retain an effective", exception.Message);
    }

    [Fact]
    public void V2VirtualNetworkRejectsSubnetSmallerThanSlash27()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("apim-subnet", "10.0.0.0/28");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => apim.WithVirtualNetworkIntegration(subnet));

        Assert.Contains("requires a /27 or larger subnet", exception.Message);
    }

    [Fact]
    public void V2VirtualNetworkRejectsSubnetUsedByAnotherApiManagementService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("apim-subnet", "10.0.0.0/24");
        builder.AddAzureApiManagement("first-apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        }).WithVirtualNetworkIntegration(subnet);
        var second = builder.AddAzureApiManagement("second-apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => second.WithVirtualNetworkIntegration(subnet));

        Assert.Contains("already used by API Management resource 'first-apim'", exception.Message);
    }

    [Fact]
    public void VirtualNetworkRejectsSubnetUsedByClassicApiManagementService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var subnet = builder.AddAzureVirtualNetwork("vnet")
            .AddSubnet("apim-subnet", "10.0.0.0/24");
        builder.AddAzureApiManagement("classic-apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        }).WithClassicVirtualNetwork(subnet, AzureApiManagementVirtualNetworkMode.External);
        var second = builder.AddAzureApiManagement("v2-apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => second.WithVirtualNetworkIntegration(subnet));

        Assert.Contains("already used by API Management resource 'classic-apim'", exception.Message);
    }

    [Fact]
    public async Task AddAzureApiManagementWithApiAndPoliciesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
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
        api.AddOperation("get-product", "get", "/products/{id}/{*path}", "Get product")
            .WithInboundPolicy("<set-query-parameter name=\"source\" exists-action=\"override\"><value>apim</value></set-query-parameter>");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        var proxyMethods = new[] { "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT", "TRACE" };
        foreach (var proxyMethod in proxyMethods)
        {
            Assert.Contains($"name: 'proxy-{proxyMethod.ToLowerInvariant()}'", bicep);
        }
        Assert.Equal(proxyMethods.Length, bicep.Split("urlTemplate: '/{*path}'").Length - 1);
        Assert.DoesNotContain("method: '*'", bicep);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task OpenApiDocumentImportGeneratesApiImport()
    {
        using var temporaryWorkspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(temporaryWorkspace.Path, "catalog.json");
        await File.WriteAllTextAsync(
            documentPath,
            """{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}""",
            TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var backend = apim.AddBackend("catalog-backend", ReferenceExpression.Create($"https://example.com"));
        apim.AddApi("catalog-api", "catalog")
            .WithBackend(backend)
            .WithOpenApiDocument(documentPath);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("format: 'openapi+json'", bicep);
        Assert.Contains("""value: '{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}'""", bicep);
        Assert.DoesNotContain("name: 'proxy'", bicep);
        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunModeDoesNotReadOpenApiDocument(bool emptyFile)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(workspace.Path, "deploy-only.json");
        if (emptyFile)
        {
            File.WriteAllText(documentPath, " ");
        }

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var api = apim.AddApi("catalog-api", "catalog").WithOpenApiDocument(documentPath);
        api.AddOperation("root", "GET", "/");

        var source = Assert.IsType<AzureApiManagementOpenApiFile>(api.Resource.OpenApiSource);
        Assert.Equal(documentPath, source.Path);
        Assert.Equal(AzureApiManagementOpenApiFormat.OpenApiJson, source.Format);
        Assert.Empty(builder.Resources.OfType<AzureApiManagementResource>());
        using var app = builder.Build();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishRejectsMissingOrEmptyOpenApiDocument(bool emptyFile)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(workspace.Path, "deploy-only.json");
        if (emptyFile)
        {
            File.WriteAllText(documentPath, " ");
        }
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var backend = apim.AddBackend("backend", ReferenceExpression.Create($"https://example.com"));
        apim.AddApi("catalog-api", "catalog").WithBackend(backend).WithOpenApiDocument(documentPath);

        using var app = builder.Build();
        if (emptyFile)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
            Assert.Contains("is empty", exception.Message);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => ExecuteBeforeStartHooksAsync(app, default));
            Assert.Equal(documentPath, exception.FileName);
        }
    }

    [Fact]
    public async Task OpenApiDocumentCanBeCreatedAfterModelConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(workspace.Path, "generated.json");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        var backend = apim.AddBackend("backend", ReferenceExpression.Create($"https://example.com"));
        apim.AddApi("catalog-api", "catalog").WithBackend(backend).WithOpenApiDocument(documentPath);
        const string document = """{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}""";
        File.WriteAllText(documentPath, document);
        string? importedDocument = null;
        apim.ConfigureInfrastructure(infrastructure =>
        {
            importedDocument = infrastructure.GetProvisionableResources()
                .OfType<ApiManagementApi>().Single().Value.Value;
        });

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Equal(document, importedDocument);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiPolicyWaitsForOperationConfiguration(bool importOpenApi)
    {
        using var temporaryWorkspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(temporaryWorkspace.Path, "catalog.json");
        await File.WriteAllTextAsync(
            documentPath,
            """{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}""",
            TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var backend = apim.AddBackend("catalog-backend", ReferenceExpression.Create($"https://example.com"));
        var api = apim.AddApi("catalog-api", "catalog").WithBackend(backend);
        if (importOpenApi)
        {
            api.WithOpenApiDocument(documentPath)
                .WithPolicy("<policies><inbound><base /><set-backend-service backend-id=\"catalog-backend\" /></inbound></policies>");
        }
        api.AddOperation("get-product", "GET", "/products/{id}");
        api.AddOperation("list-products", "GET", "/products")
            .WithInboundPolicy("<set-header name=\"x-source\" exists-action=\"override\"><value>apim</value></set-header>");

        // A different API's operations must not become dependencies of the catalog policy.
        apim.AddApi("other-api", "other").WithBackend(backend)
            .AddOperation("other-operation", "GET", "/other");

        string[]? dependencies = null;
        apim.ConfigureInfrastructure(infrastructure =>
        {
            var policy = infrastructure.GetProvisionableResources()
                .OfType<ApiPolicy>()
                .Single(resource => resource.BicepIdentifier == "_apim_apiPolicy_catalog_api");
            dependencies = policy.DependsOn.Select(resource => resource.BicepIdentifier).ToArray();
        });

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        var expected = new List<string> { "catalog_backend" };
        if (!importOpenApi)
        {
            expected.AddRange(new[] { "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT", "TRACE" }
                .SelectMany(method => new[]
                {
                    $"_apim_proxy{method}RootOperation_catalog_api",
                    $"_apim_proxy{method}Operation_catalog_api",
                }));
        }
        expected.Add("get_product");
        expected.Add("_apim_operationPolicy_list_products");
        Assert.Equal(expected, dependencies);
        await Verify(bicep, "bicep").UseParameters(importOpenApi);
    }

    [Fact]
    public async Task OpenApiEndpointImportUsesPublishedComputeEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddApi("catalog-api", backend, "catalog")
            .WithOpenApiEndpoint("/openapi/v1.json");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("format: 'openapi+json-link'", bicep);
        Assert.Contains("param _apim_openApiUrl_catalog_api string", bicep);
        Assert.Contains("param _apim_computeBackendUrl_catalog_api string", bicep);
        Assert.Contains(
            "url: empty(catalog_api.properties.?serviceUrl) ? _apim_computeBackendUrl_catalog_api : catalog_api.properties.?serviceUrl",
            bicep);
        Assert.DoesNotContain("name: 'proxy'", bicep);
        Assert.Contains("catalog-backend", manifest.ToJsonString());
        Assert.Contains("/openapi/v1.json", manifest.ToJsonString());
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task OpenApiDocumentDoesNotRequireEndpointDeploymentDependency()
    {
        using var temporaryWorkspace = TemporaryWorkspace.Create(output);
        var documentPath = Path.Combine(temporaryWorkspace.Path, "catalog.json");
        await File.WriteAllTextAsync(
            documentPath,
            """{"openapi":"3.0.1","info":{"title":"Catalog","version":"v1"},"paths":{}}""",
            TestContext.Current.CancellationToken);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddApi("catalog-api", backend, "catalog")
            .WithOpenApiDocument(documentPath);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var provisionStep = new PipelineStep
        {
            Name = "provision-apim",
            Action = static _ => Task.CompletedTask,
            Resource = apim.Resource,
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
        };
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var context = new PipelineConfigurationContext
        {
            Services = app.Services,
            Model = model,
            Steps = [provisionStep],
        };
        foreach (var annotation in apim.Resource.Annotations.OfType<PipelineConfigurationAnnotation>())
        {
            await annotation.Callback(context);
        }

        Assert.Empty(provisionStep.DependsOnSteps);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false, false)]
    [InlineData(DistributedApplicationOperation.Run, false, true)]
    [InlineData(DistributedApplicationOperation.Run, true, false)]
    [InlineData(DistributedApplicationOperation.Run, true, true)]
    [InlineData(DistributedApplicationOperation.Publish, false, false)]
    [InlineData(DistributedApplicationOperation.Publish, false, true)]
    [InlineData(DistributedApplicationOperation.Publish, true, false)]
    [InlineData(DistributedApplicationOperation.Publish, true, true)]
    public void OpenApiRejectsConflictingSources(
        DistributedApplicationOperation operation,
        bool firstSourceIsFile,
        bool secondSourceIsFile)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var target = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var api = apim.AddApi("catalog-api", target, "catalog");
        if (firstSourceIsFile)
        {
            api.WithOpenApiDocument("first.json");
        }
        else
        {
            api.WithOpenApiEndpoint("/openapi/first.json");
        }
        var originalSource = api.Resource.OpenApiSource;

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            if (secondSourceIsFile)
            {
                api.WithOpenApiDocument("second.json");
            }
            else
            {
                api.WithOpenApiEndpoint("/openapi/second.json");
            }
        });

        Assert.Equal(
            "API Management API 'catalog-api' already has a different OpenAPI source. " +
            "An API can use only one OpenAPI source, either a document file or an endpoint.",
            exception.Message);
        Assert.Same(originalSource, api.Resource.OpenApiSource);
    }

    [Fact]
    public void OpenApiDocumentAllowsIdenticalSourceButRejectsDifferentFormat()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var api = apim.AddApi("catalog-api", "catalog").WithOpenApiDocument("catalog.json");
        var originalSource = api.Resource.OpenApiSource;

        Assert.Same(api, api.WithOpenApiDocument(
            Path.Combine(builder.AppHostDirectory, "catalog.json"),
            AzureApiManagementOpenApiFormat.OpenApiJson));
        Assert.Equal(originalSource, api.Resource.OpenApiSource);
        Assert.Throws<InvalidOperationException>(
            () => api.WithOpenApiDocument("catalog.json", AzureApiManagementOpenApiFormat.SwaggerJson));
        Assert.Equal(originalSource, api.Resource.OpenApiSource);
    }

    [Theory]
    [InlineData("/openapi/other.json", null, AzureApiManagementOpenApiFormat.OpenApiJson)]
    [InlineData("/openapi/v1.json", "https", AzureApiManagementOpenApiFormat.OpenApiJson)]
    [InlineData("/openapi/v1.json", null, AzureApiManagementOpenApiFormat.SwaggerJson)]
    public void OpenApiEndpointAllowsIdenticalSourceButRejectsDifferentConfiguration(
        string documentPath,
        string? endpointName,
        AzureApiManagementOpenApiFormat format)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var target = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var api = apim.AddApi("catalog-api", target, "catalog").WithOpenApiEndpoint();
        var originalSource = api.Resource.OpenApiSource;

        Assert.Same(api, api.WithOpenApiEndpoint(
            "/openapi/v1.json", format: AzureApiManagementOpenApiFormat.OpenApiJson));
        Assert.Equal(originalSource, api.Resource.OpenApiSource);
        Assert.Throws<InvalidOperationException>(
            () => api.WithOpenApiEndpoint(documentPath, endpointName, format));
        Assert.Equal(originalSource, api.Resource.OpenApiSource);
    }

    [Fact]
    public async Task OpenApiEndpointImportRejectsInternalContainerAppEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var virtualNetwork = builder.AddAzureVirtualNetwork("vnet");
        var containerAppsSubnet = virtualNetwork.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        var apimSubnet = virtualNetwork.AddSubnet("apim-subnet", "10.0.2.0/24");
        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(containerAppsSubnet)
            .WithInternalLoadBalancer(virtualNetwork);
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Premium,
        }).WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
        apim.AddApi("catalog-api", backend, "catalog")
            .WithOpenApiEndpoint();

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("control plane cannot retrieve its OpenAPI document", exception.Message);
    }

    [Fact]
    public async Task AddAzureApiManagementWithServiceFeaturesGeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var insights = builder.AddAzureApplicationInsights("insights");
        var vault = builder.AddAzureKeyVault("vault");
        var apiKey = builder.AddParameter("api-key", secret: true);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        });

        var fragment = apim.AddPolicyFragment(
            "correlation",
            "<set-header name=\"x-correlation-id\" exists-action=\"skip\"><value>@(context.RequestId.ToString())</value></set-header>",
            "Adds a correlation ID.");
        apim.WithInboundPolicyFragment(fragment)
            .WithApplicationInsights(insights, new()
            {
                SamplingPercentage = 25,
                Verbosity = AzureApiManagementDiagnosticVerbosity.Error,
            })
            .WithCustomDomain(
                "api.contoso.example",
                vault.GetSecret("gateway-certificate"),
                defaultSslBinding: true);
        apim.AddNamedValue("backend-region", "westus3", tags: ["routing"]);
        apim.AddSecretNamedValue("api-key-value", apiKey, displayName: "ApiKey");
        apim.AddKeyVaultNamedValue(
            "upstream-secret",
            vault.GetSecret("upstream-secret"),
            displayName: "UpstreamSecret");

        var api = apim.AddApi("catalog-api", backend, "catalog")
            .WithInboundPolicyFragment(fragment)
            .WithApplicationInsights(insights, new()
            {
                SamplingPercentage = 50,
                LogClientIp = true,
            });
        api.AddOperation("get-product", "GET", "/products/{id}")
            .WithInboundPolicyFragment(fragment);
        apim.AddProduct(
                "catalog-product",
                "Catalog",
                new()
                {
                    Description = "Catalog APIs",
                    Terms = "Use responsibly.",
                })
            .WithApi(api)
            .AddSubscription("catalog-client", "Catalog client");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("param _apim_namedValueParameter_api_key_value string", bicep);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task ProductApiAssociationsHaveUnambiguousBicepIdentifiers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var catalogApi = apim.AddApi("catalog-api", backend, "catalog");
        var productCatalogApi = apim.AddApi("product-catalog-api", backend, "product-catalog");
        apim.AddProduct("catalog-product", "Catalog product").WithApi(catalogApi);
        apim.AddProduct("catalog", "Catalog").WithApi(productCatalogApi);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);
        var associationDeclarations = bicep
            .Split('\n')
            .Where(line => line.StartsWith("resource _apim_productApi_", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, associationDeclarations.Length);
        Assert.Equal(2, associationDeclarations.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ApiManagementServiceFeaturesValidateInvalidConfigurations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var nonSecretParameter = builder.AddParameter("value");
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var otherApim = builder.AddAzureApiManagement("other-apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var api = apim.AddApi("catalog-api", backend, "catalog");
        var otherApi = otherApim.AddApi("other-api", backend, "other");
        var product = apim.AddProduct("catalog-product", "Catalog");
        var fragment = apim.AddPolicyFragment("shared-policy", "<rate-limit calls=\"10\" renewal-period=\"60\" />");
        var otherFragment = otherApim.AddPolicyFragment("other-policy", "<rate-limit calls=\"20\" renewal-period=\"60\" />");

        Assert.Throws<InvalidOperationException>(() => product.WithApi(otherApi));
        Assert.Throws<InvalidOperationException>(() => api.WithInboundPolicyFragment(otherFragment));
        Assert.Throws<ArgumentException>(() => apim.AddPolicyFragment("invalid-fragment", "<base />", fragmentName: "-invalid"));
        Assert.Throws<ArgumentException>(() => apim.AddSecretNamedValue("secret", nonSecretParameter));
        Assert.Throws<ArgumentException>(() => apim.AddNamedValue("invalid-value", "value", displayName: "invalid value"));
        Assert.Throws<ArgumentException>(() => apim.AddNamedValue("empty-value", " "));
        Assert.Throws<ArgumentException>(() => apim.AddNamedValue("long-value", new string('v', 4097)));
        Assert.Throws<ArgumentException>(() => apim.AddNamedValue("empty-display-name", "value", displayName: ""));
        Assert.Throws<ArgumentException>(() =>
            apim.WithCustomDomain(
                "portal.contoso.example",
                vault.GetSecret("certificate"),
                AzureApiManagementHostnameType.DeveloperPortal,
                defaultSslBinding: true));
        apim.WithCustomDomain(
            "api.contoso.example",
            vault.GetSecret("certificate"),
            defaultSslBinding: true);
        Assert.Throws<InvalidOperationException>(() =>
            apim.WithCustomDomain(
                "api2.contoso.example",
                vault.GetSecret("certificate-2"),
                defaultSslBinding: true));

        api.WithInboundPolicyFragment(fragment);
        Assert.Single(api.Resource.InboundPolicyStatements);
    }

    [Fact]
    public void NamedValueDisplayNamesMustBeUnique()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddNamedValue("first-value", "value", displayName: "shared-name");

        var exception = Assert.Throws<InvalidOperationException>(
            () => apim.AddNamedValue("second-value", "value", displayName: "SHARED-NAME"));

        Assert.Contains("display name 'SHARED-NAME'", exception.Message);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("gateway.")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("https://api.example.com")]
    [InlineData("api..example.com")]
    public void CustomDomainsRejectUnqualifiedOrInvalidHostnames(string hostname)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });

        var exception = Assert.Throws<ArgumentException>(() => apim.WithCustomDomain(hostname, vault.GetSecret("certificate")));

        Assert.Equal("hostname", exception.ParamName);
        Assert.Empty(apim.Resource.CustomDomains);
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("API.EXAMPLE.COM")]
    [InlineData("api.example.com.")]
    [InlineData("api.example")]
    public void CustomDomainsAcceptQualifiedDnsNames(string hostname)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });

        apim.WithCustomDomain(hostname, vault.GetSecret("certificate"));

        Assert.Equal(hostname, Assert.Single(apim.Resource.CustomDomains).Hostname);
    }

    [Fact]
    public void ConsumptionSkuSupportsOneGatewayCustomDomain()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Consumption,
            Capacity = 0,
        });

        apim.WithCustomDomain("api.contoso.example", vault.GetSecret("certificate"));

        Assert.Single(apim.Resource.CustomDomains);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            apim.WithCustomDomain(
                "portal.contoso.example",
                vault.GetSecret("portal-certificate"),
                AzureApiManagementHostnameType.DeveloperPortal));

        Assert.Contains("supports custom domains only for the gateway endpoint", exception.Message);
    }

    [Theory]
    [InlineData(AzureApiManagementSku.BasicV2)]
    [InlineData(AzureApiManagementSku.StandardV2)]
    [InlineData(AzureApiManagementSku.PremiumV2)]
    public void V2SkusRejectManagementAndScmCustomDomains(AzureApiManagementSku sku)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = sku,
        });

        Assert.Throws<InvalidOperationException>(() =>
            apim.WithCustomDomain(
                "management.contoso.example",
                vault.GetSecret("management-certificate"),
                AzureApiManagementHostnameType.Management));
        Assert.Throws<InvalidOperationException>(() =>
            apim.WithCustomDomain(
                "scm.contoso.example",
                vault.GetSecret("scm-certificate"),
                AzureApiManagementHostnameType.Scm));
    }

    [Theory]
    [InlineData(AzureApiManagementSku.Consumption)]
    [InlineData(AzureApiManagementSku.Basic)]
    [InlineData(AzureApiManagementSku.BasicV2)]
    [InlineData(AzureApiManagementSku.Standard)]
    [InlineData(AzureApiManagementSku.StandardV2)]
    public void NonPremiumProductionSkusRejectMultipleGatewayCustomDomains(AzureApiManagementSku sku)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = sku,
        });
        apim.WithCustomDomain("api.contoso.example", vault.GetSecret("certificate"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            apim.WithCustomDomain("api2.contoso.example", vault.GetSecret("certificate-2")));

        Assert.Contains("Multiple gateway custom domains", exception.Message);
    }

    [Theory]
    [InlineData(AzureApiManagementSku.Developer)]
    [InlineData(AzureApiManagementSku.Premium)]
    [InlineData(AzureApiManagementSku.PremiumV2)]
    public void SupportedSkusAllowMultipleGatewayCustomDomains(AzureApiManagementSku sku)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = sku,
        });

        apim.WithCustomDomain("api.contoso.example", vault.GetSecret("certificate"));
        apim.WithCustomDomain("api2.contoso.example", vault.GetSecret("certificate-2"));

        Assert.Equal(2, apim.Resource.CustomDomains.Count);
    }

    [Theory]
    [InlineData(AzureApiManagementHostnameType.ConfigurationApi)]
    [InlineData(AzureApiManagementHostnameType.DeveloperPortal)]
    [InlineData(AzureApiManagementHostnameType.Portal)]
    [InlineData(AzureApiManagementHostnameType.Management)]
    [InlineData(AzureApiManagementHostnameType.Scm)]
    public void NonGatewayEndpointsRejectMultipleCustomDomains(AzureApiManagementHostnameType type)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        });
        apim.WithCustomDomain(
            "endpoint.contoso.example",
            vault.GetSecret("certificate"),
            type);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            apim.WithCustomDomain(
                "endpoint2.contoso.example",
                vault.GetSecret("certificate-2"),
                type));

        Assert.Contains($"Only one custom hostname can be configured for endpoint type '{type}'", exception.Message);
    }

    [Fact]
    public void ProductRejectsSubscriptionLimitWhenSubscriptionsAreDisabled()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        var exception = Assert.Throws<ArgumentException>(() =>
            apim.AddProduct(
                "product",
                "Product",
                new()
                {
                    SubscriptionRequired = false,
                    SubscriptionsLimit = 1,
                }));

        Assert.Contains("only be configured when subscriptions are required", exception.Message);
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
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Premium,
        }).WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
        apim.AddApi("catalog-api", backend, "catalog");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("param _apim_computeBackendUrl_catalog_api string", bicep);
        Assert.Contains("catalog-backend.internal", manifest.ToJsonString());
    }

    [Fact]
    public async Task InternalBackendRequiresApiManagementVirtualNetworkConnectivity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("catalog-backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
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
            PublisherName = "Contoso APIs",
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
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.StandardV2,
        });
        var primaryBackend = apim.AddFoundryBackend("chat-primary-backend", primary);
        var secondaryBackend = apim.AddFoundryBackend("chat-secondary-backend", secondary);
        var pool = apim.AddBackendPool("openai-pool")
            .WithBackend(primaryBackend, priority: 1, weight: 3)
            .WithBackend(secondaryBackend, priority: 2, weight: 1);
        apim.AddOpenAIApi("openai-api", "openai")
            .WithBackend(pool);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("openai/deployments/chat-primary", manifest.ToJsonString());
        Assert.Contains("openai/deployments/chat-secondary", manifest.ToJsonString());
        Assert.Contains("foundry-primary.outputs.endpoint", manifest.ToJsonString());
        await Verify(bicep, "bicep");
    }

    [Fact]
    public void ConsumptionSkuRejectsOpenAIBackendPools()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Consumption,
            Capacity = 0,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => apim.AddBackend(
            "backend",
            ReferenceExpression.Create($"https://example.com"),
            new AzureApiManagementBackendOptions
            {
                CircuitBreaker = new AzureApiManagementCircuitBreakerOptions(),
            }));

        Assert.Contains("not supported by the Consumption SKU", exception.Message);
    }

    [Fact]
    public void OpenAIBackendPoolAcceptsZeroPriorityAndWeight()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var foundry = builder.AddFoundry("foundry");
        var model = foundry.AddDeployment("chat", "gpt-5-mini", "2025-08-07", "OpenAI");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        var backend = apim.AddFoundryBackend("chat-backend", model);
        var pool = apim.AddBackendPool("openai-pool")
            .WithBackend(backend, priority: 0, weight: 0);

        var member = Assert.Single(pool.Resource.Backends);
        Assert.Equal(0, member.Priority);
        Assert.Equal(0, member.Weight);
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
            PublisherName = "Contoso APIs",
        });
        var backend = apim.AddAzureOpenAIBackend("openai-backend", deployment);
        apim.AddOpenAIApi("chat-api", "chat")
            .WithBackend(backend);
        apim.AddOpenAIApi("responses-api", "responses")
            .WithBackend(backend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);
        var manifestJson = manifest.ToJsonString();

        Assert.Contains("openai.outputs.endpoint", manifestJson);
        Assert.Contains("openai/deployments/chat", manifestJson);
        var roles = Assert.Single(builder.Resources.OfType<AzureRoleAssignmentResource>());
        Assert.Same(apim.Resource.Identity, roles.IdentityResource);
        var (_, rolesBicep) = await GetManifestWithBicep(roles, skipPreparer: true);
        Assert.Single(rolesBicep.Split("Microsoft.Authorization/roleAssignments@", StringSplitOptions.None).Skip(1));
        Assert.Contains("5e0bd9bd-7b93-4f28-af87-19fc36ad61bd", rolesBicep);
        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData(false, "ai-resources")]
    [InlineData(true, null)]
    [InlineData(true, "ignored-resources")]
    public async Task CrossResourceGroupAzureOpenAIBackendUsesTargetScopedRoleAssignmentModule(
        bool overrideScope,
        string? resourceGroup)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var account = builder.AddAzureOpenAI("openai")
            .PublishAsExisting("shared-openai", resourceGroup: resourceGroup);
        if (overrideScope)
        {
            account.Resource.Scope = new AzureBicepResourceScope("ai-resources");
        }

        var deployment = account.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var chatBackend = apim.AddAzureOpenAIBackend("chat-backend", deployment);
        var responsesBackend = apim.AddAzureOpenAIBackend("responses-backend", deployment);
        apim.AddOpenAIApi("chat-api", "chat").WithBackend(chatBackend);
        apim.AddOpenAIApi("responses-api", "responses").WithBackend(responsesBackend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var roleAssignments = Assert.Single(
            model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "apim-identity-roles-openai");
        Assert.Same(account.Resource, roleAssignments.TargetAzureResource);
        Assert.Equal("ai-resources", roleAssignments.Scope!.ResourceGroup);
        Assert.Same(apim.Resource.Identity, roleAssignments.OwnerResource);
        Assert.Same(apim.Resource.Identity, roleAssignments.IdentityResource);
        Assert.Contains(roleAssignments, apim.Resource.References);

        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);
        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(roleAssignments, skipPreparer: true);

        Assert.DoesNotContain("Microsoft.Authorization/roleAssignments@", apimBicep);
        Assert.Equal(1, rolesBicep.Split("Microsoft.Authorization/roleAssignments@", StringSplitOptions.None).Length - 1);
        Assert.Contains("resource openai", rolesBicep);
        Assert.DoesNotContain("scope: resourceGroup('ai-resources')", rolesBicep);
        Assert.Contains("ai-resources", rolesManifest.ToJsonString());
        Assert.Contains("apim-identity.outputs.principalId", rolesManifest.ToJsonString());
    }

    [Fact]
    public async Task UserAssignedIdentityPermissionsCoverBackendsAndKeyVault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var openai = builder.AddAzureOpenAI("openai");
        var openaiDeployment = openai.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var foundry = builder.AddFoundry("foundry");
        var foundryDeployment = foundry.AddDeployment("foundry-chat", "gpt-5-mini", "2025-08-07", "OpenAI");
        var blobs = builder.AddAzureStorage("storage").AddBlobs("blobs");
        var vault = builder.AddAzureKeyVault("vault");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddAzureOpenAIBackend("openai-backend", openaiDeployment);
        apim.AddFoundryBackend("foundry-backend", foundryDeployment);
        apim.AddBlobStorageBackend("blob-backend", blobs);
        apim.WithCustomDomain("api.contoso.example", vault.GetSecret("gateway-certificate"));
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));
        apim.AddKeyVaultNamedValue("another-secret", vault.GetSecret("another-secret"));

        await VerifyAllAzureBicep(builder);

        Assert.Same(apim.Resource.Identity, Assert.Single(builder.Resources.OfType<AzureUserAssignedIdentityResource>()));
        var roles = builder.Resources.OfType<AzureRoleAssignmentResource>().ToArray();
        Assert.Equal(4, roles.Length);
        Assert.All(roles, role =>
        {
            Assert.Same(apim.Resource.Identity, role.IdentityResource);
            Assert.Contains(role, apim.Resource.References);
        });
    }

    [Fact]
    public async Task BackendRolesArePreparedByStandardAzureResourcePreparer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var account = builder.AddAzureOpenAI("openai")
            .PublishAsExisting("shared-openai", resourceGroup: "ai-resources");
        var deployment = account.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddAzureOpenAIBackend("openai-backend", deployment);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roles = Assert.Single(builder.Resources.OfType<AzureRoleAssignmentResource>());
        Assert.Same(account.Resource, roles.TargetAzureResource);
        Assert.Same(apim.Resource.Identity, roles.IdentityResource);
        Assert.Same(apim.Resource.Identity, roles.OwnerResource);
        Assert.Contains(roles, apim.Resource.References);
        var (manifest, bicep) = await GetManifestWithBicep(roles, skipPreparer: true);
        await Verify(new { Manifest = manifest.ToJsonString(), Bicep = bicep });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitlyScopedExistingApiManagementUsesExternalRoleModuleForDefaultScopeBackend(bool overrideScope)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var account = builder.AddAzureOpenAI("openai")
            .PublishAsExisting("shared-openai", resourceGroup: null);
        var deployment = account.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("shared-apim", resourceGroup: overrideScope ? null : "gateway-resources");
        var identity = builder.AddAzureUserAssignedIdentity("shared-identity")
            .PublishAsExisting("shared-identity", resourceGroup: null);
        apim.WithAzureUserAssignedIdentity(identity);
        if (overrideScope)
        {
            apim.Resource.Scope = new AzureBicepResourceScope("gateway-resources");
        }

        var backend = apim.AddAzureOpenAIBackend("openai-backend", deployment);
        apim.AddOpenAIApi("chat-api", "chat").WithBackend(backend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var roleAssignments = Assert.Single(
            model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "shared-identity-roles-openai");
        Assert.Null(roleAssignments.Scope);
        Assert.Same(account.Resource, roleAssignments.TargetAzureResource);
        Assert.Same(apim.Resource.Identity, roleAssignments.IdentityResource);
        Assert.Contains(roleAssignments, apim.Resource.References);

        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource, skipPreparer: true);
        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(roleAssignments, skipPreparer: true);

        Assert.DoesNotContain("Microsoft.Authorization/roleAssignments@", apimBicep);
        Assert.Contains("Microsoft.Authorization/roleAssignments@", rolesBicep);
        Assert.DoesNotContain("\"scope\"", rolesManifest.ToJsonString());
        Assert.Contains("shared-identity.outputs.principalId", rolesManifest.ToJsonString());
    }

    [Fact]
    public async Task SameScopeExistingAzureOpenAIBackendUsesStandardRoleAssignmentModule()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var account = builder.AddAzureOpenAI("openai")
            .PublishAsExisting("shared-openai", resourceGroup: null);
        var deployment = account.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var backend = apim.AddAzureOpenAIBackend("openai-backend", deployment);
        apim.AddOpenAIApi("chat-api", "chat").WithBackend(backend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);

        var roles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>());
        Assert.Same(apim.Resource.Identity, roles.IdentityResource);
        Assert.Null(roles.Scope);
        var (_, rolesBicep) = await GetManifestWithBicep(roles, skipPreparer: true);
        await Verify(new { ApiManagement = apimBicep, Roles = rolesBicep });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossResourceGroupKeyVaultUsesStandaloneIdentityAndTargetScopedRoleAssignments(bool overrideScope)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault")
            .PublishAsExisting("shared-vault", resourceGroup: overrideScope ? null : "security-resources");
        if (overrideScope)
        {
            vault.Resource.Scope = new AzureBicepResourceScope("security-resources");
        }

        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.WithCustomDomain("api.contoso.example", vault.GetSecret("gateway-certificate"));
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var identity = Assert.Single(
            model.Resources.OfType<AzureUserAssignedIdentityResource>(),
            resource => resource.Name == "apim-identity");
        var roleAssignments = Assert.Single(
            model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "apim-identity-roles-vault");
        Assert.Same(identity, apim.Resource.Identity);
        Assert.Same(vault.Resource, roleAssignments.TargetAzureResource);
        Assert.Equal("security-resources", roleAssignments.Scope!.ResourceGroup);
        Assert.Contains(identity, apim.Resource.References);
        Assert.Contains(roleAssignments, apim.Resource.References);
        Assert.Contains(
            identity.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => relationship.Resource == apim.Resource && relationship.Type == "Parent");
        Assert.Same(identity, roleAssignments.IdentityResource);

        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);
        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(roleAssignments, skipPreparer: true);

        Assert.DoesNotContain("resource _apim_keyVaultIdentity_", apimBicep);
        Assert.DoesNotContain("Microsoft.Authorization/roleAssignments@", apimBicep);
        Assert.Contains("param apim_identity_outputs_id string", apimBicep);
        Assert.Equal(2, rolesBicep.Split("Microsoft.Authorization/roleAssignments@", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("scope: resourceGroup('security-resources')", rolesBicep);
        Assert.Contains("security-resources", rolesManifest.ToJsonString());
        Assert.Contains("apim-identity.outputs.principalId", rolesManifest.ToJsonString());
    }

    [Fact]
    public async Task MultipleApiManagementServicesUseIndependentStandardIdentities()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var account = builder.AddAzureOpenAI("openai")
            .PublishAsExisting("shared-openai", resourceGroup: "ai-resources");
        var deployment = account.AddDeployment("chat", "gpt-5-mini", "2025-08-07");
        var vault = builder.AddAzureKeyVault("vault")
            .PublishAsExisting("shared-vault", resourceGroup: "security-resources");
        var services = new List<IResourceBuilder<AzureApiManagementResource>>();
        for (var index = 0; index < 2; index++)
        {
            var apim = builder.AddAzureApiManagement($"apim-{index}", new()
            {
                PublisherEmail = "api-owners@example.com",
                PublisherName = "Contoso APIs",
            });
            apim.AddAzureOpenAIBackend($"openai-backend-{index}", deployment);
            apim.AddKeyVaultNamedValue($"upstream-secret-{index}", vault.GetSecret("upstream-secret"));
            services.Add(apim);
        }

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Equal(
            [
                "apim-0-identity-roles-openai",
                "apim-0-identity-roles-vault",
                "apim-1-identity-roles-openai",
                "apim-1-identity-roles-vault",
            ],
            model.Resources.OfType<AzureRoleAssignmentResource>().Select(resource => resource.Name).Order());
        Assert.Equal(
            ["apim-0-identity", "apim-1-identity"],
            model.Resources.OfType<AzureUserAssignedIdentityResource>().Select(resource => resource.Name).Order());
    }

    [Fact]
    public async Task PublishedMainBicepOrdersKeyVaultRoleAssignmentsBeforeApiManagement()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            workspace.Path);
        var vault = builder.AddAzureKeyVault("vault")
            .PublishAsExisting("shared-vault", resourceGroup: "security-resources");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        var mainBicepPath = Path.Combine(workspace.Path, "main.bicep");
        Assert.True(File.Exists(mainBicepPath), $"Expected publish to produce '{mainBicepPath}'.");
        var mainBicep = await File.ReadAllTextAsync(
            mainBicepPath,
            TestContext.Current.CancellationToken);

        await Verify(mainBicep, "bicep");
    }

    [Fact]
    public async Task SameScopeExistingKeyVaultUsesStandardIdentityAndRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddAzureKeyVault("vault")
            .PublishAsExisting("shared-vault", resourceGroup: null);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Same(apim.Resource.Identity, Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>()));
        var roles = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>());
        Assert.Same(apim.Resource.Identity, roles.IdentityResource);
        Assert.Contains(roles, apim.Resource.References);
        var (_, rolesBicep) = await GetManifestWithBicep(roles, skipPreparer: true);
        await Verify(new { ApiManagement = apimBicep, Roles = rolesBicep });
    }

    [Fact]
    public async Task CustomKeyVaultResourceKeepsInlineRoleAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vault = builder.AddResource(new CustomKeyVaultResource("vault"));
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddKeyVaultNamedValue("upstream-secret", vault.Resource.GetSecret("upstream-secret"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task MixedCrossScopeAndCustomKeyVaultResourcesPreserveAllRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var crossScopeVault = builder.AddAzureKeyVault("shared-vault")
            .PublishAsExisting("shared-vault", resourceGroup: "security-resources");
        var customVault = builder.AddResource(new CustomKeyVaultResource("custom-vault"));
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        apim.AddKeyVaultNamedValue("shared-secret", crossScopeVault.GetSecret("shared-secret"));
        apim.AddKeyVaultNamedValue("custom-secret", customVault.Resource.GetSecret("custom-secret"));

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var roleAssignments = Assert.Single(
            model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "apim-identity-roles-shared-vault");
        var (_, apimBicep) = await GetManifestWithBicep(apim.Resource);
        var (_, rolesBicep) = await GetManifestWithBicep(roleAssignments, skipPreparer: true);

        Assert.Equal(1, apimBicep.Split("Microsoft.Authorization/roleAssignments@", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, rolesBicep.Split("Microsoft.Authorization/roleAssignments@", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task GeneratedBicepIdentifiersCannotCollideWithUserResourceNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        }).WithInboundPolicy("<set-header name=\"x-test\" exists-action=\"override\"><value>true</value></set-header>");
        var api = apim.AddApi("apimPolicy", backend, "catalog")
            .WithInboundPolicy("<set-header name=\"x-api\" exists-action=\"override\"><value>true</value></set-header>");
        api.AddOperation("apimPolicyProxy", "GET", "/products")
            .WithInboundPolicy("<set-header name=\"x-operation\" exists-action=\"override\"><value>true</value></set-header>");
        apim.AddBackend(
            "apimPolicyBackend",
            ReferenceExpression.Create($"https://example.com"),
            backendName: "physical-backend");
        apim.AddNamedValue("apimPolicyPolicy", "api-policy-collision");
        apim.AddNamedValue("apimPolicyProxyPolicy", "operation-policy-collision");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);
        var declarations = bicep.Split('\n')
            .Where(line => line.StartsWith("resource ", StringComparison.Ordinal) ||
                line.StartsWith("param ", StringComparison.Ordinal) ||
                line.StartsWith("output ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToArray();

        Assert.Equal(declarations.Length, declarations.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("resource _apim_", bicep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingApiManagementRequiresExplicitSkuRegardlessOfCallOrder(bool adoptFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new() { PublisherEmail = "api-owners@example.com", PublisherName = "Contoso APIs" });
        if (adoptFirst)
        {
            apim.PublishAsExisting("shared-apim", resourceGroup: null);
        }
        apim.AddBackend("backend", ReferenceExpression.Create($"https://example.com"), new()
        {
            CircuitBreaker = new(),
        });
        if (!adoptFirst)
        {
            apim.PublishAsExisting("shared-apim", resourceGroup: null);
        }

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("requires an explicit Sku matching the adopted service", exception.Message);
    }

    [Fact]
    public void ExistingConsumptionRejectsCircuitBreakersAndOpenAIBackends()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Consumption,
        }).PublishAsExisting("shared-apim", resourceGroup: null);
        var deployment = builder.AddAzureOpenAI("openai").AddDeployment("chat", "gpt-5-mini", "2025-08-07");

        Assert.Throws<InvalidOperationException>(() =>
            apim.AddBackend("backend", ReferenceExpression.Create($"https://example.com"), new() { CircuitBreaker = new() }));
        Assert.Throws<InvalidOperationException>(() => apim.AddAzureOpenAIBackend("openai-backend", deployment));
        Assert.Empty(apim.Resource.Backends);
    }

    [Fact]
    public async Task ProvisioningCanBeCustomizedWithSdkResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        }).ConfigureInfrastructure(infrastructure =>
        {
            var service = infrastructure.GetProvisionableResources().OfType<ApiManagementService>().Single();
            service.NotificationSenderEmail = "notifications@example.com";
        });

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AddAsExistingResourceUsesApiManagementAnnotationInConsumerModule(
        bool existingConsumer,
        bool overrideScope)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var existingName = builder.AddParameter("apim-name");
        var existingResourceGroup = builder.AddParameter("apim-resource-group");
        var existingSubscription = builder.AddParameter("apim-subscription");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).AsExistingInResourceGroup(existingName, existingResourceGroup, existingSubscription);
        if (overrideScope)
        {
            apim.Resource.Scope = new AzureBicepResourceScope("override-gateway-rg", "override-gateway-subscription");
        }

        var consumer = builder.AddAzureInfrastructure("consumer", infrastructure =>
        {
            var service = Assert.IsType<ApiManagementService>(apim.Resource.AddAsExistingResource(infrastructure));
            Assert.Same(service, apim.Resource.AddAsExistingResource(infrastructure));
            infrastructure.Add(new ProvisioningOutput("gatewayUrl", typeof(string))
            {
                Value = service.GatewayUri,
            });
        });
        if (existingConsumer)
        {
            consumer.AsExistingInResourceGroup("consumer-name", "consumer-rg", "consumer-subscription");
        }

        var (_, bicep) = await GetManifestWithBicep(consumer.Resource, skipPreparer: true);

        await Verify(bicep, "bicep").UseParameters(existingConsumer, overrideScope);
    }

    [Fact]
    public async Task AddAsExistingResourceUsesNameOutputForNewService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var consumer = builder.AddAzureInfrastructure("consumer", infrastructure =>
        {
            var service = Assert.IsType<ApiManagementService>(apim.Resource.AddAsExistingResource(infrastructure));
            Assert.Same(service, apim.Resource.AddAsExistingResource(infrastructure));
            infrastructure.Add(new ProvisioningOutput("gatewayUrl", typeof(string))
            {
                Value = service.GatewayUri,
            });
        });

        var (_, bicep) = await GetManifestWithBicep(consumer.Resource, skipPreparer: true);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task ExistingApiManagementResourceCanProvisionManagedChildren()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("existing-apim", resourceGroup: "shared-infrastructure");
        var backend = apim.AddBackend("catalog-backend", ReferenceExpression.Create($"https://example.com"));
        apim.AddApi("catalog-api", "catalog").WithBackend(backend);
        apim.AddNamedValue("gateway-name", "Contoso");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing", bicep);
        Assert.Contains("name: 'existing-apim'", bicep);
        Assert.Contains("resource catalog_api 'Microsoft.ApiManagement/service/apis@2024-05-01'", bicep);
        Assert.DoesNotContain("publisherEmail:", bicep);
        Assert.Contains("shared-infrastructure", manifest.ToJsonString());
        Assert.DoesNotContain("output principalId", bicep);
    }

    [Theory]
    [InlineData("backend", false)]
    [InlineData("backend", true)]
    [InlineData("pool", false)]
    [InlineData("pool", true)]
    [InlineData("openai", false)]
    [InlineData("openai", true)]
    [InlineData("key-vault", false)]
    [InlineData("key-vault", true)]
    public async Task ExistingApiManagementRejectsUnconfirmedIdentityRegardlessOfCallOrder(string feature, bool adoptFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        });
        if (adoptFirst)
        {
            apim.PublishAsExisting("existing-apim", resourceGroup: null);
        }

        if (feature == "key-vault")
        {
            var vault = builder.AddAzureKeyVault("vault");
            apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));
        }
        else if (feature == "openai")
        {
            var deployment = builder.AddAzureOpenAI("openai").AddDeployment("chat", "gpt-5-mini", "2025-08-07");
            var backend = apim.AddAzureOpenAIBackend("openai-backend", deployment);
            apim.AddOpenAIApi("chat-api", "chat").WithBackend(backend);
        }
        else
        {
            var backend = apim.AddBackend("catalog-backend", ReferenceExpression.Create($"https://example.com"), new()
            {
                ManagedIdentityResource = "api://catalog",
            });
            var api = apim.AddApi("catalog-api", "catalog");
            if (feature == "pool")
            {
                api.WithBackend(apim.AddBackendPool("catalog-pool").WithBackend(backend));
            }
            else
            {
                api.WithBackend(backend);
            }
        }

        if (!adoptFirst)
        {
            apim.PublishAsExisting("existing-apim", resourceGroup: null);
        }

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Equal(
            "Existing API Management resource 'apim' uses managed-identity backends or Key Vault-backed named values, " +
            "but its user-assigned identity is not configured as an existing resource. " +
            "Supply an already-attached identity using WithAzureUserAssignedIdentity(...) and configure it with PublishAsExisting(...). " +
            "Aspire does not attach identities to existing services.",
            exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingApiManagementAllowsKeyVaultWithSuppliedIdentityRegardlessOfCallOrder(bool configureIdentityFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        });
        var defaultIdentity = apim.Resource.Identity;
        var identity = builder.AddAzureUserAssignedIdentity("shared-identity")
            .PublishAsExisting("shared-identity", resourceGroup: null);
        if (configureIdentityFirst)
        {
            ConfigureIdentity();
        }

        var vault = builder.AddAzureKeyVault("vault");
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));
        apim.PublishAsExisting("existing-apim", resourceGroup: null);
        if (!configureIdentityFirst)
        {
            ConfigureIdentity();
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.True(apim.Resource.IsExisting());
        Assert.True(apim.Resource.Identity.IsExisting());
        Assert.Same(identity.Resource, apim.Resource.Identity);
        Assert.DoesNotContain(defaultIdentity, builder.Resources);
        Assert.Empty(defaultIdentity.Annotations.OfType<RoleAssignmentAnnotation>());
        var roles = Assert.Single(builder.Resources.OfType<AzureRoleAssignmentResource>());
        Assert.Same(identity.Resource, roles.IdentityResource);
        Assert.Contains(roles, apim.Resource.References);

        void ConfigureIdentity()
        {
            Assert.Same(apim, apim.WithAzureUserAssignedIdentity(identity));
        }
    }

    [Fact]
    public void SuppliedIdentityRejectsNullArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("identity");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        Assert.Throws<ArgumentNullException>(() =>
            AzureApiManagementExtensions.WithAzureUserAssignedIdentity(null!, identity));
        Assert.Throws<ArgumentNullException>(() => apim.WithAzureUserAssignedIdentity(null!));
    }

    [Fact]
    public async Task ExistingApiManagementRejectsSuppliedNewIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("new-identity");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).WithAzureUserAssignedIdentity(identity)
            .PublishAsExisting("existing-apim", resourceGroup: null);
        var backend = apim.AddBackend("catalog-backend", ReferenceExpression.Create($"https://example.com"), new()
        {
            ManagedIdentityResource = "api://catalog",
        });
        apim.AddApi("catalog-api", "catalog").WithBackend(backend);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("user-assigned identity is not configured as an existing resource", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppliedIdentityKeepsInferredBackendAndKeyVaultRolesRegardlessOfCallOrder(bool identityFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var identity = builder.AddAzureUserAssignedIdentity("shared-identity");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var defaultIdentity = apim.Resource.Identity;
        if (identityFirst)
        {
            apim.WithAzureUserAssignedIdentity(identity);
        }

        var storage = builder.AddAzureStorage("storage");
        var backend = apim.AddBlobStorageBackend("blob-backend", storage.AddBlobs("blobs"));
        apim.AddApi("blob-api", "blob").WithBackend(backend);
        var vault = builder.AddAzureKeyVault("vault");
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));
        if (!identityFirst)
        {
            apim.WithAzureUserAssignedIdentity(identity);
        }
        apim.WithAzureUserAssignedIdentity(identity);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Same(identity.Resource, apim.Resource.Identity);
        Assert.Same(identity.Resource, apim.Resource.PrincipalId.Resource);
        Assert.Same(identity.Resource, apim.Resource.Annotations.OfType<AppIdentityAnnotation>().Single().IdentityResource);
        Assert.DoesNotContain(defaultIdentity, builder.Resources);
        Assert.Empty(defaultIdentity.Annotations.OfType<RoleAssignmentAnnotation>());
        var roles = builder.Resources.OfType<AzureRoleAssignmentResource>().OrderBy(role => role.Name).ToArray();
        Assert.Collection(roles,
            role =>
            {
                Assert.Same(storage.Resource, role.TargetAzureResource);
                Assert.Same(identity.Resource, role.IdentityResource);
            },
            role =>
            {
                Assert.Same(vault.Resource, role.TargetAzureResource);
                Assert.Same(identity.Resource, role.IdentityResource);
            });
        var (_, bicep) = await GetManifestWithBicep(apim.Resource, skipPreparer: true);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task ReplacingSharedIdentityMovesOnlyThisServicesInferredRoles()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var sharedIdentity = builder.AddAzureUserAssignedIdentity("shared-identity");
        var replacementIdentity = builder.AddAzureUserAssignedIdentity("replacement-identity");
        var first = builder.AddAzureApiManagement("first", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        }).WithAzureUserAssignedIdentity(sharedIdentity);
        var second = builder.AddAzureApiManagement("second", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        }).WithAzureUserAssignedIdentity(sharedIdentity);
        var firstVault = builder.AddAzureKeyVault("first-vault");
        var secondVault = builder.AddAzureKeyVault("second-vault");
        first.AddKeyVaultNamedValue("first-secret", firstVault.GetSecret("secret"));
        second.AddKeyVaultNamedValue("second-secret", secondVault.GetSecret("secret"));

        first.WithAzureUserAssignedIdentity(replacementIdentity);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Contains(sharedIdentity.Resource, builder.Resources);
        Assert.Same(secondVault.Resource, Assert.Single(sharedIdentity.Resource.Annotations.OfType<RoleAssignmentAnnotation>()).Target);
        Assert.Same(firstVault.Resource, Assert.Single(replacementIdentity.Resource.Annotations.OfType<RoleAssignmentAnnotation>()).Target);
        Assert.Contains(replacementIdentity.Resource, first.Resource.References);
        Assert.DoesNotContain(sharedIdentity.Resource, first.Resource.References);
        Assert.Contains(sharedIdentity.Resource, second.Resource.References);
    }

    [Fact]
    public async Task ExistingApiManagementDoesNotRequireIdentityConfirmationInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("existing-apim", resourceGroup: null);
        var backend = apim.AddBackend("catalog-backend", ReferenceExpression.Create($"https://example.com"), new()
        {
            ManagedIdentityResource = "api://catalog",
        });
        apim.AddApi("catalog-api", "catalog").WithBackend(backend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Empty(builder.Resources);
        Assert.True(apim.Resource.HasDefaultIdentity);
    }

    [Fact]
    public async Task ExistingApiManagementUsesUserAssignedIdentityWithoutMutatingService()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("existing-apim", resourceGroup: null);
        var identity = builder.AddAzureUserAssignedIdentity("apim-existing-identity")
            .PublishAsExisting("shared-identity", resourceGroup: null);
        apim.WithAzureUserAssignedIdentity(identity);
        var backend = apim.AddBackend(
            "catalog-backend",
            ReferenceExpression.Create($"https://example.com"),
            new()
            {
                ManagedIdentityResource = "api://catalog",
            });
        apim.AddApi("catalog-api", "catalog").WithBackend(backend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource, skipPreparer: true);
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task ExistingApiManagementCanUseExistingUserAssignedIdentityAndKeyVaultNamedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("existing-apim", resourceGroup: null);
        var identity = builder.AddAzureUserAssignedIdentity("apim-existing-identity")
            .PublishAsExisting("shared-identity", resourceGroup: "identity-resources");
        apim.WithAzureUserAssignedIdentity(identity);
        var vault = builder.AddAzureKeyVault("vault")
            .PublishAsExisting("shared-vault", resourceGroup: "security-resources");
        apim.AddKeyVaultNamedValue("upstream-secret", vault.GetSecret("upstream-secret"));
        var backend = apim.AddBackend(
            "catalog-backend",
            ReferenceExpression.Create($"https://example.com"),
            new()
            {
                ManagedIdentityResource = "api://catalog",
            });
        apim.AddApi("catalog-api", "catalog").WithBackend(backend);

        await VerifyAllAzureBicep(builder);
    }

    [Fact]
    public async Task ExistingApiManagementRejectsCrossResourceGroupDiagnostics()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var insights = builder.AddAzureApplicationInsights("insights");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Developer,
        }).PublishAsExisting("existing-apim", resourceGroup: "shared-infrastructure")
            .WithApplicationInsights(insights);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("cannot configure diagnostics", exception.Message);
    }

    [Fact]
    public async Task InternalContainerAppBackendRequiresSameVirtualNetworkEvenWhenIngressIsExternal()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var backendVnet = builder.AddAzureVirtualNetwork("backend-vnet");
        var backendSubnet = backendVnet.AddSubnet("backend-subnet", "10.0.0.0/23");
        var apimVnet = builder.AddAzureVirtualNetwork("apim-vnet");
        var apimSubnet = apimVnet.AddSubnet("apim-subnet", "10.1.0.0/24");
        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(backendSubnet)
            .WithInternalLoadBalancer(backendVnet);
        var backend = builder.AddDotnetProject("backend", "project.csproj", options => options.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(environment);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
            Sku = AzureApiManagementSku.Premium,
        }).WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
        apim.AddApi("catalog-api", backend, "catalog");

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("must be injected into the same virtual network", exception.Message);
    }

    [Fact]
    public void GeneratedIdentifiersAreBoundedAndStable()
    {
        var value = new string('a', 100);

        var identifier = AzureApiManagementExtensions.CreateBoundedIdentifier(value, 80);

        Assert.Equal(80, identifier.Length);
        Assert.Equal(identifier, AzureApiManagementExtensions.CreateBoundedIdentifier(value, 80));
        Assert.StartsWith(new string('a', 71), identifier, StringComparison.Ordinal);
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
            PublisherName = "Contoso APIs",
        });
        var exception = Assert.Throws<InvalidOperationException>(() => apim.AddFoundryBackend("embedding-backend", model));

        Assert.Contains("Only OpenAI-format deployments", exception.Message);
    }

    [Fact]
    public async Task BlobStorageBackendPoolUsesManagedIdentityAndRoleAssignments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var primary = builder.AddAzureStorage("storage-primary").AddBlobs("blobs-primary");
        var secondary = builder.AddAzureStorage("storage-secondary").AddBlobs("blobs-secondary");
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var primaryBackend = apim.AddBlobStorageBackend("blob-primary-backend", primary);
        var secondaryBackend = apim.AddBlobStorageBackend("blob-secondary-backend", secondary);
        var pool = apim.AddBackendPool("blob-pool")
            .WithBackend(primaryBackend, weight: 3)
            .WithBackend(secondaryBackend);
        apim.AddApi("blob-api", "blobs", subscriptionRequired: false)
            .WithBackend(pool)
            .WithInboundPolicy("<set-header name=\"x-ms-version\" exists-action=\"override\"><value>2023-11-03</value></set-header>");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(apim.Resource);
        var manifestJson = manifest.ToJsonString();

        Assert.Contains("storage-primary.outputs.blobEndpoint", manifestJson);
        Assert.Contains("storage-secondary.outputs.blobEndpoint", manifestJson);
        Assert.Contains("authentication-managed-identity resource=\"https://storage.azure.com/\" client-id=", bicep);
        var roles = builder.Resources.OfType<AzureRoleAssignmentResource>().ToArray();
        Assert.Equal(2, roles.Length);
        foreach (var role in roles)
        {
            Assert.Same(apim.Resource.Identity, role.IdentityResource);
            var (_, roleBicep) = await GetManifestWithBicep(role, skipPreparer: true);
            Assert.Contains("2a2b9908-6ea1-4ae2-8e65-a410df84e7d1", roleBicep);
        }
    }

    [Fact]
    public async Task GenericBackendOptionsGenerateBackendConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var backend = apim.AddBackend(
            "custom-backend",
            ReferenceExpression.Create($"https://example.com/base"),
            new AzureApiManagementBackendOptions
            {
                Title = "Custom SOAP backend",
                Protocol = AzureApiManagementBackendProtocol.Soap,
                ValidateCertificateName = false,
                CircuitBreaker = new AzureApiManagementCircuitBreakerOptions
                {
                    Name = "serverErrors",
                    FailureCount = 3,
                    FailureIntervalSeconds = 30,
                    TripDurationSeconds = 60,
                    StatusCodeRanges = [new(500, 599)],
                },
            },
            backendName: "custom\"backend");
        apim.AddApi("custom-api", "custom")
            .WithBackend(backend);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(apim.Resource);

        Assert.Contains("protocol: 'soap'", bicep);
        Assert.Contains("validateCertificateName: false", bicep);
        Assert.Contains("name: 'serverErrors'", bicep);
        Assert.Contains("interval: 'PT30S'", bicep);
        Assert.Contains("tripDuration: 'PT1M'", bicep);
        Assert.Contains("backend-id=\"custom&quot;backend\"", bicep);
    }

    [Fact]
    public void BackendPoolRejectsDifferentManagedIdentityResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });
        var storageBackend = apim.AddBackend(
            "storage",
            ReferenceExpression.Create($"https://storage.example.com"),
            new AzureApiManagementBackendOptions
            {
                ManagedIdentityResource = "https://storage.azure.com/",
            });
        var cognitiveBackend = apim.AddBackend(
            "cognitive",
            ReferenceExpression.Create($"https://cognitive.example.com"),
            new AzureApiManagementBackendOptions
            {
                ManagedIdentityResource = "https://cognitiveservices.azure.com",
            });
        var pool = apim.AddBackendPool("pool").WithBackend(storageBackend);

        var exception = Assert.Throws<InvalidOperationException>(() => pool.WithBackend(cognitiveBackend));

        Assert.Contains("must use the same managed-identity resource URI", exception.Message);
    }

    [Fact]
    public void BackendConfigurationEnforcesApiManagementLimits()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var apim = builder.AddAzureApiManagement("apim", new()
        {
            PublisherEmail = "api-owners@example.com",
            PublisherName = "Contoso APIs",
        });

        var invalidTitle = Assert.Throws<ArgumentException>(() => apim.AddBackend(
            "invalid-title",
            ReferenceExpression.Create($"https://example.com"),
            new AzureApiManagementBackendOptions
            {
                Title = "",
            }));
        var invalidRange = Assert.Throws<ArgumentOutOfRangeException>(() => apim.AddBackend(
            "invalid-range",
            ReferenceExpression.Create($"https://example.com"),
            new AzureApiManagementBackendOptions
            {
                CircuitBreaker = new AzureApiManagementCircuitBreakerOptions
                {
                    StatusCodeRanges = [new(199, 200)],
                },
            }));
        var tooManyRanges = Assert.Throws<ArgumentException>(() => apim.AddBackend(
            "too-many-ranges",
            ReferenceExpression.Create($"https://example.com"),
            new AzureApiManagementBackendOptions
            {
                CircuitBreaker = new AzureApiManagementCircuitBreakerOptions
                {
                    StatusCodeRanges = Enumerable.Repeat(new AzureApiManagementStatusCodeRange(500, 500), 11).ToArray(),
                },
            }));

        Assert.Contains("backend title", invalidTitle.Message);
        Assert.Contains("between 200 and 599", invalidRange.Message);
        Assert.Contains("more than 10", tooManyRanges.Message);
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

        var privateDns = Assert.Single(builder.Resources.OfType<AzurePrivateDnsZoneResource>());
        Assert.Equal("env-private-dns", privateDns.Name);
        Assert.Null(privateDns.ZoneName);
        var ingress = Assert.IsAssignableFrom<IAzureInternalIngressResource>(environment.Resource);
        var aRecord = Assert.Single(privateDns.ARecords);
        Assert.Equal("*", aRecord.Name);
        Assert.Equal(ingress.StaticIp.ValueExpression, aRecord.Address.ValueExpression);
        var link = Assert.Single(builder.Resources.OfType<AzurePrivateDnsZoneVNetLinkResource>());
        Assert.Same(privateDns, link.Parent);
        Assert.Same(vnet.Resource, link.VNet);
        Assert.Same(link, Assert.Single(privateDns.VNetLinks).Value);

        var (_, environmentBicep) = await GetManifestWithBicep(environment.Resource);
        var (_, privateDnsBicep) = await GetManifestWithBicep(privateDns);

        Assert.Same(vnet.Resource, environment.Resource.Annotations.OfType<InternalLoadBalancerAnnotation>().Single().VirtualNetwork);
        Assert.Contains("internal: true", environmentBicep);
        Assert.Contains("output AZURE_CONTAINER_APPS_ENVIRONMENT_STATIC_IP string", environmentBicep);
        Assert.Contains("Microsoft.Network/privateDnsZones@2024-06-01", privateDnsBicep);
        Assert.Contains("name: '*'", privateDnsBicep);
        Assert.Contains("ipv4Address:", privateDnsBicep);
        Assert.Contains("Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01", privateDnsBicep);
        await Verify(privateDnsBicep, "bicep");
    }

    [Fact]
    public async Task InternalContainerAppEnvironmentRevalidatesSubnetDelegationDuringGeneration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var subnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(subnet)
            .WithInternalLoadBalancer(vnet);
        subnet.WithServiceDelegation("Microsoft.Web/serverFarms");
        var privateDns = Assert.Single(builder.Resources.OfType<AzurePrivateDnsZoneResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetManifestWithBicep(privateDns));

        Assert.Equal(
            "The delegated subnet for Azure resource 'env' must remain delegated to " +
            "'Microsoft.App/environments' for an internal load balancer, but it is " +
            "delegated to 'Microsoft.Web/serverFarms'.",
            exception.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(64)]
    public void InternalContainerAppEnvironmentBoundsPrivateDnsResourceName(int virtualNetworkNameLength)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork(new string('v', virtualNetworkNameLength));
        var subnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        var environmentName = new string('e', 53);

        builder.AddAzureContainerAppEnvironment(environmentName)
            .WithDelegatedSubnet(subnet)
            .WithInternalLoadBalancer(vnet);

        var privateDns = Assert.Single(builder.Resources.OfType<AzurePrivateDnsZoneResource>());

        Assert.Equal(64, privateDns.Name.Length);
        Assert.StartsWith($"{environmentName}-p", privateDns.Name, StringComparison.Ordinal);
        Assert.Matches("-[0-9a-f]{8}$", privateDns.Name);
        var link = Assert.Single(builder.Resources.OfType<AzurePrivateDnsZoneVNetLinkResource>());
        Assert.Equal(64, link.Name.Length);
        Assert.StartsWith($"{environmentName}-p", link.Name, StringComparison.Ordinal);
        Assert.Matches("-[0-9a-f]{8}$", link.Name);
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

    private sealed class CustomKeyVaultResource(string name)
        : AzureBicepResource(
            name,
            templateString:
            """
            output name string = 'vault'
            output vaultUri string = 'https://vault.vault.azure.net/'
            """),
        IAzureKeyVaultResource
    {
        public BicepOutputReference VaultUriOutputReference => new("vaultUri", this);

        public BicepOutputReference NameOutputReference => new("name", this);

        public Func<IAzureKeyVaultSecretReference, CancellationToken, Task<string?>>? SecretResolver { get; set; }

        public IAzureKeyVaultSecretReference GetSecret(string secretName) => new CustomKeyVaultSecretReference(secretName, this);
    }

    private sealed class CustomKeyVaultSecretReference(
        string secretName,
        CustomKeyVaultResource resource) : IAzureKeyVaultSecretReference
    {
        public string SecretName { get; } = secretName;

        public IAzureKeyVaultResource Resource { get; } = resource;

        public IResource? SecretOwner { get; set; }

        string IManifestExpressionProvider.ValueExpression => $"{{{resource.Name}.secrets.{SecretName}}}";

        ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Secret '{SecretName}' was not resolved.");
    }
}
