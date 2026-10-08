// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AzureOpenAIEndToEnd.WebStory.Components;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// This sample targets an Azure OpenAI account, not a Foundry project.
// The account SDK consumes the connection properties supplied by WithReference(chat).
builder.Services.AddChatClient(_ =>
{
    var endpoint = builder.Configuration["CHAT_URI"] ?? throw new InvalidOperationException("CHAT_URI is required.");
    var deployment = builder.Configuration["CHAT_MODELNAME"] ?? throw new InvalidOperationException("CHAT_MODELNAME is required.");
    return new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential()).GetChatClient(deployment).AsIChatClient();
}).UseOpenTelemetry();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"))
    .WithMetrics(metrics => metrics.AddMeter("Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
