// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelMonitorTests
{
    [Fact]
    public async Task HostOutputAllocatesPortsWithoutQueryingServiceStatus()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.ReadyAsync();
        Assert.Equal(HealthStatus.Healthy, (await health.DefaultTimeout()).Status);
        Assert.Equal(KnownResourceStates.Running, test.Snapshot(test.Port).State?.Text);
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Equal("original-3000.usw2.devtunnels.ms", test.Port.TunnelEndpointAnnotation.AllocatedEndpoint?.Address);
        Assert.All(test.Client.Calls, c => Assert.Equal(nameof(IDevTunnelClient.GetAccessAsync), c.Method));
    }

    [Fact]
    public async Task UnrecognizedOutputWarnsOnceAndReconcilesReadiness()
    {
        using var test = new TestDevTunnelMonitor();
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        await test.StartAsync();
        await test.LogAsync("A new output format");
        await test.LogAsync("Another unrecognized line");
        Assert.Equal(HealthStatus.Healthy, (await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout()).Status);
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Single(await test.LogsAsync(), l => l.Content.Contains("Some devtunnel output was not recognized", StringComparison.Ordinal));
        Assert.Single(test.Client.Calls, c => c.Method == nameof(IDevTunnelClient.GetTunnelAsync));
    }

    [Fact]
    public async Task ReconciliationRetriesUntilUnrecognizedOutputCanBeReplacedByServiceStatus()
    {
        using var test = new TestDevTunnelMonitor();
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        test.Monitor.ReconciliationRetryInterval = TimeSpan.Zero;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        test.Client.GetTunnelCallback = (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                return Task.FromResult(test.Client.TunnelStatus with { HostConnections = 0 });
            }
            retried.TrySetResult();
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await retried.Task.DefaultTimeout();
        Assert.False(health.IsCompleted);
        response.SetResult(test.Client.TunnelStatus);
        await health.DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task AcceptsReadinessMessagesCombinedOnOneLine()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.LogAsync("Hosting port: 3000; Connect via browser: https://original-3000.usw2.devtunnels.ms; Ready to accept connections for tunnel: mytunnel.usw2");
        await test.App.ResourceNotifications.WaitForResourceAsync(test.Port.Name, KnownResourceStates.Running).DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Fact]
    public async Task ReconciliationUpdatesAndRemovesPreviouslyObservedPorts()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        test.Client.TunnelStatus = test.Client.TunnelStatus with
        {
            Ports = [new(3000, "http") { PortUri = new("https://replacement-3000.usw2.devtunnels.ms/") }]
        };
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal("replacement-3000.usw2.devtunnels.ms", test.Port.TunnelEndpointAnnotation.AllocatedEndpoint?.Address);
        test.Client.TunnelStatus = test.Client.TunnelStatus with { Ports = [] };
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Null(test.Port.LastKnownStatus);
    }

    [Fact]
    public async Task MissingTunnelInvalidatesLogDerivedReadiness()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.App.ResourceNotifications.PublishUpdateAsync(test.Port, s => s with
        {
            Urls = [new("tunnel", test.Port.LastKnownStatus!.PortUri!.AbsoluteUri, false)]
        });
        test.Client.GetTunnelCallback = (_, _) => throw new DevTunnelNotFoundException("mytunnel.usw2", "Tunnel not found.");

        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(test.Snapshot(test.Tunnel).HealthReports, r => r.Name == "tunnel-connection").Status);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
        Assert.Null(test.Port.LastKnownStatus);
        Assert.Null(test.Tunnel.LastKnownStatus);
    }

    [Fact]
    public async Task CustomizedUrlsSurviveReadinessReconciliationAndReconnect()
    {
        using var test = new TestDevTunnelMonitor();
        test.App.Services.GetRequiredService<IDistributedApplicationEventing>().Subscribe<ResourceEndpointsAllocatedEvent>(test.Port,
            async (_, _) => await test.App.ResourceNotifications.PublishUpdateAsync(test.Port, s => s with
            {
                Urls = [
                    new("tunnel", "https://original-3000.usw2.devtunnels.ms/swagger?x=1#operations", false),
                    new("tunnel", "https://original-3000.usw2.devtunnels.ms/health?next=%2F", false),
                    new("tunnel", "https://docs.example/guide?x=1", false),
                    new(null, "https://original-3000-inspect.usw2.devtunnels.ms/requests?id=1", true) { DisplayProperties = new("Inspect") }
                ]
            }));
        await test.StartAsync();
        await test.ReadyAsync();
        var originalUrls = test.Snapshot(test.Port).Urls.Select(u => u.Url).ToArray();
        Assert.Equal([
            "https://original-3000.usw2.devtunnels.ms/swagger?x=1#operations",
            "https://original-3000.usw2.devtunnels.ms/health?next=%2F",
            "https://docs.example/guide?x=1",
            "https://original-3000-inspect.usw2.devtunnels.ms/requests?id=1"
        ], originalUrls);
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        await test.LogAsync("Connection to host tunnel relay closed.");
        await test.LogAsync("Connection to host tunnel relay restored.");
        Assert.Equal(originalUrls, test.Snapshot(test.Port).Urls.Select(u => u.Url));

        test.Client.TunnelStatus = test.Client.TunnelStatus with
        {
            Ports = [new(3000, "http") { PortUri = new("https://replacement-3000.usw2.devtunnels.ms") }]
        };
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal([
            "https://replacement-3000.usw2.devtunnels.ms/swagger?x=1#operations",
            "https://replacement-3000.usw2.devtunnels.ms/health?next=%2F",
            "https://docs.example/guide?x=1",
            "https://replacement-3000-inspect.usw2.devtunnels.ms/requests?id=1"
        ], test.Snapshot(test.Port).Urls.Select(u => u.Url));
        Assert.All(test.Snapshot(test.Port).Urls, u => Assert.False(u.IsInactive));
    }

    [Fact]
    public async Task ServiceHostCountCannotOverrideLocalDisconnect()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.LogAsync("Connection to host tunnel relay closed. Another host for the tunnel has connected.");
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(test.Snapshot(test.Tunnel).HealthReports, r => r.Name == "tunnel-connection").Status);
        await test.LogAsync("Connection to host tunnel relay restored.");
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Fact]
    public async Task OlderReconciliationCannotUndoNewerLogObservations()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Client.GetTunnelCallback = (_, ct) =>
        {
            called.TrySetResult();
            return response.Task.WaitAsync(ct);
        };
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await called.Task.DefaultTimeout();
        await test.LogAsync("Connection to host tunnel relay closed.");
        response.SetResult(test.Client.TunnelStatus);
        await health.DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Fact]
    public async Task LogsCanCompleteReadinessWhileInitialReconciliationIsInFlight()
    {
        using var test = new TestDevTunnelMonitor();
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Client.GetTunnelCallback = (_, ct) =>
        {
            called.TrySetResult();
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await called.Task.DefaultTimeout();
        await test.ReadyAsync();
        await health.DefaultTimeout();
        Assert.False(response.Task.IsCompleted);
        var subsequentHealth = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.LogAsync("");
        response.SetResult(test.Client.TunnelStatus with { HostConnections = 0, Ports = [] });
        await subsequentHealth.DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Fact]
    public async Task OverlappingHealthEvaluationsShareServiceQuery()
    {
        using var test = new TestDevTunnelMonitor();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Client.GetTunnelCallback = (_, ct) =>
        {
            called.TrySetResult();
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        await test.ReadyAsync();
        var first = test.Monitor.CheckHealthAsync(CancellationToken.None);
        var second = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await called.Task.DefaultTimeout();
        await test.LogAsync("");
        Assert.Single(test.Client.Calls, c => c.Method == nameof(IDevTunnelClient.GetTunnelAsync));
        response.SetResult(test.Client.TunnelStatus);
        await Task.WhenAll(first, second).DefaultTimeout();
    }

    [Fact]
    public async Task RestartRejectsReadinessFromPreviousRun()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.Monitor.StopAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(KnownResourceStates.Finished, test.Snapshot(test.Port).State?.Text);
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.LogAsync("2000-01-01T00:00:00Z Ready to accept connections for tunnel: mytunnel.usw2");
        Assert.False(health.IsCompleted);
        Assert.Null(test.Port.LastKnownStatus);
        await test.ReadyAsync();
        await health.DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Fact]
    public async Task StopCancelsPendingReadinessAndServiceQuery()
    {
        using var test = new TestDevTunnelMonitor();
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Client.GetTunnelCallback = async (_, ct) =>
        {
            called.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return test.Client.TunnelStatus;
        };
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await called.Task.DefaultTimeout();
        await test.Monitor.StopAsync(CancellationToken.None).DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health).DefaultTimeout();
        Assert.Equal(KnownResourceStates.Finished, test.Snapshot(test.Port).State?.Text);
        Assert.Null(test.Tunnel.LastKnownStatus);
    }

    [Fact]
    public async Task FailedProcessStopsPortsWithoutStoppedEventOrFinalLog()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.App.ResourceNotifications.PublishUpdateAsync(test.Tunnel, s => s with { State = KnownResourceStates.FailedToStart });
        await test.App.ResourceNotifications.WaitForResourceAsync(test.Port.Name, KnownResourceStates.Finished).DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health).DefaultTimeout();
        Assert.Null(test.Port.LastKnownStatus);
    }

    [Fact]
    public async Task CancellingAHealthCallerDoesNotCancelTheSharedObserver()
    {
        using var test = new TestDevTunnelMonitor();
        using var cts = new CancellationTokenSource();
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(cts.Token);
        await test.LogAsync("");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health);
        await test.ReadyAsync();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Fact]
    public async Task AccessQueriesDoNotBlockReadiness()
    {
        using var test = new TestDevTunnelMonitor();
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelAccessStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Client.GetAccessCallback = (_, ct) =>
        {
            queried.TrySetResult();
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.ReadyAsync();
        await queried.Task.DefaultTimeout();
        await health.DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        response.SetResult(test.Client.AccessStatus);
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Same(test.Client.AccessStatus, test.Port.LastKnownAccessStatus);
    }

    [Fact]
    public async Task AuthenticationNotificationDoesNotBlockReconciliation()
    {
        using var test = new TestDevTunnelMonitor();
        test.Interaction.IsAvailable = true;
        test.Client.LoginStatus = new("Logged out", LoginProvider.Microsoft, "");
        test.Client.GetAccessCallback = (_, _) => throw new InvalidOperationException("Authentication expired.");
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.ReadyAsync();
        var notification = await test.Interaction.Interactions.Reader.ReadAsync().AsTask().DefaultTimeout();
        try
        {
            await health.DefaultTimeout();
            await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
            Assert.False(notification.CompletionTcs.Task.IsCompleted);
            Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        }
        finally
        {
            notification.CompletionTcs.TrySetResult(InteractionResult.Ok(true));
        }
    }

    [Fact]
    public async Task EndpointCallbackCanStopAndRestartParentWithoutBlockingMonitor()
    {
        using var test = new TestDevTunnelMonitor();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        test.App.Services.GetRequiredService<IDistributedApplicationEventing>().Subscribe<ResourceEndpointsAllocatedEvent>(test.Port, async (_, ct) =>
        {
            Interlocked.Increment(ref callbacks);
            await test.Monitor.StopAsync(ct);
            stopped.TrySetResult();
        });
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.LogAsync("Hosting port 3000 at https://original-3000.usw2.devtunnels.ms\nReady to accept connections for tunnel: mytunnel.usw2");
        await stopped.Task.DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health);
        Assert.Equal(KnownResourceStates.Finished, test.Snapshot(test.Port).State?.Text);
        await test.StartAsync();
        await test.ReadyAsync();
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task EndpointCallbackFailureCannotBecomeHealthyThroughReconciliation()
    {
        using var test = new TestDevTunnelMonitor();
        test.App.Services.GetRequiredService<IDistributedApplicationEventing>().Subscribe<ResourceEndpointsAllocatedEvent>(test.Port, (_, _) =>
            throw new InvalidOperationException("Endpoint initialization failed."));
        await test.StartAsync();
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await test.LogAsync("Hosting port 3000 at https://original-3000.usw2.devtunnels.ms\nReady to accept connections for tunnel: mytunnel.usw2");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => health).DefaultTimeout();
        Assert.Equal("Endpoint initialization failed.", error.Message);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(test.Snapshot(test.Port).HealthReports).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Monitor.CheckHealthAsync(CancellationToken.None)).DefaultTimeout();
    }

    [Fact]
    public async Task ReconciliationDoesNotDependOnConsoleUrlNamingConvention()
    {
        using var test = new TestDevTunnelMonitor();
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        test.Client.TunnelStatus = test.Client.TunnelStatus with
        {
            Ports = [new(3000, "http") { PortUri = new("https://service-assigned-name.usw2.devtunnels.ms") }]
        };
        await test.StartAsync();
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Equal("service-assigned-name.usw2.devtunnels.ms", test.Port.TunnelEndpointAnnotation.AllocatedEndpoint?.Address);
    }

    [Fact]
    public async Task DisposedMonitorRejectsNewWork()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.Monitor.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.Monitor.CheckHealthAsync(CancellationToken.None));
    }
}
