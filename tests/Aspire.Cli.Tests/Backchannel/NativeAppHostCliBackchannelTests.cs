// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Backchannel;

public class NativeAppHostCliBackchannelTests
{
    [Fact]
    public async Task WaitsForGraphReadinessNotJustSocketConnectivity()
    {
        var calls = 0;
        var rpc = new FakeAppHostRpcClient
        {
            InvokeAsyncCallback = (method, _, _) =>
            {
                Assert.Equal("getRuntimeState", method);
                return Task.FromResult<object?>(JsonSerializer.SerializeToElement(new { ready = ++calls == 2 }));
            }
        };
        await using var session = new FakeAppHostServerSession(rpc);
        var adapter = new NativeAppHostCliBackchannel(session, TimeSpan.FromSeconds(5));
        await adapter.ConnectAsync("fake.sock", 0, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.False((await adapter.GetDashboardUrlsAsync(TestContext.Current.CancellationToken)).DashboardHealthy);
        Assert.Equal(["native.headless.v0"], await adapter.GetCapabilitiesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartupTimeoutIsAnExplicitFailure()
    {
        var rpc = new FakeAppHostRpcClient
        {
            InvokeAsyncCallback = (_, _, _) => Task.FromResult<object?>(JsonSerializer.SerializeToElement(new { ready = false }))
        };
        await using var session = new FakeAppHostServerSession(rpc);
        var adapter = new NativeAppHostCliBackchannel(session, TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAsync<TimeoutException>(() => adapter.ConnectAsync("fake.sock", 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForwardsReadinessNotificationAndStopToNativeControlPlane()
    {
        var methods = new List<string>();
        var rpc = new FakeAppHostRpcClient
        {
            InvokeAsyncCallback = (method, _, _) =>
            {
                methods.Add(method);
                return Task.FromResult<object?>(null);
            }
        };
        await using var session = new FakeAppHostServerSession(rpc);
        var adapter = new NativeAppHostCliBackchannel(session, TimeSpan.FromSeconds(5));
        await adapter.NotifyAppHostReadyAsync(TestContext.Current.CancellationToken);
        await adapter.RequestStopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["notifyCliReady", "requestStop"], methods);
        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.ConnectAsync("fake.sock", true, 0, TestContext.Current.CancellationToken));
        Assert.Throws<NotSupportedException>(() => adapter.GetPublishingActivitiesAsync(TestContext.Current.CancellationToken));
    }
}
