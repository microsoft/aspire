// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

// Return known game-server info sourced from Aspire service-discovery environment variables.
// When running outside of Aspire, these variables are absent and the response reflects that.
app.MapGet("/servers", (IConfiguration config) =>
{
    // Aspire injects the referenced endpoint as an environment variable named
    // `services__godot-server__game__0`, but the .NET environment-variable configuration
    // provider rewrites `__` to `:` when building IConfiguration. So the configuration key
    // is `services:godot-server:game:0` (the double-underscore form never matches and is
    // always null). See playground/BlazorStandalone/README.md for the same convention.
    var serverEndpoint = config["services:godot-server:game:0"];
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
