// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAPIM001
#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.Azure;
using Azure.Provisioning.Network;

var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddProject<Projects.AzureApiManagement_ApiService>("catalog");

IResourceBuilder<AzureSubnetResource>? apimSubnet = null;

if (builder.ExecutionContext.IsPublishMode)
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

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    PublisherName = "Aspire APIM Playground",
    Sku = AzureApiManagementSku.Premium,
}).WithInboundPolicy("""
    <set-header name="x-powered-by" exists-action="override">
      <value>Aspire</value>
    </set-header>
    """);

if (apimSubnet is not null)
{
    // APIM keeps the public gateway while reaching the internal-only Container App through the VNet.
    apim.WithClassicVirtualNetwork(apimSubnet, AzureApiManagementVirtualNetworkMode.External);
}

var catalogApi = apim.AddApi(
    "catalog-api",
    catalog,
    path: "catalog",
    displayName: "Catalog API")
    .WithInboundPolicy("""
        <rate-limit calls="100" renewal-period="60" />
        """);

catalogApi.AddOperation(
    "get-product",
    method: "GET",
    urlTemplate: "/products/{id}",
    displayName: "Get product");

builder.Build().Run();
