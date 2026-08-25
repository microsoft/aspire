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

apim.AddOpenAIApi(
        "openai-api",
        path: "openai",
        displayName: "Load-balanced OpenAI API",
        subscriptionRequired: false)
    .WithFoundryBackend(primaryModel, priority: 1, weight: 3)
    .WithFoundryBackend(secondaryModel, priority: 1, weight: 1);

builder.Build().Run();
