// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.AgentServer.Invocations;
using DotNetInvocationHostedAgent;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088"}");

builder.Services.AddSingleton<EchoAIAgent>();
builder.Services.AddInvocationsServer();
builder.Services.AddScoped<InvocationHandler, EchoInvocationHandler>();

var app = builder.Build();

app.MapInvocationsServer();
app.MapGet("/liveness", () => Results.Ok("Healthy"));
app.MapGet("/readiness", () => Results.Ok("Ready"));

app.Run();
