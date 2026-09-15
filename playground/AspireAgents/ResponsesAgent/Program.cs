// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Data.Common;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

var chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat")
    ?? throw new InvalidOperationException("ConnectionStrings__chat is not set.");

DbConnectionStringBuilder chatConnectionBuilder = new() { ConnectionString = chatConnectionString };
var endpoint = GetRequiredConnectionValue(chatConnectionBuilder, "Endpoint");
var deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");

const string AgentName = "responses-agent";

var chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deploymentName)
    .AsIChatClient();

builder.Services.AddChatClient(chatClient);

[Description("Get a weather forecast for a location.")]
static string GetWeather([Description("The location to get the weather for.")] string location)
{
    return $"The weather in {location} is sunny with a high of 22 C.";
}

builder.AddAIAgent(AgentName, "You are a concise weather agent. Use tools when users ask for forecasts.")
    .WithAITool(AIFunctionFactory.Create(GetWeather, name: "get_weather"));

builder.Services.AddOpenAIResponses();
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<McpWeatherTools>();

var app = builder.Build();

app.MapOpenAIResponses();
app.MapMcp("/mcp");
app.MapGet("/health", () => Results.Ok("Healthy"));

app.Run();

static string GetRequiredConnectionValue(DbConnectionStringBuilder connectionBuilder, string key)
{
    if (!connectionBuilder.TryGetValue(key, out var rawValue) || rawValue is null)
    {
        throw new InvalidOperationException($"Connection string is missing '{key}'.");
    }

    var value = rawValue.ToString();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Connection string has an empty '{key}' value.");
    }

    return value;
}

[McpServerToolType]
public sealed class McpWeatherTools
{
    [McpServerTool(Name = "get_weather")]
    [Description("Returns a sample weather forecast.")]
    public static string GetWeather([Description("The location to forecast.")] string location)
    {
        return $"The weather in {location} is sunny with a high of 22 C.";
    }
}
