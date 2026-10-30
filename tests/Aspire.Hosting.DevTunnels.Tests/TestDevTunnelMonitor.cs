// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.DevTunnels.Tests;

internal sealed class TestDevTunnelMonitor : IDisposable
{
    private readonly IDistributedApplicationTestingBuilder _builder = TestDistributedApplicationBuilder.Create();

    public TestDevTunnelMonitor(TimeProvider? timeProvider = null, bool includeSecondPort = false)
    {
        if (timeProvider is not null)
        {
            _builder.Services.AddSingleton(timeProvider);
        }
        _builder.Services.AddSingleton<IDevTunnelClient>(Client);
        _builder.Services.AddSingleton<IInteractionService>(Interaction);
        var target = _builder.AddExecutable("target", "unused", _builder.AppHostDirectory)
            .WithHttpEndpoint(targetPort: 3000);
        var targetEndpoint = target.GetEndpoint("http");
        targetEndpoint.EndpointAnnotation.AllocatedEndpoint = new(targetEndpoint.EndpointAnnotation, "localhost", 3000);
        if (includeSecondPort)
        {
            target.WithHttpEndpoint(targetPort: 3001, name: "other");
            var otherEndpoint = target.GetEndpoint("other");
            otherEndpoint.EndpointAnnotation.AllocatedEndpoint = new(otherEndpoint.EndpointAnnotation, "localhost", 3001);
        }
        Tunnel = _builder.AddDevTunnel("tunnel", "mytunnel").WithReference(target).Resource;
        Port = Assert.Single(Tunnel.Ports, p => p.TargetEndpoint.EndpointName == "http");
        App = _builder.Build();
        Monitor = App.Services.GetRequiredKeyedService<DevTunnelMonitor>(Tunnel);
        // Tests opt into fallback explicitly; host load must not select that path accidentally.
        Monitor.StartupLogTimeout = TimeSpan.FromMinutes(1);
    }

    public TestDevTunnelClient Client { get; } = new()
    {
        TunnelStatus = new("mytunnel.usw2", 1, 0, "", [])
        {
            Ports = [new(3000, "http") { PortUri = new("https://original-3000.usw2.devtunnels.ms/") }]
        }
    };

    public DistributedApplication App { get; }
    public TestInteractionService Interaction { get; } = new() { IsAvailable = false };
    public DevTunnelMonitor Monitor { get; }
    public DevTunnelResource Tunnel { get; }
    public DevTunnelPortResource Port { get; }

    public async Task StartAsync()
    {
        await Monitor.StartAsync("mytunnel.usw2", CancellationToken.None).DefaultTimeout();
        await App.ResourceNotifications.PublishUpdateAsync(Tunnel, s => s with { State = KnownResourceStates.Running });
    }

    public Task LogAsync(string content) => Monitor.ProcessLogAsync(content, CancellationToken.None).DefaultTimeout();

    public async Task ReconcileAsync()
    {
        await Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        await Monitor.WaitForAccessRefreshAsync(CancellationToken.None).DefaultTimeout();
    }

    public async Task ReadyAsync()
    {
        await LogAsync("""
            Connection to host tunnel relay restored.
            Hosting port: 3000
            Connect via browser: https://original-3000.usw2.devtunnels.ms
            Inspect network activity: https://original-3000-inspect.usw2.devtunnels.ms
            Ready to accept connections for tunnel: mytunnel.usw2
            """);
        if (Tunnel.Ports.Count > 1)
        {
            await LogAsync("""
                Hosting port: 3001
                Connect via browser: https://original-3001.usw2.devtunnels.ms
                """);
        }
        await App.ResourceNotifications.WaitForResourceAsync(Port.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Running && e.Snapshot.HealthStatus == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy).DefaultTimeout();
    }

    public CustomResourceSnapshot Snapshot(IResource resource)
    {
        Assert.True(App.ResourceNotifications.TryGetCurrentState(resource.Name, out var current));
        return current.Snapshot;
    }

    public async Task<IReadOnlyList<LogLine>> LogsAsync(IResource? resource = null)
    {
        await using var reader = App.Services.GetRequiredService<ResourceLoggerService>().WatchAsync(resource ?? Tunnel).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().DefaultTimeout());
        return reader.Current;
    }

    public void Dispose()
    {
        App.Dispose();
        _builder.Dispose();
    }
}
