// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAPIM001
#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.Azure;
using Azure.Provisioning.Network;

var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddProject<Projects.AzureApiManagement_ApiService>("catalog");
var insights = builder.AddAzureApplicationInsights("insights");
var useOpenApiEndpoint =
    bool.TryParse(builder.Configuration["ApiManagement:UseOpenApiEndpoint"], out var configuredOpenApiEndpoint) &&
    configuredOpenApiEndpoint;

IResourceBuilder<AzureSubnetResource>? apimSubnet = null;

if (builder.ExecutionContext.IsPublishMode)
{
    if (useOpenApiEndpoint)
    {
        // APIM's control plane must be able to retrieve the OpenAPI document during deployment.
        var environment = builder.AddAzureContainerAppEnvironment("env");
        catalog.WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
    }
    else
    {
        var vnet = builder.AddAzureVirtualNetwork("vnet");
        var containerAppsSubnet = vnet.AddSubnet("container-apps-subnet", "10.0.0.0/23");
        apimSubnet = vnet.AddSubnet("apim-subnet", "10.0.2.0/24")
            // Classic APIM VNet injection requires these management, health-probe, and gateway rules.
            .AllowInbound(port: "3443", from: "ApiManagement", protocol: SecurityRuleProtocol.Tcp)
            .AllowInbound(port: "6390", from: AzureServiceTags.AzureLoadBalancer, protocol: SecurityRuleProtocol.Tcp)
            .AllowInbound(port: "443", from: AzureServiceTags.Internet, protocol: SecurityRuleProtocol.Tcp);

        var environment = builder.AddAzureContainerAppEnvironment("env")
            .WithDelegatedSubnet(containerAppsSubnet)
            .WithInternalLoadBalancer(vnet);
        catalog.WithComputeEnvironment(environment)
            .WithExternalHttpEndpoints();
    }
}

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    PublisherName = "Aspire APIM Playground",
    Sku = useOpenApiEndpoint ? AzureApiManagementSku.Developer : AzureApiManagementSku.Premium,
}).WithApplicationInsights(insights);

apim.AddNamedValue("gateway-name", "Aspire");
var standardHeaders = apim.AddPolicyFragment(
    "standard-headers",
    """
    <set-header name="x-powered-by" exists-action="override">
      <value>{{gateway-name}}</value>
    </set-header>
    """,
    description: "Adds headers shared by the playground APIs.");
apim.WithInboundPolicyFragment(standardHeaders);

if (apimSubnet is not null)
{
    // APIM keeps the public gateway while reaching the internal-only Container App through the VNet.
    apim.WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
}

var catalogApi = apim.AddApi(
    "catalog-api",
    catalog,
    path: "catalog",
    displayName: "Catalog API",
    subscriptionRequired: false)
    .WithInboundPolicy("<rate-limit calls=\"100\" renewal-period=\"60\" />");

if (useOpenApiEndpoint)
{
    catalogApi.WithOpenApiEndpoint();
}
else
{
    catalogApi.AddOperation(
        "get-product",
        method: "GET",
        urlTemplate: "/products/{id}",
        displayName: "Get product");
}

apim.AddProduct("catalog-product", "Catalog", new()
    {
        Description = "The catalog playground API.",
    })
    .WithApi(catalogApi)
    .AddSubscription("catalog-client", "Catalog playground client");

var customDomain = builder.Configuration["ApiManagement:CustomDomain"];
var certificateVaultName = builder.Configuration["ApiManagement:CertificateVaultName"];
var certificateSecretName = builder.Configuration["ApiManagement:CertificateSecretName"];
if (customDomain is not null && certificateVaultName is not null && certificateSecretName is not null)
{
    // The certificate must already exist as a PFX secret in Key Vault. Keeping this optional
    // lets the playground deploy unchanged when a DNS name and certificate are not available.
    var certificateVault = builder.AddAzureKeyVault("certificate-vault")
        .PublishAsExisting(certificateVaultName, resourceGroup: null);
    apim.WithCustomDomain(
        customDomain,
        certificateVault.GetSecret(certificateSecretName),
        defaultSslBinding: true);
}

builder.Build().Run();
