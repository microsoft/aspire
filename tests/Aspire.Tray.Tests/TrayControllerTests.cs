// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;
using static Aspire.Tray.Tests.Helpers.TestAppHostClient;

namespace Aspire.Tray.Tests;

public class TrayControllerTests
{
    [Fact]
    public async Task DistinguishesConnectingEmptyAndDisconnected()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        Assert.Equal(DiscoveryState.Connecting, controller.State.Discovery);
        Assert.Equal("Connecting to Aspire...", controller.State.Status);
        controller.Start();

        client.Publish(new([], DiscoveryState.Live));
        await WaitForStateAsync(controller, state => state.Discovery == DiscoveryState.Live);
        Assert.Equal("No AppHosts running", controller.State.Status);
        client.Publish(new([], DiscoveryState.Disconnected));
        await WaitForStateAsync(controller, state => state.Discovery == DiscoveryState.Disconnected);
        Assert.Equal("Discovery unavailable. Reconnecting...", controller.State.Status);
    }

    [Fact]
    public async Task DifferentAppHostsCanStopConcurrentlyAndDuplicateStopsAreRejected()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        var first = Host(42);
        var second = Host(43);
        client.Publish(new([first, second], DiscoveryState.Live));
        controller.Start();
        await WaitForStateAsync(controller, state => state.AppHosts.Count == 2);

        controller.RequestStop(first.Id);
        Assert.Equal(first.Id, await client.NextStopAsync());
        Assert.True(controller.State.AppHosts[0].IsStopping);
        Assert.False(controller.State.AppHosts[0].CanStop);
        Assert.True(controller.State.AppHosts[1].CanStop);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStop(first.Id));

        controller.RequestStop(second.Id);
        Assert.Equal(second.Id, await client.NextStopAsync());
        Assert.All(controller.State.AppHosts, row => Assert.True(row.IsStopping));
        Assert.Equal("Stopping 2 AppHosts...", controller.State.Status);

        client.CompleteStop(second.Id, new(StopOutcome.Stopped, 0));
        await WaitForStateAsync(controller, state => !state.AppHosts[1].IsStopping);
        Assert.True(controller.State.AppHosts[0].IsStopping);
        Assert.False(controller.State.AppHosts[1].CanStop);
        Assert.Equal(2, controller.State.AppHosts.Count);

        client.CompleteStop(first.Id, new(StopOutcome.Stopped, 0));
        await WaitForStateAsync(controller, state => state.AppHosts.All(row => !row.IsStopping));
        Assert.Throws<InvalidOperationException>(() => controller.GetDashboardUri(first.Id));
        client.Publish(new([], DiscoveryState.Live));
        await WaitForStateAsync(controller, state => state.AppHosts.Count == 0);
        Assert.Equal([first.Id, second.Id], client.Requests.ToArray());
    }

    [Fact]
    public async Task ActionsStayBoundToLifetimeAcrossReorderingAndReplacement()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        var first = Host(42);
        var second = Host(43);
        client.Publish(new([first, second], DiscoveryState.Live));
        controller.Start();
        await WaitForStateAsync(controller, state => state.AppHosts.Count == 2);

        var updated = first with { DashboardUrl = "https://localhost:5678/" };
        client.Publish(new([second, updated], DiscoveryState.Live));
        await WaitForStateAsync(controller, state => state.AppHosts[0].Id == second.Id);
        Assert.Same(updated, controller.RequireLiveInstance(first.Id));
        Assert.Equal("https://localhost:5678/", controller.GetDashboardUri(first.Id).AbsoluteUri);

        var replacement = Host(42, 2000);
        client.Publish(new([replacement, second], DiscoveryState.Live));
        await WaitForStateAsync(controller, state => state.AppHosts[0].Id == replacement.Id);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStop(first.Id));
        Assert.Throws<InvalidOperationException>(() => controller.GetDashboardUri(first.Id));
        Assert.Empty(client.Requests);

        controller.RequestStop(replacement.Id);
        Assert.Equal(replacement.Id, await client.NextStopAsync());
    }

    [Fact]
    public async Task ReconnectionAndMissingLifetimeDisableActionsWithoutLosingTheRows()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        var known = Host(42);
        var unknown = Host(43) with { ProcessStartTimeUnixMilliseconds = null };
        client.Publish(new([known, unknown], DiscoveryState.Live));
        controller.Start();
        await WaitForStateAsync(controller, state => state.AppHosts.Count == 2);
        Assert.True(controller.State.AppHosts[1].CanOpenDashboard);
        Assert.True(controller.State.AppHosts[1].IsRunning);
        Assert.Equal("1 AppHost needs attention", controller.State.Status);
        Assert.Equal(unknown.DashboardUri, controller.GetDashboardUri(unknown.Id));
        Assert.False(controller.State.AppHosts[1].CanStop);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStop(unknown.Id));

        client.Publish(new([known, unknown], DiscoveryState.Disconnected));
        await WaitForStateAsync(controller, state => state.Discovery == DiscoveryState.Disconnected);
        Assert.All(controller.State.AppHosts, row =>
        {
            Assert.False(row.CanOpenDashboard);
            Assert.False(row.CanStop);
        });
        Assert.Throws<InvalidOperationException>(() => controller.RequestStop(known.Id));
        Assert.Throws<InvalidOperationException>(() => controller.GetDashboardUri(known.Id));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FailureBelongsToItsRowAndRetryClearsIt()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        var first = Host(42);
        var second = Host(43);
        client.Publish(new([first, second], DiscoveryState.Live));
        controller.Start();
        await WaitForStateAsync(controller, state => state.AppHosts.Count == 2);
        controller.RequestStop(first.Id);
        await client.NextStopAsync();
        client.CompleteStop(first.Id, new(StopOutcome.TimedOut, null));
        await WaitForStateAsync(controller, state => state.AppHosts[0].Error is not null);
        Assert.Equal("Stop timed out. The shutdown may still complete.", controller.State.AppHosts[0].Error);
        Assert.True(controller.State.AppHosts[0].IsRunning);
        Assert.True(controller.State.AppHosts[0].CanOpenDashboard);
        Assert.Equal(first.DashboardUri, controller.GetDashboardUri(first.Id));
        Assert.Null(controller.State.AppHosts[1].Error);
        Assert.True(controller.State.AppHosts[1].CanStop);

        controller.RequestStop(first.Id);
        Assert.Equal(first.Id, await client.NextStopAsync());
        Assert.Null(controller.State.AppHosts[0].Error);
        Assert.True(controller.State.AppHosts[0].IsStopping);
    }

    [Fact]
    public async Task QuitCancelsAndJoinsAllOwnedOperationsWithoutRequestingMoreStops()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        var first = Host(42);
        var second = Host(43);
        try
        {
            client.Publish(new([first, second], DiscoveryState.Live));
            controller.Start();
            await WaitForStateAsync(controller, state => state.AppHosts.Count == 2);
            controller.RequestStop(first.Id);
            controller.RequestStop(second.Id);
            await client.NextStopAsync();
            await client.NextStopAsync();
            await controller.DisposeAsync();
            Assert.True(client.WatchFinished.Task.IsCompletedSuccessfully);
            var finished = new[] { await client.NextFinishedStopAsync(), await client.NextFinishedStopAsync() };
            Assert.Equal([42, 43], finished.Select(id => id.AppHostPid).Order());
            Assert.Equal(2, client.Requests.Count);
            Assert.Throws<ObjectDisposedException>(() => controller.RequestStop(first.Id));
        }
        finally
        {
            await controller.DisposeAsync();
        }
    }

    [Fact]
    public async Task ControllerOwnsOnlyOneDiscoveryLoop()
    {
        var controller = new TrayController(new TestAppHostClient());
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        Assert.Throws<InvalidOperationException>(controller.Start);
    }
}
