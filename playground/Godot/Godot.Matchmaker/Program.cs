// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

// Return known game-server info sourced from Aspire service-discovery environment variables.
// When running outside of Aspire, these variables are absent and the response reflects that.
app.MapGet("/servers", (IConfiguration config) =>
{
    var serverEndpoint = config["services__godot-server__game__0"];
    var serverPort = Uri.TryCreate(serverEndpoint, UriKind.Absolute, out var endpointUri)
        ? endpointUri.Port
        : (int?)null;

    return Results.Ok(new
    {
        resourceName = "godot-server",
        port = serverPort,
        endpoint = serverEndpoint,
    });
});

app.Run();
