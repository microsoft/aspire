// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Data.Common;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

var chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat")
    ?? throw new InvalidOperationException("ConnectionStrings__chat is not set.");

DbConnectionStringBuilder chatConnectionBuilder = new() { ConnectionString = chatConnectionString };
var endpoint = GetRequiredConnectionValue(chatConnectionBuilder, "Endpoint");
var deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("PORT") ?? "8080"}");

WeatherTools.Initialize(
    new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
        .GetChatClient(deploymentName)
        .AsIChatClient());

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<WeatherTools>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("Healthy"));
app.MapMcp("/mcp");

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
public sealed class WeatherTools
{
    private static IChatClient? s_chatClient;

    public static void Initialize(IChatClient chatClient)
    {
        s_chatClient = chatClient;
    }

    [McpServerTool(Name = "get_weather")]
    [Description("Uses the Foundry model deployment to answer a weather question.")]
    public static async Task<string> GetWeather([Description("The location or weather question to answer.")] string location)
    {
        var chatClient = s_chatClient ?? throw new InvalidOperationException("The chat client is not initialized.");
        var response = await chatClient.GetResponseAsync(
            $"You are a concise weather agent. Answer this weather request: {location}");

        return response.Text;
    }
}
