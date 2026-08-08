// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);

var buildProbeArgument = args.FirstOrDefault(
    argument => argument.StartsWith("--block-apphost-build=", StringComparison.Ordinal));
if (buildProbeArgument is not null)
{
    var buildProbeId = buildProbeArgument["--block-apphost-build=".Length..];
    TestingAppHostBuildProbe.Configure(builder, buildProbeId);
}

if (args.Contains("--override-dashboard-testing-defaults"))
{
    builder.Configuration["DcpPublisher:RandomizePorts"] = "false";
    builder.Configuration["ASPNETCORE_URLS"] = "http://127.0.0.1:12345";
    builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://127.0.0.1:12346";
    builder.Configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://127.0.0.1:12347";
    builder.Configuration["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "http://127.0.0.1:12348";
    builder.Configuration["ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"] = "true";
    builder.Configuration["ASPIRE_INTERACTIVITY_ENABLED"] = "true";
}

if (args.Contains("--clear-apphost-browser-token"))
{
    // The frozen token is the one dashboard setting an AppHost could still change after the testing builder had
    // hardened things, because DashboardOptions does not read this key until the application starts. It is kept
    // separate from --override-dashboard-testing-defaults so that flag keeps asserting on a token.
    builder.Configuration["AppHost:BrowserToken"] = "";
}

if (args.Contains("--null-apphost-browser-token"))
{
    // Null and empty are distinct configuration values, but DashboardEventHandlers collapses both to Unsecured
    // frontend authentication through string.IsNullOrEmpty, so both need coverage.
    builder.Configuration["AppHost:BrowserToken"] = null;
}

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
    if (buildProbeArgument is not null)
    {
        var buildProbeId = buildProbeArgument["--block-apphost-build=".Length..];
        TestingAppHostBuildProbe.SignalEntryPointFailure(buildProbeId);
    }

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
