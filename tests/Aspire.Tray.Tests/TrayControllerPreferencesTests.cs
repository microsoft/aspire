// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Tray.Tests.Helpers;
using static Aspire.Tray.Tests.Helpers.TestAppHostClient;

namespace Aspire.Tray.Tests;

public class TrayControllerPreferencesTests
{
    [Fact]
    public async Task RetiredStopPreferenceIsReadAndDroppedOnTheNextSave()
    {
        // Earlier builds saved {"appHosts":[...],"confirmStop":false}. Stopping no longer asks, and
        // the store rejects unknown properties, so the old value must still load and then disappear.
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("shop/apphost.cs");
        directory.WriteState(JsonSerializer.Serialize(new { appHosts = new[] { new { appHostPath = path, isPinned = true, isRecent = true } }, confirmStop = false }));
        await using var controller = new TrayController(new TestAppHostClient(), new FileTraySavedStateStore(directory.StatePath));

        Assert.Equal(TrayAppHostPath.Normalize(path), Assert.Single(controller.State.AppHosts).Id.AppHostPath);
        controller.SetPinned(path, false);

        using var json = JsonDocument.Parse(File.ReadAllText(directory.StatePath));
        Assert.False(json.RootElement.TryGetProperty("confirmStop", out _));
        Assert.Equal(new SavedAppHost(TrayAppHostPath.Normalize(path), false, true), Assert.Single(new FileTraySavedStateStore(directory.StatePath).Load().AppHosts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(50)]
    public async Task InitialHistoryIsTrimmedDurablyWithoutLosingPins(int limit)
    {
        using var directory = new TestTrayStateDirectory();
        var paths = Enumerable.Range(0, 50).Select(index => directory.CreateAppHost($"project-{index}/apphost.cs")).ToArray();
        var saved = new TraySavedState(paths.Select(path => new SavedAppHost(path, true, true)).ToArray());
        new FileTraySavedStateStore(directory.StatePath).Save(saved);
        await using var controller = new TrayController(new TestAppHostClient(), new FileTraySavedStateStore(directory.StatePath), limit);

        var reloaded = new FileTraySavedStateStore(directory.StatePath).Load();
        Assert.Equal(paths, reloaded.AppHosts.Select(host => host.AppHostPath));
        Assert.All(reloaded.AppHosts, host => Assert.True(host.IsPinned));
        Assert.Equal(paths.Take(limit), reloaded.AppHosts.Where(host => host.IsRecent).Select(host => host.AppHostPath));
        Assert.Equal(limit > 0, controller.State.CanClearRecent);
        Assert.Equal(50, controller.State.AppHosts.Count);
    }

    [Fact]
    public async Task OldTwentyRowFileMigratesToTen()
    {
        using var directory = new TestTrayStateDirectory();
        var saved = new TraySavedState(Enumerable.Range(0, 20)
            .Select(index => new SavedAppHost(Path.GetFullPath($"project-{index}/apphost.cs"), index == 19, true)).ToArray());
        directory.WriteState(JsonSerializer.Serialize(saved, TraySavedStateJsonContext.Default.TraySavedState));

        await using var controller = new TrayController(new TestAppHostClient(), new FileTraySavedStateStore(directory.StatePath));

        var reloaded = new FileTraySavedStateStore(directory.StatePath).Load();
        Assert.Equal(saved.AppHosts.Take(10).Append(saved.AppHosts[^1] with { IsRecent = false }), reloaded.AppHosts);
        Assert.Equal(10, controller.State.RecentAppHosts.Count);
        Assert.True(Assert.Single(controller.State.AppHosts).IsPinned);
    }

    [Fact]
    public async Task FailedInitialTrimStillAppliesLimitInMemoryAndPreservesTheStore()
    {
        var paths = Enumerable.Range(0, 20).Select(index => Path.GetFullPath($"project-{index}/apphost.cs")).ToArray();
        var saved = new TraySavedState(paths.Select(path => new SavedAppHost(path, false, true)).ToArray());
        var store = new TestTraySavedStateStore(saved) { FailSaves = true };
        await using var controller = new TrayController(new TestAppHostClient(), store, 1);

        Assert.Single(controller.State.RecentAppHosts);
        Assert.Equal(saved, store.Load());
        Assert.Equal("Unable to save AppHost history. The saved file was left unchanged.", controller.State.Status);

        store.FailSaves = false;
        controller.SetPinned(paths[0], true);
        Assert.Equal([new SavedAppHost(paths[0], true, true)], store.Load().AppHosts);
        Assert.Equal(1, store.SaveCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(50)]
    public async Task DiscoveryUsesConfiguredLimitAcrossHistoryActions(int limit)
    {
        using var directory = new TestTrayStateDirectory();
        var paths = Enumerable.Range(0, 52).Select(index => directory.CreateAppHost($"project-{index}/apphost.cs")).ToArray();
        var client = new TestAppHostClient();
        var store = new TestTraySavedStateStore(TraySavedState.Empty);
        await using var controller = new TrayController(client, store, limit);
        controller.SetPinned(paths[^1], true);
        controller.Start();
        var hosts = paths.Select((path, index) => Host(index + 1) with { AppHostPath = path }).ToArray();
        await client.PublishAndWaitAsync(controller, new(hosts, DiscoveryState.Live));
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));

        Assert.Equal(paths.Take(limit).Select(IdentityPath), controller.State.RecentAppHosts.Select(host => host.Id.AppHostPath));
        Assert.Equal(IdentityPath(paths[^1]), Assert.Single(controller.State.AppHosts).Id.AppHostPath);
        Assert.Equal(limit > 0, controller.State.CanClearRecent);

        controller.RemoveRecent(paths[0]);
        controller.ClearRecent();
        Assert.Empty(controller.State.RecentAppHosts);
        controller.SetPinned(paths[^2], true);
        controller.SetPinned(paths[^2], false);
        File.Delete(paths[^1]);
        controller.PruneMissingPinnedAppHosts();
        Assert.Empty(store.Load().AppHosts);
    }

    [Fact]
    public async Task DisablingHistoryDoesNotPreventStartsRetryOrDiscovery()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("apphost.cs");
        var client = new TestAppHostClient();
        var store = new TestTraySavedStateStore(TraySavedState.Empty);
        await using var controller = new TrayController(client, store, 0);
        controller.Start();
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        controller.RequestStart(path);
        Assert.Equal(path, await client.NextStartAsync());
        Assert.Empty(controller.State.RecentAppHosts);
        Assert.True(controller.State.HasActiveAppHosts);
        Assert.False(controller.State.CanClearRecent);
        client.CompleteStart(path, new(StartOutcome.Failed, 7));
        await WaitForStateAsync(controller, state => state.Status == "Unable to start AppHost (CLI exit 7).");
        Assert.False(controller.State.HasActiveAppHosts);

        controller.RequestStart(path);
        Assert.Equal(path, await client.NextStartAsync());
        var starting = controller.State;
        client.CompleteStart(path, new(StartOutcome.Started, 0));
        await WaitForStateAsync(controller, state => !ReferenceEquals(starting, state));
        Assert.Throws<InvalidOperationException>(() => controller.RequestStart(path));
        var host = Host(42) with { AppHostPath = path };
        await client.PublishAndWaitAsync(controller, new([host], DiscoveryState.Live));
        Assert.Equal(host.Id, Assert.Single(controller.State.AppHosts).Id);
        Assert.Empty(store.Load().AppHosts);
        await client.PublishAndWaitAsync(controller, new([], DiscoveryState.Live));
        await WaitForStateAsync(controller, state => !state.HasActiveAppHosts);
        Assert.Empty(controller.State.RecentAppHosts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(51)]
    public void ControllerRejectsOutOfRangeLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrayController(new TestAppHostClient(), new MemoryTraySavedStateStore(), limit));
    }

    private static string IdentityPath(string path) => new AppHostInfo(path, 0, null).Id.AppHostPath;
}
