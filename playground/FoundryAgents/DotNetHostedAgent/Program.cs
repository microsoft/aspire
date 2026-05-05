// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Data.Common;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

string projectConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__proj-myproject")
    ?? throw new InvalidOperationException("ConnectionStrings__proj-myproject is not set.");

string chatConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__chat")
    ?? throw new InvalidOperationException("ConnectionStrings__chat is not set.");

DbConnectionStringBuilder projectConnectionBuilder = new() { ConnectionString = projectConnectionString };
DbConnectionStringBuilder chatConnectionBuilder = new() { ConnectionString = chatConnectionString };

string projectEndpoint = GetRequiredConnectionValue(projectConnectionBuilder, "Endpoint");
string deploymentName = GetRequiredConnectionValue(chatConnectionBuilder, "Deployment");

if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out Uri? projectUri) || projectUri is null)
{
    throw new InvalidOperationException("ConnectionStrings__proj-myproject contains an invalid Endpoint value.");
}

Console.WriteLine($"Project Endpoint: {projectUri}");
Console.WriteLine($"Model Deployment: {deploymentName}");

[Description("Get a weather forecast")]
WeatherForecast[] GetWeatherForecast()
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

AIAgent agent = new AIProjectClient(projectUri, credential)
    .AsAIAgent(
        model: deploymentName,
        name: "WeatherAgent",
        instructions: """You are the Weather Intelligence Agent that can return weather forecast using your tools.""",
        tools: [AIFunctionFactory.Create(GetWeatherForecast)]);

// Bind to the port allocated by Aspire via the DEFAULT_AD_PORT environment variable.
string port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{port}");
builder.Services.AddFoundryResponses(agent);

var app = builder.Build();

app.MapFoundryResponses();
app.MapGet("/liveness", () => Results.Ok("Healthy"));
app.MapGet("/readiness", () => Results.Ok("Ready"));

Console.WriteLine($"Weather Agent Server running on http://localhost:{port}");
app.Run();

string GetRequiredConnectionValue(DbConnectionStringBuilder connectionBuilder, string key)
{
    if (!connectionBuilder.TryGetValue(key, out object? rawValue) || rawValue is null)
    {
        throw new InvalidOperationException($"Connection string is missing '{key}'.");
    }

    string? value = rawValue.ToString();

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Connection string has an empty '{key}' value.");
    }

    return value;
}

internal sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
