// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAPIM001

using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var primaryFoundry = builder.AddFoundry("foundry-primary")
    // APIM is the only model consumer in this deployment and receives its own managed-identity role.
    .ClearDefaultRoleAssignments();
var primaryModel = primaryFoundry.AddDeployment(
    "chat-primary",
    FoundryModel.OpenAI.Gpt5Mini);

var secondaryFoundry = builder.AddFoundry("foundry-secondary")
    .ClearDefaultRoleAssignments();
var secondaryModel = secondaryFoundry.AddDeployment(
    "chat-secondary",
    FoundryModel.OpenAI.Gpt5Mini);

var apim = builder.AddAzureApiManagement("apim", new()
{
    PublisherEmail = "api-owners@example.com",
    PublisherName = "Aspire APIM OpenAI Playground",
    Sku = AzureApiManagementSku.StandardV2,
});

var primaryFoundryBackend = apim.AddFoundryBackend("foundry-primary-backend", primaryModel);
var secondaryFoundryBackend = apim.AddFoundryBackend("foundry-secondary-backend", secondaryModel);
var foundryPool = apim.AddBackendPool("foundry-pool")
    .WithBackend(primaryFoundryBackend, priority: 1, weight: 3)
    .WithBackend(secondaryFoundryBackend, priority: 1, weight: 1);

apim.AddOpenAIApi(
        "openai-api",
        path: "openai",
        displayName: "Load-balanced OpenAI API",
        subscriptionRequired: false)
    .WithBackend(foundryPool);

var primaryBlobBackend = apim.AddBackend(
    "blob-primary-backend",
    ReferenceExpression.Create($"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata"));
var secondaryBlobBackend = apim.AddBackend(
    "blob-secondary-backend",
    ReferenceExpression.Create($"https://dotnetcli.azureedge.net/dotnet/release-metadata"));
var blobPool = apim.AddBackendPool("blob-pool")
    .WithBackend(primaryBlobBackend, priority: 1, weight: 3)
    .WithBackend(secondaryBlobBackend, priority: 1, weight: 1);

var blobApi = apim.AddApi(
        "blob-api",
        path: "blobs",
        displayName: "Blob Storage origin and CDN failover API",
        subscriptionRequired: false)
    .WithBackend(blobPool);
blobApi.AddOperation(
    "release-index",
    method: "GET",
    urlTemplate: "/releases-index.json",
    displayName: ".NET release index");

builder.Build().Run();
