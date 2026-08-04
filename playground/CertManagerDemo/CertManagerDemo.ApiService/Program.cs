// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

// Endpoints used to validate end-to-end TLS through AGC + cert-manager.
// /         - identity, useful for confirming routing reached the right pod
// /api      - target of the storefront-gw HTTPRoute (no path rewriting on the gateway)
// /admin    - target of the admin-gw HTTPRoute
// /tls      - reports whether the request reached us over HTTPS via X-Forwarded-Proto;
//             handy for confirming the cert chain end-to-end once a Let's Encrypt cert
//             has been issued and bound to the gateway listener.

static object BuildIdentity(string surface) => new
{
    service = "CertManagerDemo.ApiService",
    surface,
    machineName = Environment.MachineName,
    podIp = Environment.GetEnvironmentVariable("POD_IP"),
    timestampUtc = DateTimeOffset.UtcNow
};

app.MapGet("/", () => Results.Ok(BuildIdentity("root")));
app.MapGet("/api", () => Results.Ok(BuildIdentity("storefront")));
app.MapGet("/admin", () => Results.Ok(BuildIdentity("admin")));

app.MapGet("/tls", (HttpContext ctx) => Results.Ok(new
{
    // AGC terminates TLS and forwards the client scheme via X-Forwarded-Proto. If the
    // gateway listener has a Let's Encrypt cert bound by cert-manager, requests to the
    // public FQDN over https should land here with X-Forwarded-Proto=https.
    forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
    forwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString(),
    host = ctx.Request.Host.Value,
    scheme = ctx.Request.Scheme,
    isHttps = ctx.Request.IsHttps
}));

app.MapGet("/info", () => Results.Ok(new
{
    machineName = Environment.MachineName,
    osVersion = Environment.OSVersion.ToString(),
    processorCount = Environment.ProcessorCount,
    dotnetVersion = Environment.Version.ToString(),
    aspnetcoreEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
    podIp = Environment.GetEnvironmentVariable("POD_IP"),
    podName = Environment.GetEnvironmentVariable("POD_NAME"),
    nodeName = Environment.GetEnvironmentVariable("NODE_NAME")
}));

app.Run();
