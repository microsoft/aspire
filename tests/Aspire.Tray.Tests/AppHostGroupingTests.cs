// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class AppHostGroupingTests
{
    [Fact]
    public void RelatedWorktreesGroupWithBranchLabelsAndPreserveActions()
    {
        using var repo = new TestAppHostRepository();
        var hosts = new[] { Host(repo.CreateWorktree("cart", "feature/cart"), 1), Host(repo.CreateWorktree("release", "release/2.0"), 2), Host(repo.CreateWorktree("experiment", null), 3) };
        var group = Assert.Single(AppHostGrouping.Create(hosts));
        Assert.True(group.IsGroup);
        Assert.Equal("Shop", group.Title);
        Assert.Equal(["feature/cart", "release/2.0", "experiment"], group.Instances.Select(host => host.DisplayName));
        Assert.Equal(hosts.Select(host => host.Id), group.Instances.Select(host => host.Id));
        Assert.All(group.Instances, host =>
        {
            Assert.True(host.CanOpenDashboard);
            Assert.True(host.CanStop);
            Assert.True(host.IsPinned);
            Assert.Equal("Directory context and PID", host.Subtitle);
        });

    }

    [Fact]
    public void IdenticalNamesInUnrelatedRepositoriesAndDifferentAppHostsStaySeparate()
    {
        using var first = new TestAppHostRepository();
        using var second = new TestAppHostRepository();
        var hosts = new[] { Host(first.CreateWorktree("main", "main"), 1), Host(second.CreateWorktree("main", "main"), 2), Host(first.CreateWorktree("other", "main", "Other/apphost.cs"), 3) };
        var groups = AppHostGrouping.Create(hosts);
        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.False(group.IsGroup));
    }

    [Fact]
    public void SingletonRemainsDirectlyAccessible()
    {
        using var repo = new TestAppHostRepository();
        var host = Host(repo.CreateWorktree("cart", "feature/cart"), 1);
        var group = Assert.Single(AppHostGrouping.Create([host]));
        Assert.False(group.IsGroup);
        Assert.Same(host, Assert.Single(group.Instances));
    }

    [Fact]
    public void DuplicateBranchesUseDirectoryAndSameDirectoryUsesPid()
    {
        using var repo = new TestAppHostRepository();
        var first = Host(repo.CreateWorktree("first", "main"), 1);
        var second = Host(repo.CreateWorktree("second", "main"), 2);
        var third = first with { Id = first.Id with { AppHostPid = 3 } };
        var group = Assert.Single(AppHostGrouping.Create([first, second, third]));
        Assert.Equal(["main · " + first.Repository!.WorktreeDirectory + " · PID 1", "main · " + second.Repository!.WorktreeDirectory, "main · " + first.Repository.WorktreeDirectory + " · PID 3"], group.Instances.Select(host => host.DisplayName));
        Assert.Equal(3, group.Instances.Select(host => host.Title).Distinct().Count());
    }

    [Fact]
    public void TruncatedBranchLabelsStayDistinct()
    {
        using var repo = new TestAppHostRepository();
        var prefix = new string('x', 40);
        var suffix = new string('y', 40);
        var first = Host(repo.CreateWorktree("first", prefix + "first" + suffix), 1);
        var second = Host(repo.CreateWorktree("second", prefix + "second" + suffix), 2);
        var group = Assert.Single(AppHostGrouping.Create([first, second]));
        Assert.Equal(2, group.Instances.Select(host => host.Title).Distinct().Count());
        Assert.Equal([" · PID 1", " · PID 2"], group.Instances.Select(host => host.Title[^8..]));
    }

    [Fact]
    public void StoppedBranchLabelsStayDistinctWithoutProcessIds()
    {
        using var repo = new TestAppHostRepository();
        var prefix = new string('x', 40);
        var suffix = new string('y', 40);
        var first = Host(repo.CreateWorktree("first", prefix + "first" + suffix), 0) with
        {
            IsRunning = false, CanOpenDashboard = false, CanStop = false, CanStart = true
        };
        var second = Host(repo.CreateWorktree("second", prefix + "second" + suffix), 0) with
        {
            IsRunning = false, CanOpenDashboard = false, CanStop = false, CanStart = true
        };
        var group = Assert.Single(AppHostGrouping.Create([first, second]));
        Assert.Equal(2, group.Instances.Select(host => host.Title).Distinct().Count());
        Assert.Equal([" · #1", " · #2"], group.Instances.Select(host => host.Title[^5..]));
        Assert.Equal([first.Id, second.Id], group.Instances.Select(host => host.Id));
        Assert.All(group.Instances, host => Assert.True(host.CanStart));
    }

    [Fact]
    public void GeneratedDiscriminatorDoesNotCollideWithAnotherBranchLabel()
    {
        using var repo = new TestAppHostRepository();
        var prefix = new string('x', 40);
        var suffix = new string('y', 40);
        var first = Host(repo.CreateWorktree("first", prefix + "first" + suffix), 1);
        var second = Host(repo.CreateWorktree("second", prefix + "second" + suffix), 2);
        var label = Assert.Single(AppHostGrouping.Create([first, second])).Instances[0].Title;
        var third = Host(repo.CreateWorktree("third", label), 3);
        var group = Assert.Single(AppHostGrouping.Create([first, second, third]));
        Assert.Equal(3, group.Instances.Select(host => host.Title).Distinct().Count());
        Assert.Equal([first.Id, second.Id, third.Id], group.Instances.Select(host => host.Id));
    }

    [Fact]
    public void MissingHeadFallsBackToDirectoryAndMalformedPointerDoesNotGroup()
    {
        using var repo = new TestAppHostRepository();
        var firstPath = repo.CreateWorktree("first", "main");
        File.Delete(Path.Combine(repo.CommonDirectory, "worktrees", "first", "HEAD"));
        var first = Host(firstPath, 1);
        var second = Host(repo.CreateWorktree("second", "main"), 2);
        var group = Assert.Single(AppHostGrouping.Create([first, second]));
        Assert.Equal(["first", "main"], group.Instances.Select(host => host.Title));
        File.WriteAllText(Path.Combine(first.Repository!.WorktreeDirectory, ".git"), "invalid");
        Assert.Null(AppHostRepository.Read(firstPath));
        Assert.Equal(2, AppHostGrouping.Create([Host(firstPath, 1), second]).Count);
    }

    [Fact]
    public async Task ControllerGroupsByDefaultAndKeepsRecentsSeparate()
    {
        using var repo = new TestAppHostRepository();
        var first = new AppHostInfo(repo.CreateWorktree("first", "main"), 1, "http://localhost:19001") { ProcessStartTimeUnixMilliseconds = 1001 };
        var second = new AppHostInfo(repo.CreateWorktree("second", "feature/cart"), 2, null) { ProcessStartTimeUnixMilliseconds = 1002 };
        var recent = repo.CreateWorktree("third", "release/2.0");
        var client = new TestAppHostClient();
        var store = new TestTraySavedStateStore(new([new(recent, false, true)]));
        await using var controller = new TrayController(client, store);
        controller.Start();
        client.Publish(new([first, second], DiscoveryState.Live));
        await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.Count == 2);
        var group = Assert.Single(controller.State.MenuGroups);
        Assert.Equal("Shop", group.Title);
        Assert.Equal(["main", "feature/cart"], group.Instances.Select(host => host.Title));
        Assert.Equal(recent, Assert.Single(controller.State.RecentAppHosts).Id.AppHostPath);
        var original = group.Instances[0].Id;
        client.Publish(new([second, first with { ProcessStartTimeUnixMilliseconds = 2001 }], DiscoveryState.Live));
        await TestAppHostClient.WaitForStateAsync(controller, state => state.AppHosts.Any(host => host.Id.ProcessStartTimeUnixMilliseconds == 2001));
        Assert.Throws<InvalidOperationException>(() => controller.RequestStop(original));
        Assert.Empty(client.Requests);
        Assert.False(AppHostGrouping.HasSameStructure([group], controller.State.MenuGroups));
    }

    [Fact]
    public async Task RecentWorktreesGroupWithinHistoryAndKeepFirstOccurrenceOrder()
    {
        using var repo = new TestAppHostRepository();
        using var unrelated = new TestAppHostRepository();
        var first = TrayAppHostPath.Normalize(repo.CreateWorktree("cart", "feature/cart"));
        var other = TrayAppHostPath.Normalize(unrelated.CreateWorktree("main", "main"));
        var second = TrayAppHostPath.Normalize(repo.CreateWorktree("release", "release/2.0"));
        var detached = TrayAppHostPath.Normalize(repo.CreateWorktree("experiment", null));
        var client = new TestAppHostClient();
        var store = new TestTraySavedStateStore(new([new(first, false, true), new(other, false, true), new(second, false, true), new(detached, false, true)]));
        await using var controller = new TrayController(client, store);
        controller.Start();
        client.Publish(new([], DiscoveryState.Live));
        await TestAppHostClient.WaitForStateAsync(controller, state => state.Discovery == DiscoveryState.Live);
        Assert.Empty(controller.State.MenuGroups);
        var groups = controller.State.RecentMenuGroups;
        Assert.Equal(2, groups.Count);
        Assert.Equal([first, second, detached], groups[0].Instances.Select(host => host.Id.AppHostPath));
        Assert.Equal(["feature/cart", "release/2.0", "experiment"], groups[0].Instances.Select(host => host.Title));
        Assert.All(groups[0].Instances, host => Assert.True(host.CanStart));
        Assert.Equal(other, Assert.Single(groups[1].Instances).Id.AppHostPath);
        Assert.False(groups[1].IsGroup);
        Assert.Equal([first, other, second, detached], store.Load().AppHosts.Select(host => host.AppHostPath));
        controller.RemoveRecent(second);
        Assert.Equal([first, detached], controller.State.RecentMenuGroups[0].Instances.Select(host => host.Id.AppHostPath));
        Assert.False(AppHostGrouping.HasSameStructure(groups, controller.State.RecentMenuGroups));
        controller.RemoveRecent(detached);
        Assert.False(controller.State.RecentMenuGroups[0].IsGroup);
    }

    [Fact]
    public void UnavailableGitMetadataNeverGroupsDifferentDirectories()
    {
        using var directory = new TestTrayStateDirectory();
        var first = Host(directory.CreateAppHost("one/apphost.cs"), 1);
        var second = Host(directory.CreateAppHost("two/apphost.cs"), 2);
        Assert.Null(first.Repository);
        Assert.Null(second.Repository);
        Assert.Equal(2, AppHostGrouping.Create([first, second]).Count);
        var group = Assert.Single(AppHostGrouping.Create([first, first with { Id = first.Id with { AppHostPid = 3 } }]));
        Assert.Equal(2, group.Instances.Select(host => host.DisplayName).Distinct().Count());
    }

    [Fact]
    public void MainCheckoutAndLinkedWorktreeShareRepositoryIdentity()
    {
        using var repo = new TestAppHostRepository();
        var linked = repo.CreateWorktree("cart", "feature/cart");
        File.WriteAllText(Path.Combine(repo.CommonDirectory, "HEAD"), "ref: refs/heads/main\n");
        var main = Path.Combine(Path.GetDirectoryName(repo.CommonDirectory)!, "Shop.AppHost", "apphost.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(main)!);
        File.WriteAllText(main, "");
        Assert.True(Assert.Single(AppHostGrouping.Create([Host(main, 1), Host(linked, 2)])).IsGroup);
    }

    [Theory]
    [InlineData(AppHostHealth.Healthy, AppHostHealth.Healthy, AppHostHealth.Healthy)]
    [InlineData(AppHostHealth.Healthy, AppHostHealth.Warning, AppHostHealth.Warning)]
    [InlineData(AppHostHealth.Warning, AppHostHealth.Unhealthy, AppHostHealth.Unhealthy)]
    [InlineData(AppHostHealth.Healthy, AppHostHealth.Unknown, AppHostHealth.Unknown)]
    public void AggregateHealthUsesWorstKnownStateAndDoesNotHideUnknown(int firstValue, int secondValue, int expectedValue)
    {
        var first = new AppHostMenuItem(default, "Shop", "", "Shop", true, true, false, null) { Health = (AppHostHealth)firstValue };
        var group = new AppHostMenuGroup("Shop", [first, first with { Health = (AppHostHealth)secondValue }]);
        Assert.Equal((AppHostHealth)expectedValue, group.GetHealth(true));
        Assert.Equal(AppHostHealth.Unknown, group.GetHealth(false));
        Assert.Equal(AppHostHealth.Unhealthy, (group with { Instances = [first, first with { Error = "Failed" }] }).GetHealth(true));
        Assert.Equal(AppHostHealth.Warning, (group with { Instances = [first, first with { IsStopping = true }] }).GetHealth(true));
        Assert.Equal(AppHostHealth.Unknown, (group with { Instances = [first with { IsRunning = false }] }).GetHealth(true));
    }

    private static AppHostMenuItem Host(string path, int pid)
        => new(new(path, pid, 1000 + pid), "Shop", "Directory context and PID", "Shop", true, true, false, null)
        {
            Repository = AppHostRepository.Read(path), IsPinned = true, Health = AppHostHealth.Healthy
        };
}
