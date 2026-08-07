// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

// Report the game server's *configured* endpoint, sourced from Aspire service-discovery
// configuration.
//
// This is deliberately named `/configuration` and not `/servers`: Aspire allocates the endpoint's
// port when the application model is built, but `godot-server` is an explicit-start resource, so the
// port is usually allocated while nothing is listening on it. A route called `/servers` would imply
// the returned endpoint is live and joinable, which it is not. A real matchmaker would need genuine
// registration or a readiness probe before advertising a server to players; this playground only
// demonstrates that the endpoint reaches the matchmaker as configuration.
app.MapGet("/configuration", (IConfiguration config) =>
{
    // Aspire injects the referenced endpoint as an environment variable named
    // `services__godot-server__game__0`, but the .NET environment-variable configuration
    // provider rewrites `__` to `:` when building IConfiguration. So the configuration key
    // is `services:godot-server:game:0` (the double-underscore form never matches and is
    // always null). See playground/BlazorStandalone/README.md for the same convention.
    var configuredEndpoint = config["services:godot-server:game:0"];
    var configuredPort = Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpointUri)
        ? endpointUri.Port
        : (int?)null;

    return Results.Ok(new
    {
        resourceName = "godot-server",
        endpointConfigured = configuredEndpoint is not null,
        configuredPort,
        configuredEndpoint,
        // Stated explicitly so no caller mistakes an allocated port for a running game server.
        note = "Configured endpoint only. The godot-server resource is explicit-start, so this port may not be listening.",
    });
});

app.Run();
