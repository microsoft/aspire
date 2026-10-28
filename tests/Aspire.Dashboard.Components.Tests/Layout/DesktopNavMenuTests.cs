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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Navigation_ReflectsAppHostMode(bool enabled, bool hasTerminals)
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
        var cut = Render<DesktopNavMenu>(builder => builder.Add(p => p.HasResourceTerminals, hasTerminals));
        var expected = new List<string>();
        if (enabled)
        {
            expected.Add(DashboardUrls.ResourceListUrl());
            if (hasTerminals)
            {
                expected.Add(DashboardUrls.TerminalsUrl());
            }
        }
        expected.Add(DashboardUrls.TelemetrySourcesUrl());
        var links = cut.FindAll("nav.desktop-nav-menu > a");
        Assert.Equal(expected, links.Select(link => link.GetAttribute("href")));
        if (enabled && hasTerminals)
        {
            var terminalLink = Assert.Single(links, link => link.GetAttribute("href") == DashboardUrls.TerminalsUrl());
            Assert.Equal(Resources.Layout.NavMenuTerminalsTab, terminalLink.GetAttribute("aria-label"));
            Assert.Equal("page", terminalLink.GetAttribute("aria-current"));
        }
        else if (!enabled)
        {
            var structuredLogsLink = Assert.Single(links, link => link.GetAttribute("href") == DashboardUrls.TelemetrySourcesUrl());
            Assert.Equal(Resources.Layout.NavMenuTelemetrySourcesTab, structuredLogsLink.GetAttribute("aria-label"));
            Assert.Equal("page", structuredLogsLink.GetAttribute("aria-current"));
        }
    }

    [Theory]
    [InlineData("/consolelogs")]
    [InlineData("/consolelogs/resource/api")]
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
    public void EmptyTelemetrySources_AreDisabledWithTheReason()
    {
        RenderNavMenu(DashboardUrls.ResourceListUrl());
        var cut = Render<DesktopNavMenu>(builder => builder.Add(p => p.Availability, new NavigationAvailability(HasResourceTerminals: false, HasParameters: false, HasTelemetrySources: false)));

        Assert.Equal(["desktop-nav-telemetry"], cut.FindAll(".desktop-nav-item.disabled").Select(item => item.Id));
        Assert.Equal(Resources.Layout.TelemetryPaneNoSources, cut.Find("#desktop-nav-telemetry").GetAttribute("aria-description"));

        cut.Render(builder => builder.Add(p => p.Availability, new NavigationAvailability(HasResourceTerminals: false, HasParameters: true, HasTelemetrySources: true)));

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
