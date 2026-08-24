// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAPIM001

using Aspire.Hosting.Azure;

var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddProject<Projects.AzureApiManagement_ApiService>("catalog")
    .WithExternalHttpEndpoints();

if (builder.ExecutionContext.IsPublishMode)
{
    var environment = builder.AddAzureContainerAppEnvironment("env");
    catalog.WithComputeEnvironment(environment);
}

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    PublisherName = "Aspire APIM Playground",
    Sku = AzureApiManagementSku.StandardV2,
}).WithInboundPolicy("""
    <set-header name="x-powered-by" exists-action="override">
      <value>Aspire</value>
    </set-header>
    """);

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
