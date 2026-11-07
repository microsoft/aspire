// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Tray.Tests;

public class TrayWorktreeGroupingTests
{
    private static AppHostMenuItem Host(string path, int pid) => TrayPanelSamples.Running(path, pid) with { Checkout = TrayGitCheckoutReader.Read(path) };

    [Theory]
    [InlineData("apphost.cs")]
    [InlineData("AppHost/apphost.cs")]
    public void BareRepositoryWorktreesUseTheRepositoryName(string relativePath)
    {
        using var repo = new TrayWorktreeRepository(bare: true);
        var main = Host(repo.CreateHost("main", "main", relativePath), 1);
        var cart = Host(repo.CreateHost("cart", "feature/cart", relativePath), 2);
        var group = Assert.Single(TrayAppHostGroup.Create([main, cart]));
        Assert.True(group.IsGroup);
        Assert.Equal("Shop", group.Name);
        Assert.Equal("Shop", Assert.Single(TrayAppHostGroup.Create([cart, main])).Name);
    }

    [Fact]
    public async Task StartAfterDisposalDoesNotPublishOrExposeCacheErrors()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("main", "main");
        var client = new TestAppHostClient();
        var controller = new TrayController(client);
        await controller.DisposeAsync();
        var changes = 0;
        controller.Changed += () => changes++;
        var error = Assert.Throws<ObjectDisposedException>(() => controller.RequestStart(path));
        Assert.Equal(typeof(TrayController).FullName, error.ObjectName);
        Assert.Equal(0, changes);
        Assert.Empty(client.StartRequests);
    }

    [Fact]
    public async Task RemovingAndReaddingAPathInvalidatesCachedMetadataImmediately()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("main", "main");
        var checkout = TrayGitCheckoutReader.Read(path);
        Assert.NotNull(checkout);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<(TrayGitCheckout? Removed, TrayGitCheckout? Readded)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var cache = new TrayGitCheckoutCache(new FakeTimeProvider(), _ =>
        {
            if (Interlocked.Increment(ref reads) > 1)
            {
                release.Task.GetAwaiter().GetResult();
            }
            return checkout;
        });
        cache.Changed += () =>
        {
            if (result.Task.IsCompleted)
            {
                return;
            }
            cache.SetPaths([]);
            var removed = cache.Get(path);
            cache.SetPaths([path]);
            result.TrySetResult((removed, cache.Get(path)));
        };
        cache.SetPaths([path]);
        cache.Start();
        try
        {
            var values = await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(values.Removed);
            Assert.Null(values.Readded);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public void GitCanReplaceHeadWhileTheTrayReadsIt()
    {
        using var repo = new TrayWorktreeRepository();
        repo.CreateHost("main", "main");
        var head = Path.Combine(repo.CommonDirectory, "HEAD");
        using var read = TrayGitCheckoutReader.OpenMetadata(head);
        File.WriteAllText(head + ".lock", "ref: refs/heads/feature/cart");
        File.Replace(head + ".lock", head, null);
        Assert.Equal("ref: refs/heads/feature/cart", File.ReadAllText(head));
        using var reader = new StreamReader(read);
        Assert.Equal("ref: refs/heads/main", reader.ReadToEnd());
    }

    [Fact]
    public async Task SlowGitStorageDoesNotBlockUiPublicationOrShutdown()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("main", "main");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestAppHostClient();
        TrayGitCheckout? ReadCheckout(string hostPath)
        {
            started.TrySetResult();
            try
            {
                release.Task.GetAwaiter().GetResult();
                return TrayGitCheckoutReader.Read(hostPath);
            }
            finally
            {
                finished.TrySetResult();
            }
        }
        await using var controller = new TrayController(client, new MemoryTraySavedStateStore(), 10, TimeProvider.System, ReadCheckout);
        Assert.False(started.Task.IsCompleted);
        controller.Start();
        var host = new AppHostInfo(path, 1, "https://localhost:1234/") { ProcessStartTimeUnixMilliseconds = 1001 };
        client.Publish(new([host], DiscoveryState.Live));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() =>
            {
                Assert.Equal(host.DashboardUri, controller.GetDashboardUri(host.Id));
                controller.RequestStop(host.Id);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(host.Id, await client.NextStopAsync());
            await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StalledCheckoutDoesNotBlockOtherRefreshesOrAccumulateReads()
    {
        using var repo = new TrayWorktreeRepository();
        var blockedPath = TrayAppHostPath.Normalize(repo.CreateHost("main", "main"));
        var healthyPath = TrayAppHostPath.Normalize(repo.CreateHost("cart", "feature/cart"));
        var time = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedReads = 0;
        var client = new TestAppHostClient();
        TrayGitCheckout? ReadCheckout(string path)
        {
            if (!TrayAppHostPath.Comparer.Equals(path, blockedPath))
            {
                return TrayGitCheckoutReader.Read(path);
            }
            Interlocked.Increment(ref blockedReads);
            started.TrySetResult();
            try
            {
                release.Task.GetAwaiter().GetResult();
                return TrayGitCheckoutReader.Read(path);
            }
            finally
            {
                finished.TrySetResult();
            }
        }
        await using var controller = new TrayController(client, new MemoryTraySavedStateStore(), 10, time, ReadCheckout);
        var blocked = new AppHostInfo(blockedPath, 1, null) { ProcessStartTimeUnixMilliseconds = 1001 };
        var healthy = new AppHostInfo(healthyPath, 2, null) { ProcessStartTimeUnixMilliseconds = 1002 };
        controller.Start();
        client.Publish(new([blocked, healthy], DiscoveryState.Live));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await TestAppHostClient.WaitForStateAsync(controller,
                state => state.AppHosts.FirstOrDefault(host => host.Id == healthy.Id)?.Checkout?.Label == "feature/cart");
            for (var index = 0; index < 3; index++)
            {
                var branch = $"feature/cart-{index}";
                File.WriteAllText(Path.Combine(repo.CommonDirectory, "worktrees", "cart", "HEAD"), "ref: refs/heads/" + branch);
                time.Advance(TrayGitCheckoutCache.RefreshInterval);
                await TestAppHostClient.WaitForStateAsync(controller,
                    state => state.AppHosts.FirstOrDefault(host => host.Id == healthy.Id)?.Checkout?.Label == branch);
            }
            // Remove the path from both discovery and saved history, then re-add it.
            // The outstanding read still owns its slot until that read actually finishes.
            client.Publish(new([healthy], DiscoveryState.Live));
            await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.Count == 1);
            controller.RemoveRecent(blockedPath);
            client.Publish(new([blocked, healthy], DiscoveryState.Live));
            await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.Count == 2);
            Assert.Equal(1, Volatile.Read(ref blockedReads));
            Assert.Null(controller.State.AppHosts.Single(host => host.Id == blocked.Id).Checkout);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnOldCardDoesNotScrollFocusAwayFromAnotherRow(bool mouseActivation, bool deferred)
    {
        var state = TrayPanelSamples.Many();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { MaxHeight = 250 };
        var first = state.AppHosts[0];
        var later = new PanelTargetKey(PanelTargetKind.Row, PanelSection.Running, state.AppHosts[1].Id);
        if (mouseActivation)
        {
            panel.Focus(later);
            var more = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.More && target.Key.Host == first.Id);
            panel.PointerDown(more.HitBounds.CenterX, more.HitBounds.CenterY);
            panel.PointerUp(more.HitBounds.CenterX, more.HitBounds.CenterY);
            var laterBounds = panel.Scene.Targets.Single(target => target.Key == later).Bounds;
            panel.Scroll(-Math.Max(0, laterBounds.Bottom - panel.Viewport.Bottom));
        }
        else
        {
            panel.Invoke(new(PanelTargetKind.More, PanelSection.Running, first.Id));
            panel.Key(PanelKey.Home);
            for (var step = 0; step < 20 && panel.FocusedTarget != later; step++)
            {
                panel.Key(PanelKey.Down);
            }
        }
        Assert.Equal(later, panel.FocusedTarget);
        var row = panel.Scene.Targets.Single(target => target.Key == later);
        Assert.True(panel.Viewport.Contains(row.Bounds));
        Assert.Equal(first.Id, panel.ActionsRow!.Value.Host);
        var scroll = panel.ScrollOffset;
        var update = state with { Status = "Updated" };
        if (deferred)
        {
            panel.PointerDown(row.HitBounds.CenterX, row.HitBounds.CenterY);
            Assert.False(panel.Update(update));
            panel.PointerUp(-1, -1);
        }
        else
        {
            panel.Update(update);
        }
        Assert.Equal(later, panel.FocusedTarget);
        Assert.Equal(scroll, panel.ScrollOffset);
        Assert.True(panel.Viewport.Contains(panel.Scene.Targets.Single(target => target.Key == later).Bounds));
    }

    [Fact]
    public async Task GitCacheRefreshesBranchesWithoutRereadingOnUiActions()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("main", "main");
        var time = new FakeTimeProvider();
        var reads = 0;
        var client = new TestAppHostClient();
        TrayGitCheckout? ReadCheckout(string hostPath)
        {
            Interlocked.Increment(ref reads);
            return TrayGitCheckoutReader.Read(hostPath);
        }
        await using var controller = new TrayController(client, new MemoryTraySavedStateStore(), 10, time, ReadCheckout);
        controller.Start();
        var host = new AppHostInfo(path, 1, "https://localhost:1234/") { ProcessStartTimeUnixMilliseconds = 1001 };
        client.Publish(new([host], DiscoveryState.Live));
        await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.SingleOrDefault()?.Checkout?.Label == "main");
        var initialReads = Volatile.Read(ref reads);
        File.WriteAllText(Path.Combine(repo.CommonDirectory, "HEAD"), "ref: refs/heads/feature/cart");
        controller.GetDashboardUri(host.Id);
        controller.SetPinned(path, true);
        Assert.Equal(initialReads, Volatile.Read(ref reads));
        Assert.Equal("main", Assert.Single(controller.State.AppHosts).Checkout!.Label);
        time.Advance(TrayGitCheckoutCache.RefreshInterval);
        await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.SingleOrDefault()?.Checkout?.Label == "feature/cart");
        Assert.True(Volatile.Read(ref reads) > initialReads);
    }

    [Fact]
    public async Task OfflineGitBranchesRefreshWithoutDiscoveryUpdates()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("main", "main");
        var time = new FakeTimeProvider();
        var store = new TestTraySavedStateStore(new([new(path, false, true)]));
        await using var controller = new TrayController(new TestAppHostClient(), store, 10, time);
        controller.Start();
        await TestAppHostClient.WaitForStateAsync(controller, state => state.RecentAppHosts.SingleOrDefault()?.Checkout?.Label == "main");
        File.WriteAllText(Path.Combine(repo.CommonDirectory, "HEAD"), "ref: refs/heads/feature/cart");
        time.Advance(TrayGitCheckoutCache.RefreshInterval);
        await TestAppHostClient.WaitForStateAsync(controller, state => state.RecentAppHosts.SingleOrDefault()?.Checkout?.Label == "feature/cart");
    }

    [Fact]
    public void DiscoveryDoesNotUndoScrollingAwayFromAnOpenCard()
    {
        var state = TrayPanelSamples.Many();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { MaxHeight = 250 };
        var host = state.AppHosts[0];
        panel.Invoke(new(PanelTargetKind.More, PanelSection.Running, host.Id));
        var pin = new PanelTargetKey(PanelTargetKind.TogglePin, PanelSection.Running, host.Id);
        panel.Focus(pin);
        panel.Scroll(-10000);
        var scroll = panel.ScrollOffset;
        Assert.True(panel.Scene.Targets.Single(target => target.Key == pin).HitBounds.IsEmpty);
        panel.Update(state with { Status = "Updated" });
        Assert.Equal(scroll, panel.ScrollOffset);
        Assert.True(panel.Scene.Targets.Single(target => target.Key == pin).HitBounds.IsEmpty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RegroupingRevealsFocusedCardControlsInAConstrainedViewport(bool confirmation, bool deferred)
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), confirmation ? 0 : 1) with { IsRunning = !confirmation };
        var second = Host(repo.CreateHost("cart", "cart"), confirmation ? 0 : 2) with { IsRunning = !confirmation };
        TrayViewState State(params AppHostMenuItem[] hosts) => confirmation
            ? new(DiscoveryState.Live, [], "") { RecentAppHosts = hosts }
            : new(DiscoveryState.Live, hosts, "");
        var panel = new TrayPanel(State(first), TrayPanelPalette.Light) { MaxHeight = 250 };
        var section = confirmation ? PanelSection.Recent : PanelSection.Running;
        if (confirmation)
        {
            panel.ConfirmRemoveMissing(first);
        }
        else
        {
            panel.Invoke(new(PanelTargetKind.More, section, first.Id));
            panel.Focus(new(PanelTargetKind.TogglePin, section, first.Id));
        }
        var focused = panel.FocusedTarget!.Value;
        var before = panel.Scene.Targets.Single(target => target.Key == focused);
        Assert.True(panel.Viewport.Contains(before.Bounds));
        if (deferred)
        {
            // Hold a non-acting row press while discovery inserts the application header.
            var row = panel.Scene.Targets.FirstOrDefault(target => target.Kind == PanelTargetKind.Row && !target.HitBounds.IsEmpty)
                ?? panel.Scene.Targets.Single(target => target.Key == focused);
            panel.PointerDown(row.HitBounds.CenterX, row.HitBounds.CenterY);
            Assert.False(panel.Update(State(first, second)));
            panel.PointerUp(-1, -1);
        }
        else
        {
            Assert.True(panel.Update(State(first, second)));
        }
        var after = panel.Scene.Targets.Single(target => target.Key == focused);
        Assert.Equal(focused, panel.FocusedTarget);
        Assert.True(panel.Viewport.Contains(after.Bounds));
        Assert.False(after.HitBounds.IsEmpty);
    }

    [Theory]
    [InlineData("apphost.cs", "main")]
    [InlineData("AppHost/apphost.cs", "main")]
    [InlineData("Shop.AppHost/apphost.cs", "Shop")]
    public void ApplicationTitleDoesNotDependOnTheFirstLiveCheckout(string relativePath, string expected)
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("feature-first", "feature/first", relativePath), 1);
        var second = Host(repo.CreateHost("feature-second", "feature/second", relativePath), 2);
        var third = Host(repo.CreateHost("feature-third", "feature/third", relativePath), 3);
        Assert.Equal(expected, Assert.Single(TrayAppHostGroup.Create([first, second])).Name);
        Assert.Equal(expected, Assert.Single(TrayAppHostGroup.Create([second, first])).Name);
        Assert.Equal(expected, Assert.Single(TrayAppHostGroup.Create([second, third])).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedAndForgedGitPointersDoNotJoinAnotherCheckout(bool pointAtMain)
    {
        using var repo = new TrayWorktreeRepository();
        var original = repo.CreateHost("original", "cart");
        var copy = repo.CreateHost("copy", "other");
        var originalPointer = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(original))!, ".git");
        var copyPointer = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(copy))!, ".git");
        File.WriteAllText(copyPointer, pointAtMain ? "gitdir: " + repo.CommonDirectory : File.ReadAllText(originalPointer));
        Assert.NotNull(TrayGitCheckoutReader.Read(original));
        Assert.Null(TrayGitCheckoutReader.Read(copy));
        var groups = TrayAppHostGroup.Create([Host(original, 1), Host(copy, 2)]);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.False(group.IsGroup));
    }

    [Fact]
    public void EmptyGitDirectoryAndRemovedCheckoutDoNotInheritAncestorMetadata()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("main", "main");
        var removed = Path.Combine(Path.GetDirectoryName(repo.CommonDirectory)!, "removed", "apphost.cs");
        Assert.Null(TrayGitCheckoutReader.Read(removed));
        File.Delete(Path.Combine(repo.CommonDirectory, "HEAD"));
        Assert.Null(TrayGitCheckoutReader.Read(path));
    }

    [Fact]
    public void OpenActionsRemainVisibleWhenDiscoveryFormsAGroup()
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1);
        var second = Host(repo.CreateHost("cart", "cart"), 2);
        var panel = new TrayPanel(new(DiscoveryState.Live, [first], ""), TrayPanelPalette.Light);
        panel.Invoke(new(PanelTargetKind.More, PanelSection.Running, first.Id));
        panel.Update(new(DiscoveryState.Live, [first, second], ""));
        Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.TogglePin && target.Key.Host == first.Id);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.More, PanelSection.Running, first.Id), panel.FocusedTarget);
        Assert.Null(panel.Key(PanelKey.Escape).Action);
        Assert.Null(panel.ActionsRow);
        Assert.Equal(PanelActionKind.Close, panel.Key(PanelKey.Escape).Action!.Kind);
        panel.Invoke(new(PanelTargetKind.More, PanelSection.Running, first.Id));
        panel.Invoke(Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ToggleApplication).Key);
        Assert.Null(panel.ActionsRow);
        Assert.All(panel.Scene.Targets, target => Assert.Null(target.Host));
        Assert.Equal(PanelActionKind.Close, panel.Key(PanelKey.Escape).Action!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFileConfirmationRevealsItsGroupAndKeepsKeyboardFocus(bool groupFormsAfterConfirm)
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 0) with { IsRunning = false, CanStart = true };
        var second = Host(repo.CreateHost("cart", "cart"), 0) with { IsRunning = false, CanStart = true };
        var initial = groupFormsAfterConfirm ? new[] { first } : new[] { first, second };
        var panel = new TrayPanel(new(DiscoveryState.Live, [], "") { RecentAppHosts = initial }, TrayPanelPalette.Light);
        panel.ConfirmRemoveMissing(first);
        if (groupFormsAfterConfirm)
        {
            panel.Update(new(DiscoveryState.Live, [], "") { RecentAppHosts = [first, second] });
        }
        var cancel = Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ConfirmCancel);
        Assert.False(cancel.HitBounds.IsEmpty);
        Assert.Equal(cancel.Key, panel.FocusedTarget);
        panel.Invoke(new(PanelTargetKind.ConfirmAccept, PanelSection.Recent, first.Id));
        Assert.Null(panel.Confirmation);
        Assert.Equal(PanelActionKind.Close, panel.Key(PanelKey.Escape).Action!.Kind);
    }

    [Theory]
    [InlineData((int)AppHostHealth.Healthy, false, false, true, "Healthy")]
    [InlineData((int)AppHostHealth.Warning, false, false, true, "Needs attention")]
    [InlineData((int)AppHostHealth.Unhealthy, false, false, true, "Unhealthy")]
    [InlineData((int)AppHostHealth.Unknown, false, false, true, "Status unavailable")]
    [InlineData((int)AppHostHealth.Healthy, true, false, true, "Starting or stopping")]
    [InlineData((int)AppHostHealth.Healthy, false, true, true, "Stopped")]
    [InlineData((int)AppHostHealth.Healthy, false, false, false, "Status unavailable")]
    public void CollapsedApplicationsExposeAggregateStatusToNativeAccessibility(int health, bool starting, bool stopped, bool live, string expected)
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1) with { Health = (AppHostHealth)health, IsStarting = starting, IsRunning = !stopped };
        var second = Host(repo.CreateHost("cart", "cart"), 2);
        var panel = new TrayPanel(new(live ? DiscoveryState.Live : DiscoveryState.Disconnected, [first, second], ""), TrayPanelPalette.Light);
        var application = Assert.Single(panel.Scene.Accessibility, element => element.Target?.Kind == PanelTargetKind.ToggleApplication);
        Assert.Equal(expected, application.Description);
        Assert.Equal("Show 2 instances of Shop", application.Name);
    }

    [Fact]
    public void PidDisambiguationUsesDisplayedRowsRatherThanCollapsedApplicationNames()
    {
        using var shop = new TrayWorktreeRepository();
        using var billing = new TrayWorktreeRepository();
        var first = Host(shop.CreateHost("main", "main"), 1);
        var second = Host(shop.CreateHost("cart", "cart"), 2);
        var third = Host(billing.CreateHost("main", "main", "Billing.AppHost/apphost.cs"), 3);
        var fourth = Host(billing.CreateHost("fix", "fix", "Billing.AppHost/apphost.cs"), 4);
        var standalone = Host(shop.CreateHost("standalone", "standalone"), 5) with { Checkout = null };
        var panel = new TrayPanel(new(DiscoveryState.Live, [first, second, third, fourth, standalone], ""), TrayPanelPalette.Light);
        var row = Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.Row);
        Assert.Equal(standalone.Id, row.Host!.Id);
        Assert.Equal("Shop, Running · " + AppHostPresentation.GetLocation(standalone.Id.AppHostPath), row.AccessibleName);
        foreach (var application in panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.ToggleApplication).ToArray())
        {
            panel.Invoke(application.Key);
        }
        var mainRows = panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row && target.Host!.DisplayName == "main").ToArray();
        Assert.Equal([first.Id, third.Id], mainRows.Select(target => target.Host!.Id));
        Assert.Equal(["main, Running · " + AppHostPresentation.GetLocation(first.Id.AppHostPath) + " · PID 1",
            "main, Running · " + AppHostPresentation.GetLocation(third.Id.AppHostPath) + " · PID 3"], mainRows.Select(target => target.AccessibleName));
    }

    [Fact]
    public void RelatedWorktreesGroupButClonesAndDifferentAppHostsRemainSeparate()
    {
        using var repo = new TrayWorktreeRepository();
        using var clone = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1);
        var second = Host(repo.CreateHost("cart", "feature/cart"), 2);
        var detached = Host(repo.CreateHost("experiment", null), 3);
        var other = Host(repo.CreateHost("other", "other", "Other.AppHost/apphost.cs"), 4);
        var cloned = Host(clone.CreateHost("main", "main"), 5);
        var groups = TrayAppHostGroup.Create([first, other, cloned, second, detached]);

        Assert.Equal(3, groups.Count);
        Assert.Equal([first.Id, second.Id, detached.Id], groups[0].Hosts.Select(host => host.Id));
        Assert.Equal(["main", "feature/cart", "experiment"], groups[0].Hosts.Select(host => host.DisplayName));
        Assert.Same(other, Assert.Single(groups[1].Hosts));
        Assert.Same(cloned, Assert.Single(groups[2].Hosts));
        Assert.False(groups[1].IsGroup);
        Assert.False(groups[2].IsGroup);
    }

    [Fact]
    public void MissingOrMalformedGitMetadataKeepsHostsVisibleAndSeparate()
    {
        using var repo = new TrayWorktreeRepository();
        var path = repo.CreateHost("cart", "feature/cart");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, ".git"), "not a git pointer");
        var first = Host(path, 1);
        var second = Host(path, 2);
        Assert.Null(first.Checkout);
        Assert.Equal(2, TrayAppHostGroup.Create([first, second]).Count);
        Assert.Null(TrayGitCheckoutReader.Read(Path.Combine(repo.CommonDirectory, "..", "..", "missing", "apphost.cs")));
    }

    [Fact]
    public void DuplicateAndLongBranchLabelsKeepDiscriminatorsBeforeEllipsis()
    {
        using var repo = new TrayWorktreeRepository();
        var branch = new string('x', 80);
        var first = Host(repo.CreateHost("first", branch), 1);
        var second = Host(repo.CreateHost("second", branch), 2);
        var group = Assert.Single(TrayAppHostGroup.Create([first, second]));
        Assert.Equal(["1. " + branch, "2. " + branch], group.Hosts.Select(host => host.DisplayName));
        Assert.Equal([first.Id, second.Id], group.Hosts.Select(host => host.Id));
    }

    [Fact]
    public void GeneratedLabelsDoNotCollideWithLiteralBranchNames()
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("first", "cart"), 1);
        var second = Host(repo.CreateHost("second", "cart"), 2);
        var third = Host(repo.CreateHost("third", "1. cart"), 3);
        var group = Assert.Single(TrayAppHostGroup.Create([first, second, third]));
        Assert.Equal(["1. cart", "2. cart", "3.1. 1. cart"], group.Hosts.Select(host => host.DisplayName));
    }

    [Fact]
    public void KeyboardAndPointerActivationDoNotRetargetAReplacedProcess()
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1);
        var second = Host(repo.CreateHost("cart", "cart"), 2);
        var panel = new TrayPanel(new(DiscoveryState.Live, [first, second], ""), TrayPanelPalette.Light);
        panel.FocusFirst();
        Assert.Equal(PanelTargetKind.ToggleApplication, panel.FocusedTarget!.Value.Kind);
        panel.Key(PanelKey.Enter);
        var stop = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Stop && target.Key.Host == first.Id);
        var point = stop.HitBounds;
        panel.PointerDown(point.CenterX, point.CenterY);
        var replacement = first with { Id = first.Id with { ProcessStartTimeUnixMilliseconds = 5000 } };
        Assert.False(panel.Update(new(DiscoveryState.Live, [replacement, second], "")));
        // The controller validates this old lifetime again before executing the returned action.
        Assert.Equal(first.Id, panel.PointerUp(point.CenterX, point.CenterY).Action!.Host!.Id);
        Assert.Null(panel.Invoke(stop.Key).Action);
        Assert.Equal(replacement.Id, panel.Invoke(stop.Key with { Host = replacement.Id }).Action!.Host!.Id);
    }

    [Fact]
    public void ExpandingThePanelExposesOnlyInstanceActionsAndPreservesTheirIdentity()
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1);
        var second = Host(repo.CreateHost("cart", "feature/cart"), 2);
        var panel = new TrayPanel(new(DiscoveryState.Live, [first, second], ""), TrayPanelPalette.Light);
        var toggle = Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ToggleApplication).Key;
        Assert.All(panel.Scene.Targets, target => Assert.Null(target.Host));

        Assert.Null(panel.Invoke(toggle).Action);
        Assert.Equal([first.Id, second.Id], panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).Select(target => target.Host!.Id));
        Assert.Equal(["main", "feature/cart"], panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).Select(target => target.Host!.DisplayName));
        var stop = panel.Invoke(new(PanelTargetKind.Stop, PanelSection.Running, second.Id)).Action!;
        Assert.Equal(PanelActionKind.Stop, stop.Kind);
        Assert.Equal(second.Id, stop.Host!.Id);
        panel.Invoke(new(PanelTargetKind.More, PanelSection.Running, first.Id));
        var pin = panel.Invoke(new(PanelTargetKind.TogglePin, PanelSection.Running, first.Id)).Action!;
        Assert.Equal(PanelActionKind.TogglePin, pin.Kind);
        Assert.Equal(first.Id, pin.Host!.Id);
        panel.Invoke(toggle);
        Assert.All(panel.Scene.Targets, target => Assert.Null(target.Host));
    }

    [Fact]
    public void ExpansionSurvivesReplacementAndReorderingButOldActionsDoNot()
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1);
        var second = Host(repo.CreateHost("cart", "feature/cart"), 2);
        var panel = new TrayPanel(new(DiscoveryState.Live, [first, second], ""), TrayPanelPalette.Dark);
        var toggle = Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ToggleApplication).Key;
        panel.Invoke(toggle);
        var oldStop = new PanelTargetKey(PanelTargetKind.Stop, PanelSection.Running, first.Id);
        var replacement = first with { Id = first.Id with { ProcessStartTimeUnixMilliseconds = 5000 } };
        panel.Update(new(DiscoveryState.Live, [second, replacement], ""));
        Assert.Equal(toggle, Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ToggleApplication).Key);
        Assert.Equal([second.Id, replacement.Id], panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).Select(target => target.Host!.Id));
        Assert.Null(panel.Invoke(oldStop).Action);
        Assert.Equal(replacement.Id, panel.Invoke(oldStop with { Host = replacement.Id }).Action!.Host!.Id);
    }

    [Fact]
    public void PinnedAndRecentApplicationsExpandIndependentlyAndRetainStartActions()
    {
        using var repo = new TrayWorktreeRepository();
        AppHostMenuItem Offline(string checkout) => Host(repo.CreateHost(checkout, checkout), 0) with
        {
            IsRunning = false, CanOpenDashboard = false, CanStop = false, CanStart = true
        };
        var first = Offline("main");
        var second = Offline("cart");
        var third = Offline("experiment");
        var fourth = Offline("fix");
        var panel = new TrayPanel(new(DiscoveryState.Live, [first, second], "") { RecentAppHosts = [third, fourth] }, TrayPanelPalette.Light);
        panel.Invoke(new(PanelTargetKind.ToggleRecent, PanelSection.Recent, null));
        var toggles = panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.ToggleApplication).ToArray();
        Assert.Equal([PanelSection.Pinned, PanelSection.Recent], toggles.Select(target => target.Key.Section));
        panel.Invoke(toggles[1].Key);
        Assert.Equal([third.Id, fourth.Id], panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).Select(target => target.Host!.Id));
        var start = panel.Invoke(new(PanelTargetKind.Start, PanelSection.Recent, fourth.Id)).Action!;
        Assert.True(start.Recent);
        Assert.Equal(fourth.Id, start.Host!.Id);
        Assert.Equal(PanelActionKind.Start, start.Kind);
    }

    [Fact]
    public async Task DiscoveryCapturesGitMetadataForRunningPinnedAndRecentEntries()
    {
        using var repo = new TrayWorktreeRepository();
        var first = repo.CreateHost("main", "main");
        var second = repo.CreateHost("cart", "feature/cart");
        var third = repo.CreateHost("experiment", null);
        var client = new TestAppHostClient();
        var store = new TestTraySavedStateStore(new([new(second, true, true), new(third, false, true)]));
        await using var controller = new TrayController(client, store);
        controller.Start();
        client.Publish(new([new(first, 1, null) { ProcessStartTimeUnixMilliseconds = 1001 }], DiscoveryState.Live));
        await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.Count == 2
            && state.AppHosts.All(host => host.Checkout is not null) && state.RecentAppHosts.All(host => host.Checkout is not null));
        Assert.Equal(["main", "feature/cart"], controller.State.AppHosts.Select(host => host.Checkout!.Label));
        Assert.Equal("experiment", Assert.Single(controller.State.RecentAppHosts).Checkout!.Label);
    }

    [Theory]
    [InlineData((int)AppHostHealth.Healthy, (int)AppHostHealth.Unhealthy, (int)PanelStatusGlyph.Unhealthy)]
    [InlineData((int)AppHostHealth.Healthy, (int)AppHostHealth.Unknown, (int)PanelStatusGlyph.Unknown)]
    [InlineData((int)AppHostHealth.Warning, (int)AppHostHealth.Healthy, (int)PanelStatusGlyph.Warning)]
    public void AggregateStatusDoesNotHideUnhealthyOrUnknownChildren(int firstHealth, int secondHealth, int expected)
    {
        using var repo = new TrayWorktreeRepository();
        var first = Host(repo.CreateHost("main", "main"), 1) with { Health = (AppHostHealth)firstHealth };
        var second = Host(repo.CreateHost("cart", "cart"), 2) with { Health = (AppHostHealth)secondHealth };
        var group = Assert.Single(TrayAppHostGroup.Create([first, second]));
        Assert.Equal((PanelStatusGlyph)expected, group.Status(live: true));
        Assert.Equal(PanelStatusGlyph.Unknown, group.Status(live: false));
    }
}
