// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;
using static Aspire.Tray.Tests.Helpers.TestAppHostClient;

namespace Aspire.Tray.Tests;

public class TrayControllerSavedStateTests
{
    [Fact]
    public async Task SavedHistoryAndPinsReloadWithoutLiveIdentityOrStartingAnything()
    {
        using var directory = new TestTrayStateDirectory();
        var pinned = directory.CreateAppHost("pinned/apphost.cs");
        var recent = directory.CreateAppHost("recent/apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(pinned).Remember(recent).SetPinned(pinned, true));
        var client = new TestAppHostClient();
        var controller = new TrayController(client, new FileTraySavedStateStore(directory.StatePath));
        await using var lifetime = controller.ConfigureAwait(true);

        var row = Assert.Single(controller.State.AppHosts);
        Assert.Equal(new AppHostInfo(pinned, 0, null).Id, row.Id);
        Assert.True(row.IsPinned);
        Assert.False(row.IsRunning);
        Assert.False(row.CanStop);
        Assert.False(row.CanOpenDashboard);
        Assert.Equal(AppHostHealth.Unknown, row.Health);
        Assert.Equal(IdentityPath(recent), Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
        Assert.False(controller.State.HasActiveAppHosts);
        Assert.Empty(client.StartRequests);

        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.True(Assert.Single(controller.State.AppHosts).CanStart);
        Assert.True(Assert.Single(controller.State.RecentAppHosts).CanStart);
        Assert.Empty(client.StartRequests);
    }

    [Fact]
    public async Task PinTracksLiveAndStoppedInstancesWithoutDuplicatingRecentEntries()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("pinned/apphost.cs");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        var first = Host(42) with { AppHostPath = path, Health = AppHostHealth.Healthy };
        await client.PublishAndWaitAsync(controller, new([first], DiscoveryState.Live));
        controller.SetPinned(path, true);

        var live = Assert.Single(controller.State.AppHosts);
        Assert.True(live.IsPinned);
        Assert.True(live.IsRunning);
        Assert.False(live.CanStart);
        Assert.Equal(AppHostHealth.Healthy, live.Health);
        Assert.Empty(controller.State.RecentAppHosts);

        controller.RequestStop(first.Id);
        await client.NextStopAsync();
        client.CompleteStop(first.Id, new(StopOutcome.Stopped, 0));
        await WaitForStateAsync(controller, state => !state.AppHosts[0].IsStopping);
        Assert.False(controller.State.AppHosts[0].IsRunning);
        Assert.False(controller.State.AppHosts[0].CanStart);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        var offline = Assert.Single(controller.State.AppHosts);
        Assert.True(offline.IsPinned);
        Assert.True(offline.CanStart);
        Assert.False(offline.IsRunning);
        Assert.False(offline.CanStop);
        Assert.Equal(AppHostHealth.Unknown, offline.Health);
        Assert.Equal(0, offline.Id.AppHostPid);
        Assert.Null(offline.Id.ProcessStartTimeUnixMilliseconds);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStop(offline.Id));
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.False(controller.State.HasActiveAppHosts);

        var replacement = Host(43, 2000) with { AppHostPath = path, Health = AppHostHealth.Warning };
        await client.PublishAndWaitAsync(controller, new([replacement], DiscoveryState.Live));
        Assert.Equal(replacement.Id, Assert.Single(controller.State.AppHosts).Id);
        Assert.True(controller.State.AppHosts[0].IsPinned);
        Assert.Equal(AppHostHealth.Warning, controller.State.AppHosts[0].Health);
        Assert.Empty(controller.State.RecentAppHosts);

        controller.SetPinned(path, false);
        Assert.False(Assert.Single(controller.State.AppHosts).IsPinned);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.Empty(controller.State.AppHosts);
        Assert.Equal(IdentityPath(path), Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
    }

    [Fact]
    public async Task MultipleLiveInstancesShareOnePinAndOneRecentPath()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        var first = Host(42) with { AppHostPath = path };
        var second = Host(43) with { AppHostPath = path };
        await client.PublishAndWaitAsync(controller, new([first, second], DiscoveryState.Live));
        controller.SetPinned(first.AppHostPath, true);
        Assert.Equal([first.Id, second.Id], controller.State.AppHosts.Select(host => host.Id));
        Assert.All(controller.State.AppHosts, row => Assert.True(row.IsPinned));
        Assert.Empty(controller.State.RecentAppHosts);

        await client.PublishAndWaitAsync(controller, new([second], DiscoveryState.Live));
        Assert.Equal(second.Id, Assert.Single(controller.State.AppHosts).Id);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.Equal(0, Assert.Single(controller.State.AppHosts).Id.AppHostPid);
        controller.SetPinned(first.AppHostPath, false);
        Assert.Equal(first.Id.AppHostPath, Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
    }

    [Fact]
    public async Task ClearingRecentsPreservesPinsAndHeartbeatsDoNotResurrectHistory()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var client = new TestAppHostClient();
        var store = new MemoryTraySavedStateStore();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        var host = Host(42) with { AppHostPath = path };
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        controller.SetPinned(host.AppHostPath, true);
        controller.ClearRecent();

        Assert.False(controller.State.CanClearRecent);
        Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
        Assert.Equal([new SavedAppHost(host.AppHostPath, true, false)], store.Load().AppHosts);
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Disconnected));
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        Assert.False(controller.State.CanClearRecent);

        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
        controller.SetPinned(host.AppHostPath, false);
        Assert.Empty(controller.State.AppHosts);
        Assert.Empty(controller.State.RecentAppHosts);

        var replacement = Host(42, 2000) with { AppHostPath = path };
        await client.PublishAndWaitAsync(controller, new([replacement], DiscoveryState.Live));
        Assert.True(controller.State.CanClearRecent);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.Equal(host.Id.AppHostPath, Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
    }

    [Fact]
    public async Task RecentMenuFiltersMainPathsAndUsesBoundedMostRecentOrder()
    {
        using var directory = new TestTrayStateDirectory();
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        var hosts = Enumerable.Range(1, 25).Select(pid => Host(pid) with
        {
            AppHostPath = directory.CreateAppHost($"sample-{pid}/apphost.cs")
        }).ToArray();
        foreach (var host in hosts)
        {
            await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        }
        Assert.Equal(hosts[^1].Id, Assert.Single(controller.State.AppHosts).Id);
        Assert.Equal(hosts.Skip(5).SkipLast(1).Reverse().Select(host => host.Id.AppHostPath),
            controller.State.RecentAppHosts.Select(host => host.Id.AppHostPath));

        controller.SetPinned(hosts[10].AppHostPath, true);
        Assert.Equal([hosts[^1].Id.AppHostPath, hosts[10].Id.AppHostPath], controller.State.AppHosts.Select(host => host.Id.AppHostPath));
        Assert.Equal(hosts.Skip(5).SkipLast(1).Reverse().Where(host => host != hosts[10]).Select(host => host.Id.AppHostPath),
            controller.State.RecentAppHosts.Select(host => host.Id.AppHostPath));

        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.Equal(hosts[10].Id.AppHostPath, Assert.Single(controller.State.AppHosts).Id.AppHostPath);
        Assert.Equal(hosts.Skip(5).Reverse().Where(host => host != hosts[10]).Select(host => host.Id.AppHostPath),
            controller.State.RecentAppHosts.Select(host => host.Id.AppHostPath));
    }

    [Fact]
    public async Task StartsAreExplicitConcurrentByPathAndWaitForDiscoveryBeforeRetry()
    {
        using var directory = new TestTrayStateDirectory();
        var first = directory.CreateAppHost("first/apphost.cs");
        var second = directory.CreateAppHost("second/apphost.cs");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        controller.RequestStart(first);
        Assert.Equal(first, await client.NextStartAsync());
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(Path.Combine(Path.GetDirectoryName(first)!, ".", "apphost.cs")));
        controller.RequestStart(second);
        Assert.Equal(second, await client.NextStartAsync());
        Assert.Equal([first, second], client.StartRequests.ToArray());
        Assert.All(controller.State.RecentAppHosts, row =>
        {
            Assert.True(row.IsStarting);
            Assert.False(row.CanStart);
            Assert.False(row.IsRunning);
        });
        Assert.Equal("Starting 2 AppHosts...", controller.State.Status);

        client.CompleteStart(first, new(StartOutcome.Started, 0));
        await WaitForStateAsync(controller, state => state.RecentAppHosts.Count == 1);
        Assert.Equal(IdentityPath(second), Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(first));
        var running = Host(42) with { AppHostPath = first };
        await client.PublishAndWaitAsync(controller, new([running], DiscoveryState.Live));
        Assert.Equal(running.Id, Assert.Single(controller.State.AppHosts).Id);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(first));
        Assert.Equal(IdentityPath(second), Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
        Assert.Equal([first, second], client.StartRequests.ToArray());
    }

    [Fact]
    public async Task StartingAMissingPinRemovesItsHistoryWithoutPromptingOrLaunching()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("missing/apphost.cs");
        var client = new TestAppHostClient();
        var store = new MemoryTraySavedStateStore();
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        File.Delete(path);

        controller.RequestStart(path);
        Assert.Throws<ArgumentException>(() => controller.RequestStart("relative/apphost.cs"));
        Assert.Empty(controller.State.AppHosts);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Empty(store.Load().AppHosts);
        Assert.Equal("No AppHosts running", controller.State.Status);
        Assert.Empty(client.StartRequests);
    }

    [Fact]
    public async Task DisconnectedAndUnknownHealthNeverMeanStoppedOrSafeToStart()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        var host = Host(42) with { AppHostPath = path, Health = AppHostHealth.Unknown };
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(path));
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Disconnected));

        var row = Assert.Single(controller.State.AppHosts);
        Assert.Equal(host.Id, row.Id);
        Assert.True(row.IsRunning);
        Assert.True(controller.State.HasActiveAppHosts);
        Assert.False(row.CanStart);
        Assert.Equal(AppHostHealth.Unknown, row.Health);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(path));
        Assert.Empty(client.StartRequests);
    }

    [Fact]
    public async Task StartFailuresAreSafeAndRetryClearsOnlyTheSelectedFailure()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        controller.RequestStart(path);
        await client.NextStartAsync();
        client.FailStart(path, new InvalidOperationException("https://localhost/?token=private"));
        await WaitForStateAsync(controller, state => state.RecentAppHosts[0].Error is not null);
        Assert.Equal("Unable to start AppHost.", Assert.Single(controller.State.RecentAppHosts).Error);
        Assert.True(controller.State.RecentAppHosts[0].CanStart);

        controller.RequestStart(path);
        await client.NextStartAsync();
        Assert.Null(Assert.Single(controller.State.RecentAppHosts).Error);
        client.CompleteStart(path, new(StartOutcome.TimedOut, null));
        await WaitForStateAsync(controller, state => state.RecentAppHosts[0].Error is not null);
        Assert.Equal("Start timed out. The AppHost may still start; wait for discovery before retrying.",
            Assert.Single(controller.State.RecentAppHosts).Error);
        Assert.False(controller.State.RecentAppHosts[0].CanStart);
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(path));
    }

    [Fact]
    public async Task CorruptSavedStateIsVisibleAndPreservedWhileDiscoveryContinues()
    {
        using var directory = new TestTrayStateDirectory();
        directory.WriteState("{corrupt");
        var client = new TestAppHostClient();
        var controller = new TrayController(client, new FileTraySavedStateStore(directory.StatePath));
        await using var lifetime = controller.ConfigureAwait(true);
        Assert.Equal("Unable to load saved AppHosts. The saved file was left unchanged.", controller.State.Status);
        Assert.True(controller.State.ShowStatus);
        controller.Start();
        var host = Host(42);
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));

        Assert.Equal(host.Id, Assert.Single(controller.State.AppHosts).Id);
        Assert.Equal("Unable to save AppHost history. The saved file was left unchanged.", controller.State.Status);
        Assert.True(controller.State.ShowStatus);
        Assert.Throws<InvalidOperationException>(() => controller.SetPinned(host.AppHostPath, true));
        Assert.False(Assert.Single(controller.State.AppHosts).IsPinned);
        Assert.Equal("{corrupt", File.ReadAllText(directory.StatePath));
    }

    [Fact]
    public async Task StatusNoticeShowsErrorsAndDiscoveryFailuresWithoutExposingRoutineCounts()
    {
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        Assert.True(controller.State.ShowStatus);
        controller.Start();
        var host = Host(42);
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        Assert.False(controller.State.ShowStatus);

        controller.ReportActionError("Unable to open the dashboard.");
        Assert.True(controller.State.ShowStatus);
        Assert.Equal("Unable to open the dashboard.", controller.State.Status);
        controller.GetDashboardUri(host.Id);
        Assert.False(controller.State.ShowStatus);

        controller.RequestStop(host.Id);
        await client.NextStopAsync();
        Assert.False(controller.State.ShowStatus);
        client.CompleteStop(host.Id, new(StopOutcome.Failed, 7));
        await WaitForStateAsync(controller, state => state.AppHosts[0].Error is not null);
        Assert.True(controller.State.ShowStatus);
        Assert.Equal("1 AppHost needs attention", controller.State.Status);

        controller.RequestStop(host.Id);
        await client.NextStopAsync();
        Assert.False(controller.State.ShowStatus);
        client.CompleteStop(host.Id, new(StopOutcome.Stopped, 0));
        await WaitForStateAsync(controller, state => !state.AppHosts[0].IsStopping);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.True(controller.State.ShowStatus);

        var replacement = Host(42, 2000);
        await client.PublishAndWaitAsync(controller, new([replacement], DiscoveryState.Live));
        Assert.False(controller.State.ShowStatus);
        await client.PublishAndWaitAsync(controller, new([replacement], DiscoveryState.Disconnected));
        Assert.True(controller.State.ShowStatus);
        Assert.Equal("Discovery unavailable. Reconnecting...", controller.State.Status);
        await client.PublishAndWaitAsync(controller, new([replacement], DiscoveryState.Incompatible));
        Assert.True(controller.State.ShowStatus);
        Assert.Equal("Incompatible CLI. Use the matching Aspire build.", controller.State.Status);
    }

    [Fact]
    public async Task QuitCancelsAndJoinsStartsWithoutStoppingAppHosts()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        controller.RequestStart(path);
        await client.NextStartAsync();

        await controller.DisposeAsync();

        Assert.Equal(path, await client.NextFinishedStartAsync());
        Assert.True(client.WatchFinished.Task.IsCompletedSuccessfully);
        Assert.Empty(client.Requests);
        Assert.Equal([path], client.StartRequests.ToArray());
        Assert.Throws<ObjectDisposedException>(() => controller.RequestStart(path));
    }

    private static string IdentityPath(string path) => new AppHostInfo(path, 0, null).Id.AppHostPath;
}
