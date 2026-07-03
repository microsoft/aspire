// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var aca = builder.AddAzureContainerAppEnvironment("env");

var foundry = builder.AddFoundry("aifmyfoundry");
var project = foundry.AddProject("projmyproject");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt5);

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

var webSearch = project.AddWebSearchTool("websearch");

// --- Prompt Agents ---

var researchAgent = project.AddPromptAgent("research-agent", chat,
    instructions: """
        You are a research assistant. When asked a question:
        1. Use Bing grounding to search the web for current information
        2. Use the code interpreter to analyze data or perform calculations
        Always cite your sources and be thorough in your analysis.
        """)
    .WithTool(aiSearchTool)
    .WithTool(codeInterpreter);

var jokerAgent = project.AddPromptAgent("joker-agent", chat,
    instructions: """
        You are a hilarious comedian. Tell jokes, be witty, and make people laugh.
        If someone asks you to analyze something, use the code interpreter to
        create funny charts or calculations about the topic.
        """);

var searchAgent = project.AddPromptAgent("searchagent", chat,
    instructions: """
        You are an agent capable of searching the web for information.
        """)
    .WithTool(webSearch);

// --- Hosted Agents ---

builder.AddPythonApp("weather-python", "../app", "main.py")
    .WithUv()
    .WithReference(chat).WaitFor(chat)
    .AsHostedAgent(project);

builder.AddProject<Projects.DotNetHostedAgent>("weather-dotnet")
    .WithHttpEndpoint(targetPort: 9000)
    .WithReference(chat).WaitFor(chat)
    .WithReference(searchAgent).WaitFor(searchAgent)
    .AsHostedAgent(project);

builder.AddProject<Projects.PromptAgentChat>("chat-app")
    .WithExternalHttpEndpoints()
    .WithComputeEnvironment(aca)
    .WithReference(jokerAgent).WaitFor(jokerAgent)
    .WithReference(researchAgent).WaitFor(researchAgent);

builder.Build().Run();
