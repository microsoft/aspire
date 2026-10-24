// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using RemoteDependencies;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DisableDashboard = Environment.GetEnvironmentVariable("REMOTE_HEADLESS") == "1"
});

if (Environment.GetEnvironmentVariable("REMOTE_HEADLESS") == "1")
{
    builder.Services.AddHostedService<HeadlessObserver>();
}

string Required(string name) => builder.Configuration[name]
    ?? throw new InvalidOperationException($"Set {name}; see ../README.md for the isolated SSH fixture.");

var machine = builder.AddRemoteMachine("dev-server", new SshTarget(
    Required("REMOTE_HOST"),
    int.Parse(Required("REMOTE_SSH_PORT"), System.Globalization.CultureInfo.InvariantCulture),
    Required("REMOTE_USER"),
    Required("REMOTE_IDENTITY"),
    Required("REMOTE_KNOWN_HOSTS")));

var cache = builder.AddRemoteRedis("cache", machine,
    int.Parse(builder.Configuration["REMOTE_LOCAL_PORT"] ?? "16379", System.Globalization.CultureInfo.InvariantCulture),
    builder.Configuration["REMOTE_IMAGE"] ?? "redis:7-alpine",
    builder.Configuration["REMOTE_HOST_NETWORK_PORT"] is { } remotePort
        ? int.Parse(remotePort, System.Globalization.CultureInfo.InvariantCulture) : null);

builder.AddProject<Projects.RemoteDependencies_Api>("api")
    .WithHttpEndpoint(port: 18080, targetPort: 18080, isProxied: false)
    .WithReference(cache)
    .WaitFor(cache);

await using var app = builder.Build();
try
{
    await app.RunAsync();
}
finally
{
    // Host startup cancellation can return before hosted-service StopAsync is invoked.
    await app.Services.GetRequiredService<RemoteSessions>().StopAsync(CancellationToken.None);
}
