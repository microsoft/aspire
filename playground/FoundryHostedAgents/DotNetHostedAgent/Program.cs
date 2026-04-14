// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Data.Common;
using Azure.AI.AgentServer.AgentFramework.Extensions;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

string chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat")
    ?? throw new InvalidOperationException("ConnectionStrings__chat is not set.");

DbConnectionStringBuilder chatConnectionBuilder = new()
{
    ConnectionString = chatConnectionString,
};

string endpoint = GetRequiredConnectionValue(chatConnectionBuilder, "Endpoint");
string deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? openAiEndpoint) || openAiEndpoint is null)
{
    throw new InvalidOperationException("ConnectionStrings__chat contains an invalid Endpoint value.");
}

Console.WriteLine($"OpenAI Endpoint: {openAiEndpoint}");
Console.WriteLine($"Model Deployment: {deploymentName}");

// Read the port from environment variable (set by Aspire), default to 8088
string? portString = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT");
int port = int.TryParse(portString, out int parsedPort) ? parsedPort : 8088;

[Description("Get a weather forecast")]
WeatherForecast[]? GetWeatherForecast()
{
    string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
}

DefaultAzureCredential credential = new();

IChatClient chatClient = new AzureOpenAIClient(openAiEndpoint, credential)
    .GetChatClient(deploymentName)
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(sourceName: "Agents", configure: cfg => cfg.EnableSensitiveData = true)
    .Build();
 
AIAgent agent = chatClient.AsAIAgent(
    name: "WeatherAgent",
    instructions: """You are the Weather Intelligence Agent that can return weather forecast using your tools.""",
    tools: [AIFunctionFactory.Create(GetWeatherForecast)])
    .AsBuilder()
    .UseOpenTelemetry(sourceName: "Agents", configure: cfg => cfg.EnableSensitiveData = true)
    .Build();

Console.WriteLine($"Weather Agent Server running on http://localhost:{port}");
await agent.RunAIAgentAsync(telemetrySourceName: "Agents");

string GetRequiredConnectionValue(DbConnectionStringBuilder connectionBuilder, string key)
{
    if (!connectionBuilder.TryGetValue(key, out object? rawValue) || rawValue is null)
    {
        throw new InvalidOperationException($"ConnectionStrings__chat is missing '{key}'.");
    }

    string? value = rawValue.ToString();

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"ConnectionStrings__chat has an empty '{key}' value.");
    }

    return value;
}

internal sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
