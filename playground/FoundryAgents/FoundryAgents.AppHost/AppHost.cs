// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

var aca = builder.AddAzureContainerAppEnvironment("env");

var foundry = builder.AddFoundry("aif-myfoundry");
var project = foundry.AddProject("proj-myproject")
    // workaround for https://github.com/microsoft/aspire/issues/15971
    .ConfigureInfrastructure(infra =>
    {
        var project = infra.GetProvisionableResources().OfType<CognitiveServicesProject>().Single();

        var foundryAccount = foundry.Resource.AddAsExistingResource(infra);

        var cogUserRa = foundryAccount.CreateRoleAssignment(CognitiveServicesBuiltInRole.CognitiveServicesUser, RoleManagementPrincipalType.ServicePrincipal, project.Identity.PrincipalId);
        // There's a bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
        cogUserRa.Name = BicepFunction.CreateGuid(foundryAccount.Id, project.Id, cogUserRa.RoleDefinitionId);
        infra.Add(cogUserRa);
    });
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

// --- Prompt agent tools ---

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

var codeInterpreter = project.AddCodeInterpreterTool("code-interp");

builder.AddPythonApp("weather-hosted-agent", "../app", "main.py")
    .WithUv()
    .WithReference(project).WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent(project);

builder.AddProject<Projects.DotNetHostedAgent>("proj-dotnet-hosted-agent")
    .WithHttpEndpoint(targetPort: 9000)
    .WithReference(project).WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent(project);

// --- Prompt Agents ---

var researchAgent = project.AddPromptAgent(chat, "research-agent",
    instructions: """
        You are a research assistant. When asked a question:
        1. Use Bing grounding to search the web for current information
        2. Use the code interpreter to analyze data or perform calculations
        Always cite your sources and be thorough in your analysis.
        """)
    .WithTool(aiSearchTool)
    .WithTool(codeInterpreter);

var jokerAgent = project.AddPromptAgent(chat, "joker-agent",
    instructions: """
        You are a hilarious comedian. Tell jokes, be witty, and make people laugh.
        If someone asks you to analyze something, use the code interpreter to
        create funny charts or calculations about the topic.
        """)
    .WithTool(codeInterpreter);

builder.AddProject<Projects.PromptAgentChat>("chat-app")
    .WithExternalHttpEndpoints()
    .WithComputeEnvironment(aca)
    .WithReference(jokerAgent).WaitFor(jokerAgent)
    .WithReference(researchAgent).WaitFor(researchAgent);

builder.Build().Run();
