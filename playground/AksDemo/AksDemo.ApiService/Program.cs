// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

// Simple endpoints to validate the Gateway -> HTTPRoute -> Service path through AGC.
// /         - returns identifying info so it's obvious which pod handled the request
// /hello/{name} - echoes a name so route prefixes can be exercised
// /info     - returns environment metadata useful for debugging

app.MapGet("/", () => Results.Ok(new
{
    service = "AksDemo.ApiService",
    machineName = Environment.MachineName,
    podIp = Environment.GetEnvironmentVariable("POD_IP"),
    timestampUtc = DateTimeOffset.UtcNow
}));

app.MapGet("/hello/{name}", (string name) => Results.Ok(new
{
    message = $"Hello, {name}!",
    machineName = Environment.MachineName
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
