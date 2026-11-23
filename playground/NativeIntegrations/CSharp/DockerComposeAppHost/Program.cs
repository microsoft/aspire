// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Native.Authoring;
using Aspire.Hosting.Native.Client;
using NativeIntegrations.DockerCompose;

// Share the actual web application and Dockerfile with the TypeScript example.
// --native-application-root selects that AppHost directory, not this build output.
var builder = DistributedApplication.CreateBuilder(["--operation=publish", .. args]);
var compose = builder.AddDockerComposeEnvironment("compose").WithDashboard(enabled: false);
var password = builder.AddConfiguredParameter("cache-password", secret: true);
var cache = builder.AddContainer("cache", "redis:7.4-alpine")
    .WithEnvironment("REDIS_PASSWORD", password)
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", "exec redis-server --requirepass \"$REDIS_PASSWORD\" --appendonly yes --appendfsync always --dir /data")
    .WithEndpoint("tcp", 6379, "redis")
    .WithComputeEnvironment(compose);
cache.WithVolume(builder.GetDefaultDataVolumeName(cache.Resource), "/data");
cache.WithConnectionString(ReferenceExpression.Create("redis://:", password.AsReference().EncodeUri(), "@",
    cache.GetEndpoint("tcp").HostAndPort));
cache.PublishAsDockerComposeService((_, service) =>
{
    service.Healthcheck = new(["CMD-SHELL", "REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli ping | grep -q PONG"], "1s", "3s", 30);
    service.Restart = "unless-stopped";
});
var web = builder.AddExecutable("web", Environment.ProcessPath!, Path.Combine(builder.AppHostDirectory, "web"))
    .WithArgs("server.mts")
    .WithReference(cache)
    .WithEndpoint("http", 8080, "http")
    .WithExternalHttpEndpoints()
    .WaitFor(cache)
    .WithComputeEnvironment(compose);
web.PublishAsDockerComposeService((_, service) =>
{
    service.Image = builder.Configuration["image"] ?? "aspire-v2-compose-web:local";
    service.Build = new("./web", "./web/Dockerfile");
    service.Healthcheck = new(["CMD", "node", "-e",
        "fetch('http://127.0.0.1:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"], "1s", "3s", 30);
});

var policy = builder.Pipeline.AddStep("deployment-policy", context =>
    context.AddSummaryAsync("Deployment policy", "Native model inspected"));
policy.DependsOn.Add("validate-compose");
policy.RequiredBy.Add("publish-compose");
var verify = builder.Pipeline.AddStep("verify-publish", async context =>
{
    var artifact = Path.Combine(context.PublishingContext.OutputPath, "compose.yaml");
    if (!File.Exists(artifact))
    {
        throw new InvalidOperationException("Compose publish did not produce the expected artifact.");
    }
    await context.LogInformationAsync("Compose artifacts generated.");
});
verify.DependsOn.Add("publish");

var endpoint = args.FirstOrDefault(argument => argument.StartsWith("--native-endpoint=", StringComparison.Ordinal))?["--native-endpoint=".Length..]
    ?? builder.Configuration["ASPIRE_NATIVE_RPC_ENDPOINT"]
    ?? throw new ArgumentException("Supply --native-endpoint=127.0.0.1:<port> for the standalone native server.");
var token = builder.Configuration["ASPIRE_NATIVE_RPC_TOKEN"] ?? throw new ArgumentException("Set ASPIRE_NATIVE_RPC_TOKEN.");
await using var client = await NativeClient.ConnectAsync(IPEndPoint.Parse(endpoint), token, TimeSpan.FromSeconds(15), TimeProvider.System);
await builder.PublishAsync(client, builder.Configuration["output-path"] ?? "aspire-output",
    builder.Configuration["environment"] ?? "ComposeSmoke", builder.Configuration["step"] ?? "publish", CancellationToken.None);
