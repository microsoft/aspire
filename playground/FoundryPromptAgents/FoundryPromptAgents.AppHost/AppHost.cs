// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

// --- Foundry account & project ---

var foundry = builder.AddFoundry("aif-promptagents");
var project = foundry.AddProject("proj-promptagents")
    // workaround for https://github.com/microsoft/aspire/issues/15971
    .ConfigureInfrastructure(infra =>
    {
        var proj = infra.GetProvisionableResources().OfType<CognitiveServicesProject>().Single();
        var foundryAccount = foundry.Resource.AddAsExistingResource(infra);
        var cogUserRa = foundryAccount.CreateRoleAssignment(
            CognitiveServicesBuiltInRole.CognitiveServicesUser,
            RoleManagementPrincipalType.ServicePrincipal,
            proj.Identity.PrincipalId);
        // Bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
        cogUserRa.Name = BicepFunction.CreateGuid(foundryAccount.Id, proj.Id, cogUserRa.RoleDefinitionId);
        infra.Add(cogUserRa);
    });

// --- Model deployments ---

var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

// --- Azure AI Search (provisioned by Aspire, connected to Foundry) ---

var search = builder.AddAzureSearch("search")
    .ConfigureInfrastructure(infra =>
    {
        var searchService = infra.GetProvisionableResources()
            .OfType<Azure.Provisioning.Search.SearchService>()
            .Single();
        searchService.SearchSkuName = Azure.Provisioning.Search.SearchServiceSkuName.Free;
    });
var aiSearchTool = project.AddAISearchTool("aisearch-tool", indexName: "default")
    .WithReference(search);

// --- Bing Grounding (existing resource, connected to Foundry via resource ID) ---
// NOTE: Bing Grounding resources must be created manually in the Azure portal.
// See: https://portal.azure.com → Create "Bing Grounding" resource
// Then store the resource ID in user secrets:
//   dotnet user-secrets set "Parameters:bingResourceId" "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Bing/accounts/{name}"

var bingResourceId = builder.AddParameter("bingResourceId");
var bingTool = project.AddBingGroundingTool("bing-tool")
    .WithReference(bingResourceId);

// --- Built-in tools (no connection or provisioning needed) ---

var codeInterpreter = project.AddCodeInterpreterTool("code-interp");

// --- Prompt Agents ---

// Research agent: uses Bing for grounding + AI Search for indexed data + code interpreter
var researchAgent = project.AddPromptAgent(chat, "research-agent",
    instructions: """
        You are a research assistant. When asked a question:
        1. Use Bing grounding to search the web for current information
        2. Use the code interpreter to analyze data or perform calculations
        Always cite your sources and be thorough in your analysis.
        """,
    tools: [bingTool, aiSearchTool, codeInterpreter]);

// Joker agent: a simple agent with just code interpreter for fun
var jokerAgent = project.AddPromptAgent(chat, "joker-agent",
    instructions: """
        You are a hilarious comedian. Tell jokes, be witty, and make people laugh.
        If someone asks you to analyze something, use the code interpreter to
        create funny charts or calculations about the topic.
        """,
    tools: [codeInterpreter]);

// --- Consumer service that talks to the prompt agents ---

builder.AddProject<Projects.PromptAgentChat>("chat-app")
    .WithExternalHttpEndpoints()
    .WithReference(jokerAgent).WaitFor(jokerAgent)
    .WithReference(researchAgent).WaitFor(researchAgent);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
