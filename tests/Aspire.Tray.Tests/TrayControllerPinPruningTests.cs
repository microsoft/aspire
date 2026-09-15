// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;
using static Aspire.Tray.Tests.Helpers.TestAppHostClient;

namespace Aspire.Tray.Tests;

public class TrayControllerPinPruningTests
{
    public static bool SupportsRestrictedUnixDirectory => !OperatingSystem.IsWindows() && Environment.UserName != "root";

    [Fact]
    public async Task FirstLiveSnapshotPrunesMissingPinnedFilesAndFoldersDurably()
    {
        using var directory = new TestTrayStateDirectory();
        var retained = directory.CreateAppHost("retained/apphost.cs");
        var missingFile = directory.CreateAppHost("missing-file/apphost.cs");
        var missingFolder = directory.CreateAppHost("missing-folder/apphost.cs");
        var recent = directory.CreateAppHost("recent/apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(retained).SetPinned(retained, true)
            .Remember(missingFile).SetPinned(missingFile, true)
            .Remember(missingFolder).SetPinned(missingFolder, true).Remember(recent));
        File.Delete(missingFile);
        Directory.Delete(Path.GetDirectoryName(missingFolder)!, recursive: true);
        File.Delete(recent);
        var client = new TestAppHostClient();
        var controller = new TrayController(client, new FileTraySavedStateStore(directory.StatePath));
        await using var lifetime = controller.ConfigureAwait(true);

        Assert.Equal(3, controller.State.AppHosts.Count);
        controller.PruneMissingPinnedAppHosts();
        Assert.Equal(3, controller.State.AppHosts.Count);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        Assert.Equal(new AppHostInfo(retained, 0, null).Id, Assert.Single(controller.State.AppHosts).Id);
        Assert.Equal(new AppHostInfo(recent, 0, null).Id, Assert.Single(controller.State.RecentAppHosts).Id);
        Assert.Equal("Stopped", controller.State.AppHosts[0].Subtitle);
        Assert.False(controller.State.ShowStatus);
        var reloaded = new FileTraySavedStateStore(directory.StatePath).Load();
        Assert.Equal([new SavedAppHost(recent, false, true), new SavedAppHost(retained, true, true)], reloaded.AppHosts);
        Assert.True(File.Exists(retained));
        Assert.Empty(client.StartRequests);
    }

    [Fact]
    public async Task SubsequentLiveRefreshPrunesDeletedPinsButDisconnectDoesNot()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        File.Delete(path);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Disconnected));
        controller.PruneMissingPinnedAppHosts();

        Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
        Assert.Equal([new SavedAppHost(path, true, true)], new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        Assert.Empty(controller.State.AppHosts);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Empty(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.False(controller.State.CanClearRecent);
        Assert.Equal("No AppHosts running", controller.State.Status);
    }

    [Fact]
    public async Task LiveDiscoveredAppHostsRemainPinnedEvenWhenTheirSourceWasDeletedBeforeTrayStartup()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        File.Delete(path);
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        var host = Host(42) with { AppHostPath = path, Health = AppHostHealth.Healthy };
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        controller.PruneMissingPinnedAppHosts();

        var row = Assert.Single(controller.State.AppHosts);
        Assert.Equal(host.Id, row.Id);
        Assert.True(row.IsPinned);
        Assert.True(row.IsRunning);
        Assert.Equal(AppHostHealth.Healthy, row.Health);
        Assert.Equal([new SavedAppHost(path, true, true)], new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Disconnected));
        Assert.Equal(host.Id, Assert.Single(controller.State.AppHosts).Id);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        Assert.Empty(controller.State.AppHosts);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Empty(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
    }

    [Fact]
    public async Task ExplicitPruningRemovesDeletedPinsWithoutRepeatedChangeNotifications()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        File.Delete(path);
        var notifications = 0;
        controller.Changed += () => Interlocked.Increment(ref notifications);

        controller.PruneMissingPinnedAppHosts();

        Assert.Empty(controller.State.AppHosts);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Empty(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.Equal(1, notifications);
        controller.PruneMissingPinnedAppHosts();
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task MissingUnpinnedRecentRequiresExplicitRemoval()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path));
        File.Delete(path);
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        controller.PruneMissingPinnedAppHosts();

        Assert.Throws<FileNotFoundException>(() => controller.RequestStart(path));
        Assert.Equal(new AppHostInfo(path, 0, null).Id, Assert.Single(controller.State.RecentAppHosts).Id);
        Assert.Equal([new SavedAppHost(path, false, true)], new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
        Assert.Empty(client.StartRequests);
        controller.RemoveRecent(path);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Empty(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
    }

    [Fact]
    public async Task PendingAndSuccessfulStartsProtectPinsUntilDiscoveryObservesThem()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new MemoryTraySavedStateStore();
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        controller.RequestStart(path);
        await client.NextStartAsync();
        File.Delete(path);
        controller.PruneMissingPinnedAppHosts();
        Assert.True(Assert.Single(controller.State.AppHosts).IsStarting);
        Assert.Equal([new SavedAppHost(path, true, true)], store.Load().AppHosts);
        var previous = controller.State;
        client.CompleteStart(path, new(StartOutcome.Started, 0));
        await WaitForStateAsync(controller, state => !ReferenceEquals(previous, state));
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        Assert.True(Assert.Single(controller.State.AppHosts).IsStarting);
        var host = Host(42) with { AppHostPath = path };
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        Assert.Equal(host.Id, Assert.Single(controller.State.AppHosts).Id);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        Assert.Empty(controller.State.AppHosts);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Empty(store.Load().AppHosts);
    }

    [Fact(Skip = "Requires a non-root Unix user.", SkipUnless = nameof(SupportsRestrictedUnixDirectory))]
    public async Task PermissionErrorsPreservePinsAndReportTheProbeFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("restricted/apphost.cs");
        var sourceDirectory = Path.GetDirectoryName(path)!;
        var originalMode = File.GetUnixFileMode(sourceDirectory);
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        try
        {
            File.SetUnixFileMode(sourceDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Throws<UnauthorizedAccessException>(() => TrayAppHostPath.IsMissing(path));

            controller.PruneMissingPinnedAppHosts();

            Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
            Assert.Equal("Unable to check saved AppHost paths. Unavailable pins were kept.", controller.State.Status);
            Assert.True(controller.State.ShowStatus);
            Assert.Equal([new SavedAppHost(path, true, true)], new FileTraySavedStateStore(directory.StatePath).Load().AppHosts);
            Assert.Throws<UnauthorizedAccessException>(() => controller.RequestStart(path));
            Assert.Empty(client.StartRequests);
        }
        finally
        {
            File.SetUnixFileMode(sourceDirectory, originalMode);
        }
        controller.PruneMissingPinnedAppHosts();
        Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
        Assert.False(controller.State.ShowStatus);
    }

    [Fact]
    public async Task PruningDoesNotOverwriteCorruptExternalEditsOrClaimThePinWasRemoved()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var store = new FileTraySavedStateStore(directory.StatePath);
        store.Save(TraySavedState.Empty.Remember(path).SetPinned(path, true));
        var client = new TestAppHostClient();
        var controller = new TrayController(client, store);
        await using var lifetime = controller.ConfigureAwait(true);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        directory.WriteState("{externally corrupted");
        File.Delete(path);

        controller.PruneMissingPinnedAppHosts();

        Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.Equal("Unable to save AppHost history. The saved file was left unchanged.", controller.State.Status);
        Assert.True(controller.State.ShowStatus);
        Assert.Equal("{externally corrupted", File.ReadAllText(directory.StatePath));
        controller.RequestStart(path);
        Assert.Empty(client.StartRequests);
        Assert.Equal("{externally corrupted", File.ReadAllText(directory.StatePath));
    }
}
