// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

// Echo what arrived through Traefik: the original host and the headers Traefik and its middlewares add.
app.MapGet("/", (HttpRequest request) => Describe(request));
app.MapGet("/v1/{**path}", (HttpRequest request) => Describe(request));

// Behind basic authentication and Keycloak single sign-on, on api.<domain>.
app.MapGet("/admin/{**path}", (HttpRequest request) => Describe(request));
app.MapGet("/me/{**path}", (HttpRequest request) => Describe(request));

// A long internal path, published as app.<domain>/forecast.
app.MapGet("/reports/weather/daily-forecast", (HttpRequest request) => Describe(request));

app.Run();

static object Describe(HttpRequest request) => new
{
    PublicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL"),
    Host = request.Host.Value,
    Path = request.Path.Value,
    // X-Forwarded-* from Traefik, X-Auth-Request-* from oauth2-proxy, X-Replaced-Path from the replacePath middleware.
    ForwardedHeaders = request.Headers
        .Where(h => h.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || h.Key.StartsWith("X-Auth-Request-", StringComparison.OrdinalIgnoreCase)
            || h.Key.Equals("X-Replaced-Path", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(h => h.Key, h => h.Value.ToString()),
};
