// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayIconStatusTests
{
    [Fact]
    public void NothingRunningShowsThePlainLogo()
    {
        Assert.Equal(TrayIconStatus.Idle, TrayIconStatusRules.Get(TrayPanelSamples.Empty()));
        Assert.False(TrayIconStatusRules.HasBadge(TrayIconStatus.Idle));
        Assert.False(TrayIconStatusRules.IsDimmed(TrayIconStatus.Idle));
    }

    [Fact]
    public void HealthyRunningAppHostsShowTheRunningBadge()
    {
        var state = new TrayViewState(DiscoveryState.Live,
            [TrayPanelSamples.Running("/src/A/A.AppHost.csproj", 1), TrayPanelSamples.Running("/src/B/B.AppHost.csproj", 2, AppHostHealth.Unknown)], "");

        Assert.Equal(TrayIconStatus.Running, TrayIconStatusRules.Get(state));
        Assert.True(TrayIconStatusRules.HasBadge(TrayIconStatus.Running));
    }

    [Theory]
    [InlineData((int)AppHostHealth.Warning)]
    [InlineData((int)AppHostHealth.Unhealthy)]
    public void UnhealthyResourcesShowTheAttentionBadge(int health)
    {
        var state = new TrayViewState(DiscoveryState.Live, [TrayPanelSamples.Running("/src/A/A.AppHost.csproj", 1, (AppHostHealth)health)], "");

        Assert.Equal(TrayIconStatus.Attention, TrayIconStatusRules.Get(state));
    }

    [Fact]
    public void ActionErrorsAndNoticesShowTheAttentionBadgeWhileAppHostsRun()
    {
        var running = TrayPanelSamples.Running("/src/A/A.AppHost.csproj", 1);
        var failedStop = new TrayViewState(DiscoveryState.Live, [running with { Error = "Stop timed out." }], "");
        var notice = new TrayViewState(DiscoveryState.Live, [running], "") { Notice = "Unable to save AppHost history." };
        var noticeWhileIdle = new TrayViewState(DiscoveryState.Live, [], "") { Notice = "Unable to save AppHost history." };

        Assert.Equal(TrayIconStatus.Attention, TrayIconStatusRules.Get(failedStop));
        Assert.Equal(TrayIconStatus.Attention, TrayIconStatusRules.Get(notice));
        Assert.Equal(TrayIconStatus.Idle, TrayIconStatusRules.Get(noticeWhileIdle));
    }

    [Fact]
    public void StartingAppHostsCountAsRunning()
    {
        var state = new TrayViewState(DiscoveryState.Live, [TrayPanelSamples.Offline("/src/A/A.cs", starting: true)], "");

        Assert.Equal(TrayIconStatus.Running, TrayIconStatusRules.Get(state));
    }

    [Theory]
    [InlineData((int)DiscoveryState.Connecting, (int)TrayIconStatus.Connecting)]
    [InlineData((int)DiscoveryState.Disconnected, (int)TrayIconStatus.Unavailable)]
    [InlineData((int)DiscoveryState.Incompatible, (int)TrayIconStatus.Unavailable)]
    [InlineData((int)DiscoveryState.LimitExceeded, (int)TrayIconStatus.Unavailable)]
    public void DiscoveryProblemsDimTheLogoWithoutABadge(int discovery, int expected)
    {
        var state = new TrayViewState((DiscoveryState)discovery, [TrayPanelSamples.Running("/src/A/A.AppHost.csproj", 1)], "");
        var status = TrayIconStatusRules.Get(state);

        Assert.Equal((TrayIconStatus)expected, status);
        Assert.True(TrayIconStatusRules.IsDimmed(status));
        Assert.False(TrayIconStatusRules.HasBadge(status));
    }
}
