// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable OPENAI001 // Responses API is experimental

using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureAIProjectClient("projmyproject");

var app = builder.Build();

app.MapGet("/", () => "Prompt Agent Chat - use /chat?message=... (joker) or /research?message=... (research agent with Bing)");

app.MapGet("/chat", async (AIProjectClient projectClient, string? message) =>
{
    return await InvokeAgentAsync(projectClient, "joker-agent", message ?? "Tell me a joke!");
});

app.MapGet("/research", async (AIProjectClient projectClient, string? message) =>
{
    return await InvokeAgentAsync(projectClient, "research-agent", message ?? "What are the latest Aspire features?");
});

static async Task<IResult> InvokeAgentAsync(AIProjectClient projectClient, string agentResourceName, string message)
{
    var environmentPrefix = agentResourceName.Replace('-', '_').ToUpperInvariant();
    var agentName = Environment.GetEnvironmentVariable($"{environmentPrefix}_AGENTNAME")
        ?? throw new InvalidOperationException($"{environmentPrefix}_AGENTNAME is not set.");

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
