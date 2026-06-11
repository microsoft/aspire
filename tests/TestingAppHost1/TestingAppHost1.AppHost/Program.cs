// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

builder.Configuration["ConnectionStrings:cs"] = "testconnection";

builder.AddConnectionString("cs");

builder.AddConnectionString("cs2", ReferenceExpression.Create($"Value={builder.AddParameter("p", "this is a value")}"));

if (args.Contains("--add-redis"))
{
    builder.AddRedis("redis1")
        // Disable certificate features to avoid extra arguments
        .WithoutHttpsCertificate()
        .WithCertificateTrustScope(CertificateTrustScope.None);
}

var webApp = builder.AddProject<Projects.TestingAppHost1_MyWebApp>("mywebapp1")
    .WithEnvironment("APP_HOST_ARG", builder.Configuration["APP_HOST_ARG"])
    .WithEnvironment("LAUNCH_PROFILE_VAR_FROM_APP_HOST", builder.Configuration["LAUNCH_PROFILE_VAR_FROM_APP_HOST"]);

if (builder.Configuration.GetValue("USE_HTTPS", false))
{
    webApp.WithExternalHttpEndpoints();
}

builder.AddProject<Projects.TestingAppHost1_MyWorker>("myworker1")
    .WithEndpoint(name: "myendpoint1", env: "myendpoint1_port");

if (args.Contains("--add-unknown-container"))
{
    var failsToStart = builder.AddContainer("fails-to-start", $"{Guid.NewGuid()}/does/not/exist");
    builder.AddExecutable("app", "cmd", ".")
        .WaitFor(failsToStart)
        .WithHttpEndpoint()
        .WithHttpHealthCheck();
}

if (args.Contains("--crash-before-build"))
{
    throw new InvalidOperationException("Crashing: before-build.");
}

var app = builder.Build();

if (args.Contains("--crash-after-build"))
{
    throw new InvalidOperationException("Crashing: after-build.");
}

await app.StartAsync();

if (args.Contains("--wait-for-healthy"))
{
    // Wait indefinitely until redis becomes healthy.
    var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
    await notifications.WaitForResourceHealthyAsync("redis1");
}

if (args.Contains("--crash-after-start"))
{
    throw new InvalidOperationException("Crashing: after-start.");
}

await app.WaitForShutdownAsync();

if (args.Contains("--crash-after-shutdown"))
{
    throw new InvalidOperationException("Crashing after-shutdown.");
}
