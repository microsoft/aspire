// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ApplicationSummaryTests : DashboardTestContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_DefaultSetting_ShowsSummaryAndOnlyAvailableLayouts(bool graphEnabled)
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = !graphEnabled);
        var cut = RenderSummary();
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));

        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Single(cut.FindAll(".home-activity"));
        Assert.Single(cut.FindAll(".home-insights"));
        Assert.Single(cut.FindAll(".home-problems"));
        Assert.Empty(cut.FindAll(".resource-card, .home-resource-table"));
        Assert.Equal(graphEnabled ? ["Summary", "Graph"] : ["Summary"],
            outlet.FindAll(".icon-toggle").Select(e => e.GetAttribute("aria-label")));
        Assert.IsType<Icons.Regular.Size20.DataBarVertical>(
            outlet.FindComponent<IconToggleGroup<GroupOverview>>().Instance.Options.Single(option => option.Value == GroupOverview.Summary).Icon);
    }

    [Fact]
    public async Task Render_SelectionChanges_UpdatesSummaryWithoutChangingLayout()
    {
        var cut = RenderSummary();
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["api"])));
        cut.WaitForAssertion(() => Assert.Equal("All 1 resources are running", cut.Find(".home-hero-headline").TextContent));
        Assert.Single(cut.FindAll(".home-activity"));
        Assert.Single(cut.FindAll(".home-insights"));
        Assert.Single(cut.FindAll(".home-problems"));

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["broken"])));
        cut.WaitForAssertion(() => Assert.Equal("1 of 1 resources need attention", cut.Find(".home-hero-headline").TextContent));
        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["api", "broken"])));
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal(GroupOverview.Summary, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
    }

    [Fact]
    public async Task Render_GraphSetting_ShowsTheResourceGraph()
    {
        var module = JSInterop.SetupModule("/js/app-resourcegraph.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderSummary();

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Empty(cut.FindAll(".home-hero"));
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "initializeResourcesGraph");
        Assert.Contains(module.Invocations, invocation => invocation.Identifier == "updateResourcesGraph");
    }

    [Fact]
    public async Task Render_GraphSettingWhenGraphDisabled_ShowsSummary()
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = true);
        var cut = RenderSummary();
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await cut.InvokeAsync(() => preferences.SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".home-hero")));
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.True(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
        Assert.Equal(["Summary"], outlet.FindAll(".icon-toggle").Select(button => button.GetAttribute("aria-label")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void Render_LayoutToggleChangesViewsWithMissingParameters(bool isDesktop, bool missingParameters)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: !isDesktop);
        JSInterop.SetupModule("/js/app-resourcegraph.js").Mode = JSRuntimeMode.Loose;
        var cut = RenderSummary(viewport, missingParameters);
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        outlet.WaitForAssertion(() =>
        {
            Assert.Equal("Layout", outlet.Find(".icon-toggle-group").GetAttribute("aria-label"));
            Assert.Equal(["Summary", "Graph"], outlet.FindAll(".icon-toggle").Select(button => button.GetAttribute("aria-label")));
            Assert.Equal(missingParameters ? 1 : 0, outlet.FindAll(".missing-parameters-link").Count);
            Assert.Equal("true", outlet.Find("[aria-label='Summary']").GetAttribute("aria-pressed"));
        });
        if (missingParameters)
        {
            Assert.Contains("parameter", outlet.Find(".missing-parameters-link").GetAttribute("href"));
        }

        outlet.Find("[aria-label='Graph']").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Graph']").GetAttribute("aria-pressed"));

        outlet.Find("[aria-label='Summary']").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".home-hero")));
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Summary']").GetAttribute("aria-pressed"));
    }

    private IRenderedComponent<ApplicationSummary> RenderSummary(ViewportInformation? viewport = null, bool missingParameters = false)
    {
        viewport ??= new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("broken", state: KnownResourceState.FailedToStart, resourceType: "Container"),
                ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter,
                    state: missingParameters ? KnownResourceState.ValueMissing : KnownResourceState.Running)
            ],
            resourceChannelProvider: () => channel));

        return Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
    }
}
