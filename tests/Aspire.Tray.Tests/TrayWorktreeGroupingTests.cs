// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayWorktreeGroupingTests
{
    private static AppHostMenuItem Host(string path, int pid) => TrayPanelSamples.Running(path, pid) with { Checkout = TrayGitCheckout.Read(path) };

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
        Assert.Null(TrayGitCheckout.Read(Path.Combine(repo.CommonDirectory, "..", "..", "missing", "apphost.cs")));
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
        await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.Count == 2);
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
