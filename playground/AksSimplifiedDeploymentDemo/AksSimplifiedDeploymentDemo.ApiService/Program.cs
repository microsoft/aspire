// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

// Endpoints used to validate the WithSimplifiedDeployment pit-of-success flow against a live
// AKS deployment.

static object BuildIdentity(string surface) => new
{
    service = "AksSimplifiedDeploymentDemo.ApiService",
    surface,
    machineName = Environment.MachineName,
    podIp = Environment.GetEnvironmentVariable("POD_IP"),
    timestampUtc = DateTimeOffset.UtcNow
};

app.MapGet("/", () => Results.Ok(BuildIdentity("root")));
app.MapGet("/api", () => Results.Ok(BuildIdentity("api")));

// Surfaces the AGC forwarded-protocol header so a single `curl /tls` call confirms that
// the HTTPS listener terminated TLS and forwarded a plain HTTP request to the pod with
// X-Forwarded-Proto=https — exercising both the 301 redirect (Phase 1) and the
// cert-manager-issued cert.
app.MapGet("/tls", (HttpContext ctx) => Results.Ok(new
{
    forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].ToString(),
    host = ctx.Request.Host.Value,
    scheme = ctx.Request.Scheme,
    isHttps = ctx.Request.IsHttps
}));

app.Run();
