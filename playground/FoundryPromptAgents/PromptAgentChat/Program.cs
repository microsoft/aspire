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
    return await InvokeAgentAsync("research-agent", message ?? "What are the latest .NET Aspire features?");
});

static async Task<IResult> InvokeAgentAsync(string agentResourceName, string message)
{
    var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{agentResourceName}")
        ?? throw new InvalidOperationException($"ConnectionStrings__{agentResourceName} is not set.");

    var agentIndex = connectionString.IndexOf("/agents/", StringComparison.OrdinalIgnoreCase);
    if (agentIndex < 0)
    {
        throw new InvalidOperationException("Connection string doesn't contain '/agents/' segment.");
    }

    var projectEndpoint = connectionString[..agentIndex];
    var agentName = connectionString[(agentIndex + "/agents/".Length)..];

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
