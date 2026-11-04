// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Trace.V1;
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
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await cut.InvokeAsync(() => preferences.SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".resource-card").Count));
        Assert.Equal(GroupOverview.Cards, preferences.GroupOverview);
        Assert.True(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
        Assert.Equal(["Cards", "Home"], outlet.FindAll(".icon-toggle").Select(button => button.GetAttribute("aria-label")));
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
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        outlet.WaitForAssertion(() =>
        {
            Assert.Equal("Layout", outlet.Find(".icon-toggle-group").GetAttribute("aria-label"));
            Assert.Equal(["Cards", "Home", "Graph"], outlet.FindAll(".icon-toggle").Select(button => button.GetAttribute("aria-label")));
            Assert.Equal(missingParameters ? 1 : 0, outlet.FindAll(".missing-parameters-link").Count);
            Assert.Equal("true", outlet.Find("[aria-label='Cards']").GetAttribute("aria-pressed"));
        });

        outlet.Find("[aria-label='Home']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".home-hero")));
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Home']").GetAttribute("aria-pressed"));

        outlet.Find("[aria-label='Graph']").Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Equal(GroupOverview.Graph, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Graph']").GetAttribute("aria-pressed"));

        outlet.Find("[aria-label='Cards']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".resource-card")));
        Assert.Equal(GroupOverview.Cards, preferences.GroupOverview);
        Assert.Equal("true", outlet.Find("[aria-label='Cards']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Recording_ActivityWindowEndsAtLastRequest()
    {
        // Much older than the activity window, which would be empty if it ended at the current time.
        var requestTime = new DateTime(2020, 1, 15, 12, 0, 10, DateTimeKind.Utc);
        var cut = await RenderRecordingSummaryAsync(writer => writer.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope(),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(traceId: "1", spanId: "1", startTime: requestTime, endTime: requestTime.AddSeconds(1), kind: Span.Types.SpanKind.Server)
                        }
                    }
                }
            }
        }));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Activity", cut.Find("#home-activity-title").TextContent);
            Assert.Empty(cut.FindAll(".home-live-dot"));
            Assert.Single(cut.FindAll(".home-chart-bar"));
            Assert.Equal(2, cut.FindAll(".home-chart-axis span").Count);
        });
    }

    [Fact]
    public async Task Recording_WithoutRequests_HidesActivityAxis()
    {
        var cut = await RenderRecordingSummaryAsync(_ => Task.CompletedTask);

        cut.WaitForAssertion(() => Assert.Equal("No requests were recorded in this run.", cut.Find(".home-chart-empty").TextContent));
        Assert.Empty(cut.FindAll(".home-chart-axis"));
    }

    private async Task<IRenderedComponent<ApplicationSummary>> RenderRecordingSummaryAsync(Func<ITelemetryRepositoryWriter, Task> seed)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesLayout(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("api", state: KnownResourceState.Finished, resourceType: "Project")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            isReadOnly: true),
            configureServices: services => FluentUISetupHelpers.AddTelemetryRepositoryConfiguration(services, readOnly: true));
        await FluentUISetupHelpers.SeedTelemetryRepositoryAsync(this, seed);

        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));
        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Summary));
        return cut;
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
