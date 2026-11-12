// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class DesktopNavMenuTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Navigation_ReflectsAppHostMode(bool enabled)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: enabled));
        if (!enabled)
        {
            Services.GetRequiredService<ResourcePaneState>().SetMode(ResourcePaneMode.Tags);
        }
        Services.GetRequiredService<NavigationManager>().NavigateTo(enabled ? "/terminals/resource/shell" : "/traces/detail/abc");
        Render<FluentTooltipProvider>();
        var cut = Render<DesktopNavMenu>();
        var expected = new List<string>();
        if (enabled)
        {
            expected.Add(DashboardUrls.ResourceOverviewUrl("shell"));
        }
        expected.Add(DashboardUrls.TelemetrySourcesUrl());
        var links = cut.FindAll("nav.desktop-nav-menu > a");
        Assert.Equal(expected, links.Select(link => link.GetAttribute("href")));
        if (enabled)
        {
            Assert.Equal("desktop-nav-resources", cut.Find("a[aria-current='page']").Id);
        }
        if (!enabled)
        {
            var structuredLogsLink = Assert.Single(links, link => link.GetAttribute("href") == DashboardUrls.TelemetrySourcesUrl());
            Assert.Equal(Resources.Layout.NavMenuTelemetrySourcesTab, structuredLogsLink.GetAttribute("aria-label"));
            Assert.Equal("page", structuredLogsLink.GetAttribute("aria-current"));
        }
    }

    [Theory]
    [InlineData("/consolelogs")]
    [InlineData("/consolelogs/resource/api")]
    [InlineData("/terminals/resource/shell")]
    [InlineData("/structuredlogs/resource/api")]
    [InlineData("/traces/detail/abc")]
    [InlineData("/metrics/resource/api")]
    public void SelectionPages_HighlightResources(string url)
    {
        RenderNavMenu(url);
        var cut = Render<DesktopNavMenu>();

        // The pages of a selection have their own entries in the top navigation, so the app navigation has no entries for them.
        Assert.Equal("desktop-nav-resources", cut.Find("a[aria-current='page']").Id);
    }

    [Fact]
    public void Resources_KeepsTheCurrentSelection()
    {
        string[] selection = ["api", "worker"];
        RenderNavMenu(DashboardUrls.AddResourceSelection(DashboardUrls.ConsoleLogsUrl(), selection));
        var cut = Render<DesktopNavMenu>();

        Assert.Equal(DashboardUrls.AddResourceSelection(DashboardUrls.ResourceOverviewUrl(), selection), cut.Find("#desktop-nav-resources").GetAttribute("href"));
    }

    [Fact]
    public void Resources_FromSelectedTelemetrySource_ClearsTheTelemetrySelection()
    {
        RenderNavMenu(DashboardUrls.AddTelemetryPane(DashboardUrls.StructuredLogsUrl("telemetry-source")));
        var cut = Render<DesktopNavMenu>();

        Assert.Equal(DashboardUrls.ResourceListUrl(), cut.Find("#desktop-nav-resources").GetAttribute("href"));
    }

    [Fact]
    public void EmptyTelemetrySources_StayEnabled()
    {
        RenderNavMenu(DashboardUrls.ResourceListUrl());
        var cut = Render<DesktopNavMenu>();

        var telemetry = cut.Find("#desktop-nav-telemetry");
        Assert.Equal(DashboardUrls.TelemetrySourcesUrl(), telemetry.GetAttribute("href"));
        Assert.Null(telemetry.GetAttribute("aria-disabled"));
        Assert.Empty(cut.FindAll(".desktop-nav-item.disabled"));
    }

    private void RenderNavMenu(string url)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: true));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        Render<FluentTooltipProvider>();
    }
}
