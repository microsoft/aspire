// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

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
        await test.LogAsync("Connection to host tunnel relay restored.");
        await test.LogAsync("A new output format");
        await test.LogAsync("Another unrecognized line");
        Assert.Equal(HealthStatus.Healthy, (await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout()).Status);
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Single(await test.LogsAsync(), l => l.Content.Contains("Some devtunnel output was not recognized", StringComparison.Ordinal));
        Assert.Single(test.Client.Calls, c => c.Method == nameof(IDevTunnelClient.GetTunnelAsync));
    }

    [Fact]
    public async Task AnotherHostCannotEstablishUnknownLocalReadiness()
    {
        using var test = new TestDevTunnelMonitor();
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        test.Monitor.ReconciliationRetryInterval = TimeSpan.FromMinutes(1);
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Client.GetTunnelCallback = (_, _) =>
        {
            queried.TrySetResult();
            return Task.FromResult(test.Client.TunnelStatus);
        };
        await test.StartAsync();
        await test.LogAsync("Unrecognized connection output.");
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await queried.Task.DefaultTimeout();
        await test.App.ResourceNotifications.WaitForResourceAsync(test.Tunnel.Name,
            _ => test.Tunnel.LastKnownStatus?.HostConnections == 1).DefaultTimeout();
        await test.LogAsync("");

        Assert.False(health.IsCompleted);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(test.Snapshot(test.Tunnel).HealthReports, r => r.Name == "tunnel-connection").Status);
        Assert.Equal(KnownResourceStates.NotStarted, test.Snapshot(test.Port).State?.Text);
        Assert.Null(test.Port.TunnelEndpointAnnotation.AllocatedEndpoint);
        Assert.All(test.Snapshot(test.Port).Urls, u => Assert.True(u.IsInactive));

        // Even after service metadata is available, an unrecognized local host is not enough.
        // This run must supply a connection observation before those URLs can become active.
        await test.LogAsync("Connection to host tunnel relay restored.");
        await health.DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
    }

    [Theory]
    [InlineData("Executing command 'stop'.")]
    [InlineData("Executing command 'restart'.")]
    [InlineData("Successfully executed command 'start'.")]
    [InlineData("Error executing command 'restart'.\nSystem.Exception: command failure\n   at SomeMethod()")]
    [InlineData("Failure executing command 'start'. Error message: failed")]
    [InlineData("Command 'restart' was canceled.")]
    [InlineData("[sys] Starting process...\nUnprefixed system detail")]
    [InlineData("[Aspire dev tunnels] Failed to refresh tunnel access metadata.\nRetryableProvisioningException: Tunnel port not found\n   at SomeMethod()")]
    public async Task AspireDiagnosticEntriesDoNotTriggerOutputWarnings(string content)
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.LogAsync(content);
        test.App.Services.GetRequiredService<ResourceLoggerService>().GetLogger(test.Tunnel)
            .LogInformation(DevTunnelMonitor.DiagnosticPrefix + "Log capture boundary.");
        Assert.Equal(0, (await test.LogsAsync()).Count(l => l.Content.Contains("Some devtunnel output was not recognized", StringComparison.Ordinal)));

        // Filtering a known source must not use up the once-per-start warning allowance.
        await test.LogAsync("An actual unknown CLI message");
        Assert.Single(await test.LogsAsync(), l => l.Content.Contains("Some devtunnel output was not recognized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconciliationRetriesUntilUnrecognizedOutputCanBeReplacedByServiceStatus()
    {
        var time = new TestDevTunnelTimeProvider();
        using var test = new TestDevTunnelMonitor(time);
        test.Monitor.StartupLogTimeout = TimeSpan.Zero;
        test.Monitor.ReconciliationRetryInterval = TimeSpan.FromSeconds(1);
        var firstReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.App.Services.GetRequiredService<IDistributedApplicationEventing>().Subscribe<ResourceEndpointsAllocatedEvent>(test.Port, async (_, ct) =>
        {
            callbackStarted.TrySetResult();
            await callbackRelease.Task.WaitAsync(ct);
        });
        var count = 0;
        test.Client.GetTunnelCallback = (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                firstReturned.TrySetResult();
                return Task.FromResult(test.Client.TunnelStatus with { HostConnections = 0 });
            }
            retried.TrySetResult();
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        await test.LogAsync("Connection to host tunnel relay restored.");
        var health = test.Monitor.CheckHealthAsync(CancellationToken.None);
        await firstReturned.Task.DefaultTimeout();
        Assert.Equal(TimeSpan.FromSeconds(1), await time.ScheduledDelays.Reader.ReadAsync().AsTask().DefaultTimeout());
        time.Advance(TimeSpan.FromSeconds(1));
        await retried.Task.DefaultTimeout();
        Assert.False(health.IsCompleted);
        response.SetResult(test.Client.TunnelStatus);
        await callbackStarted.Task.DefaultTimeout();
        time.Advance(TimeSpan.FromMinutes(1));
        await test.LogAsync("");
        Assert.False(health.IsCompleted);
        Assert.Equal(2, count);
        callbackRelease.SetResult();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstablishedConnectionLossRequiresFreshLocalEvidence(bool deletion)
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        await test.App.ResourceNotifications.PublishUpdateAsync(test.Port, s => s with
        {
            Urls = [new("tunnel", "https://original-3000.usw2.devtunnels.ms/", false)]
        });
        var connectedStatus = test.Client.TunnelStatus;
        if (deletion)
        {
            test.Client.GetTunnelCallback = (_, _) => throw new DevTunnelNotFoundException("mytunnel.usw2", "Tunnel not found.");
        }
        else
        {
            test.Client.TunnelStatus = connectedStatus with { HostConnections = 0 };
        }
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);

        // A replacement/foreign host is positive service metadata, not evidence of a local reconnect.
        test.Client.GetTunnelCallback = null;
        test.Client.TunnelStatus = connectedStatus;
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(test.Snapshot(test.Tunnel).HealthReports, r => r.Name == "tunnel-connection").Status);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);

        await test.LogAsync("Connection to host tunnel relay restored.");
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.False(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
    }

    [Fact]
    public async Task InitialZeroHostObservationDoesNotDiscardStartupEvidence()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        var connectedStatus = test.Client.TunnelStatus;
        test.Client.TunnelStatus = connectedStatus with { HostConnections = 0 };
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        test.Client.TunnelStatus = connectedStatus;
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteMessageCannotHideHostTakeover(bool wrappedDisconnect)
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.App.ResourceNotifications.PublishUpdateAsync(test.Port, s => s with
        {
            Urls = [new("tunnel", "https://original-3000.usw2.devtunnels.ms/", false)]
        });
        await test.LogAsync("Inspect network activity:");
        if (wrappedDisconnect)
        {
            await test.LogAsync("Connection");
            await test.LogAsync("to host tunnel relay");
            await test.LogAsync("closed. Another host for the tunnel has connected.");
        }
        else
        {
            await test.LogAsync("Connection to host tunnel relay closed. Another host for the tunnel has connected.");
        }
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);

        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
        await test.LogAsync("Connection to host tunnel relay restored.");
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.False(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
    }

    [Fact]
    public async Task ColoredCombinedMessagesCannotHideHostTakeover()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.App.ResourceNotifications.PublishUpdateAsync(test.Port, s => s with
        {
            Urls = [new("tunnel", "https://original-3000.usw2.devtunnels.ms/", false)]
        });

        await test.LogAsync("Connection to host tunnel relay restored.; \u001b[31mConnection to host tunnel relay closed.\u001b[0m");
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(test.Snapshot(test.Tunnel).HealthReports, r => r.Name == "tunnel-connection").Status);

        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.True(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
        await test.LogAsync("\u001b[32mConnection to host tunnel relay restored.\u001b[0m");
        Assert.Equal(HealthStatus.Healthy, test.Snapshot(test.Port).HealthStatus);
        Assert.False(Assert.Single(test.Snapshot(test.Port).Urls).IsInactive);
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
        await test.ReconcileAsync();
        Assert.Same(test.Client.AccessStatus, test.Port.LastKnownAccessStatus);
    }

    [Fact]
    public async Task StalledAccessRefreshDoesNotBlockSubsequentStatusQueries()
    {
        using var test = new TestDevTunnelMonitor();
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelAccessStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accessQueries = 0;
        test.Client.GetAccessCallback = (_, ct) =>
        {
            if (Interlocked.Increment(ref accessQueries) == 2)
            {
                queried.TrySetResult();
            }
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        await test.ReadyAsync();
        await queried.Task.DefaultTimeout();
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.False(response.Task.IsCompleted);

        test.Client.TunnelStatus = test.Client.TunnelStatus with { Ports = [] };
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        await test.Monitor.CheckHealthAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(3, test.Client.Calls.Count(c => c.Method == nameof(IDevTunnelClient.GetTunnelAsync)));
        Assert.Equal(2, accessQueries);
        Assert.Equal(HealthStatus.Unhealthy, test.Snapshot(test.Port).HealthStatus);
        Assert.Null(test.Port.LastKnownStatus);
        Assert.False(response.Task.IsCompleted);

        // Shared access work still belongs to the run and is canceled/tracked during stop.
        await test.Monitor.StopAsync(CancellationToken.None).DefaultTimeout();
        Assert.Equal(KnownResourceStates.Finished, test.Snapshot(test.Port).State?.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccessQueryFailuresClearOnlyFailedResults(bool missingPort)
    {
        using var test = new TestDevTunnelMonitor(includeSecondPort: true);
        var otherPort = Assert.Single(test.Tunnel.Ports, p => p.TargetEndpoint.EndpointName == "other");
        test.Client.TunnelStatus = test.Client.TunnelStatus with
        {
            Ports = [
                .. test.Client.TunnelStatus.Ports,
                new(3001, "http") { PortUri = new("https://original-3001.usw2.devtunnels.ms/") }
            ]
        };
        await test.StartAsync();
        await test.ReadyAsync();
        await test.ReconcileAsync();
        Assert.Equal("Denied", Assert.Single(test.Snapshot(test.Port).Properties, p => p.Name == "Anonymous access").Value);
        Assert.Equal("Denied", Assert.Single(test.Snapshot(otherPort).Properties, p => p.Name == "Anonymous access").Value);

        var allowed = new DevTunnelAccessStatus
        {
            AccessControlEntries = [new("Anonymous", false, true, [], ["connect"])]
        };
        test.Client.GetAccessCallback = (number, _) =>
        {
            if (number == (missingPort ? 3001 : (int?)null))
            {
                throw new DistributedApplicationException("Access query failed.");
            }
            return Task.FromResult(allowed);
        };
        await test.ReconcileAsync();

        Assert.Same(allowed, test.Port.LastKnownAccessStatus);
        Assert.Equal("Allowed", Assert.Single(test.Snapshot(test.Port).Properties, p => p.Name == "Anonymous access").Value);
        if (missingPort)
        {
            Assert.Same(allowed, test.Tunnel.LastKnownAccessStatus);
            Assert.Null(otherPort.LastKnownAccessStatus);
            Assert.Equal(0, test.Snapshot(otherPort).Properties.Count(p => p.Name == "Anonymous access"));
        }
        else
        {
            Assert.Null(test.Tunnel.LastKnownAccessStatus);
            Assert.Same(allowed, otherPort.LastKnownAccessStatus);
            Assert.Equal("Allowed", Assert.Single(test.Snapshot(otherPort).Properties, p => p.Name == "Anonymous access").Value);
        }
        Assert.Equal(0, (await test.LogsAsync()).Count(l => l.Content.Contains("Some devtunnel output was not recognized", StringComparison.Ordinal)));

        test.Client.GetAccessCallback = (_, _) => Task.FromResult(allowed);
        await test.ReconcileAsync();
        Assert.Same(allowed, test.Tunnel.LastKnownAccessStatus);
        Assert.Equal("Allowed", Assert.Single(test.Snapshot(otherPort).Properties, p => p.Name == "Anonymous access").Value);
    }

    [Fact]
    public async Task AccessPolicyLogsOnlyEffectiveChanges()
    {
        using var test = new TestDevTunnelMonitor();
        await test.StartAsync();
        await test.ReadyAsync();
        await test.ReconcileAsync();
        await test.ReconcileAsync();
        Assert.Single(await test.LogsAsync(test.Port), l => l.Content.Contains("Anonymous access is not allowed", StringComparison.Ordinal));

        test.Client.AccessStatus = new()
        {
            AccessControlEntries = [new("Anonymous", false, true, [], ["connect"])]
        };
        await test.ReconcileAsync();
        test.Client.AccessStatus = new()
        {
            AccessControlEntries = [new("Anonymous", false, false, [], ["connect"])]
        };
        await test.ReconcileAsync();
        Assert.Single(await test.LogsAsync(test.Port), l => l.Content.Contains("!! Anonymous access is allowed", StringComparison.Ordinal));
        Assert.Equal("Allowed", Assert.Single(test.Snapshot(test.Port).Properties, p => p.Name == "Anonymous access").Value);

        test.Client.GetAccessCallback = (_, _) => throw new DistributedApplicationException("Access query failed.");
        await test.ReconcileAsync();
        Assert.Equal(0, test.Snapshot(test.Port).Properties.Count(p => p.Name == "Anonymous access"));
        test.Client.GetAccessCallback = null;
        await test.ReconcileAsync();
        Assert.Equal(2, (await test.LogsAsync(test.Port)).Count(l => l.Content.Contains("!! Anonymous access is allowed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExpirationAndInverseSemanticsFlowToAccessProperties()
    {
        var time = new TestDevTunnelTimeProvider();
        using var test = new TestDevTunnelMonitor(time);
        var expiration = time.GetUtcNow().AddMinutes(1);
        test.Client.AccessStatus = new()
        {
            AccessControlEntries = [
                new("Anonymous", false, true, [], ["connect"]),
                new("Anonymous", true, false, [], ["connect"]) { Expiration = expiration },
                new("Anonymous", true, false, [], ["connect"]) { IsInverse = true }
            ]
        };
        await test.StartAsync();
        await test.ReadyAsync();
        await test.ReconcileAsync();
        Assert.Equal("Denied", Assert.Single(test.Snapshot(test.Port).Properties, p => p.Name == "Anonymous access").Value);
        time.Advance(TimeSpan.FromMinutes(1));
        await test.ReconcileAsync();
        Assert.Equal("Allowed", Assert.Single(test.Snapshot(test.Port).Properties, p => p.Name == "Anonymous access").Value);
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
        await test.LogAsync("Connection to host tunnel relay restored.");
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

    [Fact]
    public async Task DisposalCancelsIndependentAccessQueries()
    {
        using var test = new TestDevTunnelMonitor(includeSecondPort: true);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DevTunnelAccessStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = 0;
        test.Client.GetAccessCallback = (_, ct) =>
        {
            if (Interlocked.Increment(ref queries) == 3)
            {
                called.TrySetResult();
            }
            return response.Task.WaitAsync(ct);
        };
        await test.StartAsync();
        await test.ReadyAsync();
        await called.Task.DefaultTimeout();
        await test.Monitor.DisposeAsync().AsTask().DefaultTimeout();
        Assert.Equal(3, queries);
        Assert.False(response.Task.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.Monitor.CheckHealthAsync(CancellationToken.None));
    }
}
