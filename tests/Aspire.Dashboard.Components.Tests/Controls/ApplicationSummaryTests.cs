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
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ApplicationSummaryTests : DashboardTestContext
{
    [Theory]
    [InlineData(GroupOverview.Cards)]
    [InlineData(GroupOverview.Resources)]
    public async Task Render_PersistentContainersHaveBadgedGlyphAndQualifiedType(GroupOverview overview)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("persistent", state: KnownResourceState.Running, properties: new()
                {
                    [KnownProperties.Container.Lifetime] = new(KnownProperties.Container.Lifetime, Value.ForString("Persistent"), false, null, 0, null, false)
                }),
                ModelTestHelpers.CreateResource("ordinary", state: KnownResourceState.Running)
            ], resourceChannelProvider: () => channel));
        var cut = Render<ApplicationSummary>(p => p.AddCascadingValue(viewport));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(overview));

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll(overview == GroupOverview.Cards ? ".resource-card" : ".home-resource-table tbody tr");
            Assert.Equal(2, items.Count);
            var persistent = items.Single(item => item.QuerySelector(".resource-name-text")!.TextContent == "persistent");
            var ordinary = items.Single(item => item.QuerySelector(".resource-name-text")!.TextContent == "ordinary");
            Assert.Single(persistent.QuerySelectorAll(".persistent-container-badge"));
            Assert.Empty(ordinary.QuerySelectorAll(".persistent-container-badge"));
            var typeSelector = overview == GroupOverview.Cards ? ".resource-card-type" : ".home-table-type";
            Assert.Equal("Container (persistent)", persistent.QuerySelector(typeSelector)!.TextContent);
            Assert.Equal("Container", ordinary.QuerySelector(typeSelector)!.TextContent);
            Assert.Empty(cut.FindAll(".persistent-container-icon"));
        });
    }

    [Fact]
    public void Render_DefaultSetting_ShowsResourceCards()
    {
        var cut = RenderSummary();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-card").Count));
        Assert.Empty(cut.FindAll(".home-hero"));
        Assert.Empty(cut.FindAll(".home-activity"));
        Assert.Empty(cut.FindAll(".home-insights"));
        Assert.Empty(cut.FindAll(".home-problems"));
    }

    [Fact]
    public async Task Render_CardsSetting_ShowsResourceCardsAndCardActions()
    {
        var cut = RenderSummary();
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Cards));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-card").Count));
        Assert.Empty(cut.FindAll(".home-hero"));
        Assert.Empty(cut.FindAll(".home-activity"));
        Assert.Empty(cut.FindAll(".home-insights"));
        Assert.Empty(cut.FindAll(".home-problems"));
        Assert.Empty(cut.FindAll(".home-resource-table"));
        Assert.Equal(["broken", "api"], cut.FindAll(".resource-card-title").Select(title => title.TextContent.Trim()));
        Assert.Equal(2, cut.FindComponents<ResourceActions>().Count);
        Assert.All(cut.FindComponents<ResourceActions>(), action => Assert.True(action.Instance.CardMode));
    }

    [Fact]
    public async Task Render_CardsSetting_UsesSelectedDensity()
    {
        var cut = RenderSummary();
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();
        await cut.InvokeAsync(() => preferences.SetGroupOverviewAsync(GroupOverview.Cards));

        cut.WaitForAssertion(() => Assert.Contains("resource-card-grid-comfortable", cut.Find(".resource-card-grid").ClassList));

        await cut.InvokeAsync(() => preferences.SetDensityAsync(ResourceListDensity.Compact));
        Assert.Contains("resource-card-grid-compact", cut.Find(".resource-card-grid").ClassList);

        await cut.InvokeAsync(() => preferences.SetDensityAsync(ResourceListDensity.Spacious));
        Assert.Contains("resource-card-grid-spacious", cut.Find(".resource-card-grid").ClassList);
    }

    [Fact]
    public async Task Render_CardsSetting_ParameterCopyActionRequiresResolvedValue()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource(
                    "resolved",
                    resourceType: KnownResourceTypes.Parameter,
                    state: KnownResourceState.Running,
                    properties: new()
                    {
                        [KnownProperties.Parameter.Value] = new(
                            KnownProperties.Parameter.Value,
                            Value.ForString("parameter-value"),
                            isValueSensitive: true,
                            knownProperty: null,
                            sortOrder: 0,
                            displayName: null,
                            isHighlighted: false)
                    }),
                ModelTestHelpers.CreateResource(
                    "unresolved",
                    resourceType: KnownResourceTypes.Parameter,
                    state: KnownResourceState.ValueMissing)
            ],
            resourceChannelProvider: () => channel));
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Cards));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-card-parameter").Count));
        var cards = cut.FindAll(".resource-card-parameter");
        Assert.Single(cards.Single(card => card.QuerySelector(".resource-card-title")!.TextContent.Trim() == "resolved").QuerySelectorAll(".resource-card-copy-value"));
        Assert.Empty(cards.Single(card => card.QuerySelector(".resource-card-title")!.TextContent.Trim() == "unresolved").QuerySelectorAll(".resource-card-copy-value"));
        var copyButton = cut.Find(".resource-card-copy-value");
        Assert.Equal("parameter-value", copyButton.GetAttribute("data-text"));
        Assert.Equal("Copy to clipboard", copyButton.GetAttribute("aria-label"));
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
        Assert.Empty(cut.FindAll(".home-resource-table"));
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "initializeResourcesGraph");
        Assert.Contains(module.Invocations, invocation => invocation.Identifier == "updateResourcesGraph");
    }

    [Fact]
    public async Task Render_GraphSettingWhenGraphDisabled_ShowsResourceCards()
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = true);
        var cut = RenderSummary();
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await cut.InvokeAsync(() => preferences.SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-card").Count));
        Assert.Equal(GroupOverview.Cards, preferences.GroupOverview);
        Assert.True(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
    }

    [Fact]
    public void Render_MobileViewport_CollapsesLayoutOptionsIntoMenu()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: true);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("broken", state: KnownResourceState.FailedToStart, resourceType: "Container")
            ],
            resourceChannelProvider: () => channel));
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        _ = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));

        outlet.WaitForAssertion(() =>
        {
            Assert.Empty(outlet.FindAll(".icon-toggle-group"));
            Assert.Equal("Cards", outlet.Find(".overview-layout-menu").TextContent.Trim());
            Assert.Equal("Layout: Cards", outlet.Find(".overview-layout-menu").GetAttribute("aria-label"));
        });
    }

    private IRenderedComponent<ApplicationSummary> RenderSummary(ViewportInformation? viewport = null)
    {
        viewport ??= new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("broken", state: KnownResourceState.FailedToStart, resourceType: "Container")
            ],
            resourceChannelProvider: () => channel));

        // The resource actions take the viewport from the layout.
        return Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
    }
}
