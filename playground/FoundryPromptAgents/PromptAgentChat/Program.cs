// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using Azure.AI.OpenAI;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapGet("/", () => "Prompt Agent Chat - use /chat?message=your+message to talk to the joker agent");

app.MapGet("/chat", async (string? message) =>
{
    message ??= "Tell me a joke!";

    // Read the agent connection string injected by Aspire
    // Format: Endpoint={projectEndpoint}/agents/{agentName}
    var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__joker-agent")
        ?? throw new InvalidOperationException("ConnectionStrings__joker-agent is not set.");

    var connectionBuilder = new DbConnectionStringBuilder { ConnectionString = connectionString };

    if (!connectionBuilder.TryGetValue("Endpoint", out var endpointObj) || endpointObj is null)
    {
        throw new InvalidOperationException("Connection string is missing 'Endpoint'.");
    }

    var fullEndpoint = endpointObj.ToString()!;
    var agentIndex = fullEndpoint.IndexOf("/agents/", StringComparison.OrdinalIgnoreCase);
    if (agentIndex < 0)
    {
        throw new InvalidOperationException("Connection string endpoint doesn't contain '/agents/' segment.");
    }

    var projectEndpoint = fullEndpoint[..agentIndex];
    var agentName = fullEndpoint[(agentIndex + "/agents/".Length)..];

    // Create an AzureOpenAI client pointing at the Foundry project endpoint.
    // The prompt agent is invoked via the OpenAI Responses API — Foundry routes
    // the request to the pre-configured agent identified by its name.
    var openAiClient = new AzureOpenAIClient(new Uri(projectEndpoint), new DefaultAzureCredential());

#pragma warning disable OPENAI001 // Responses API is in preview
    var responsesClient = openAiClient.GetResponsesClient();

    // Use the agent name as the "model" parameter — Foundry resolves it to the agent definition
    var response = await responsesClient.CreateResponseAsync(agentName, message);
    var outputText = response.Value.GetOutputText();
#pragma warning restore OPENAI001

    return Results.Ok(new
    {
        Agent = agentName,
        Message = message,
        Response = outputText
    });
});

app.Run();
