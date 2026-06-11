// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapGet("/", () => "Prompt Agent Chat - use /chat?message=... (joker) or /research?message=... (research agent with Bing)");

app.MapGet("/chat", async (string? message) =>
{
    return await InvokeAgentAsync("joker-agent", message ?? "Tell me a joke!");
});

app.MapGet("/research", async (string? message) =>
{
    return await InvokeAgentAsync("research-agent", message ?? "What are the latest Aspire features?");
});

static async Task<IResult> InvokeAgentAsync(string agentResourceName, string message)
{
    var environmentPrefix = agentResourceName.Replace('-', '_').ToUpperInvariant();
    var projectEndpoint = Environment.GetEnvironmentVariable($"{environmentPrefix}_PROJECTENDPOINT")
        ?? throw new InvalidOperationException($"{environmentPrefix}_PROJECTENDPOINT is not set.");
    var agentName = Environment.GetEnvironmentVariable($"{environmentPrefix}_AGENTNAME")
        ?? throw new InvalidOperationException($"{environmentPrefix}_AGENTNAME is not set.");

    var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());
    var agentRef = new AgentReference(name: agentName);
    var responseClient = projectClient.ProjectOpenAIClient.GetProjectResponsesClientForAgent(agentRef);
    var response = await responseClient.CreateResponseAsync(message);
    var outputText = response.Value.GetOutputText();

    return Results.Ok(new
    {
        Agent = agentName,
        Message = message,
        Response = outputText
    });
}

app.Run();
