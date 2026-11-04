// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ApplicationSummaryTests : DashboardTestContext
{
    [Fact]
    public async Task SavedDefault_ChangesWithoutChangingTheActiveOrSelectedLayout()
    {
        JSInterop.SetupModule("/js/app-resourcegraph.js").Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.SetupFluentList(this);
        SetupSummary();
        var stored = new Dictionary<string, object?> { [BrowserStorageKeys.GroupOverview] = "Summary" };
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => stored.TryGetValue(key, out var value) ? (true, value) : (false, null);
        storage.OnSetUnprotectedAsync = (key, value) => stored[key] = value;
        var tabState = new Dictionary<string, object?>();
        var sessionStorage = Assert.IsType<TestSessionStorage>(Services.GetRequiredService<ISessionStorage>());
        sessionStorage.OnGetAsync = key => tabState.TryGetValue(key, out var value) ? (true, value) : (false, null);
        sessionStorage.OnSetAsync = (key, value) => tabState[key] = value;
        var summary = Render<ApplicationSummary>(p => p.AddCascadingValue(DesktopViewport).Add(p => p.ResourceNames, ["api", "worker"]));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();
        await Services.GetRequiredService<ThemeManager>().EnsureInitializedAsync();
        var settings = Render<SettingsDialog>();
        var header = Render<SectionOutlet>(p => p.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));

        await settings.InvokeAsync(() => settings.Find(".icon-toggle[aria-label='Graph']").Click());
        summary.WaitForAssertion(() => Assert.Equal("All 2 resources are running", summary.Find(".home-hero-headline").TextContent));
        Assert.Equal("Graph", stored[BrowserStorageKeys.GroupOverview]);
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal("Summary", tabState[BrowserStorageKeys.ActiveGroupOverview]);

        await header.InvokeAsync(() => header.Find(".icon-toggle[aria-label='Graph']").Click());
        await settings.InvokeAsync(() => settings.Find(".icon-toggle[aria-label='Summary']").Click());
        summary.WaitForAssertion(() =>
        {
            Assert.Empty(summary.FindAll(".home-hero"));
            Assert.False(summary.Find(".home-resource-graph-layout").HasAttribute("hidden"));
        });
        Assert.Equal("Summary", settings.Find("[aria-label='Default overview layout'] .icon-toggle[aria-pressed='true']").GetAttribute("aria-label"));
        Assert.Equal(GroupOverview.Summary, preferences.DefaultGroupOverview);
        Assert.Equal("Summary", stored[BrowserStorageKeys.GroupOverview]);
        Assert.Equal("Graph", tabState[BrowserStorageKeys.ActiveGroupOverview]);
        Assert.Equal(["api", "worker"], summary.Instance.ResourceNames);

        summary.Dispose();
        var navigated = Render<ApplicationSummary>(p => p.AddCascadingValue(DesktopViewport).Add(p => p.ResourceNames, ["api", "worker"]));
        navigated.WaitForAssertion(() => Assert.False(navigated.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Equal("Graph", header.Find(".icon-toggle[aria-pressed='true']").GetAttribute("aria-label"));
        Assert.Equal("Summary", stored[BrowserStorageKeys.GroupOverview]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_DefaultSetting_ShowsHomeAndOnlyAvailableLayouts(bool graphEnabled)
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = !graphEnabled);
        SetupSummary();
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(DesktopViewport));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".home-hero"));
            Assert.Single(cut.FindAll(".home-activity"));
            Assert.Single(cut.FindAll(".home-insights"));
            Assert.Single(cut.FindAll(".home-problems"));
            Assert.Equal("1 of 3 resources need attention", cut.Find(".home-hero-headline").TextContent);
        });
        Assert.Empty(cut.FindAll(".resource-card, .home-resource-table"));
        Assert.Equal(graphEnabled ? ["Summary", "Graph"] : ["Summary"],
            outlet.FindAll(".icon-toggle").Select(e => e.GetAttribute("aria-label")));
        Assert.Equal("Summary", outlet.Find(".icon-toggle[aria-pressed='true']").GetAttribute("aria-label"));
        Assert.IsType<Icons.Regular.Size20.DataBarVertical>(
            outlet.FindComponent<IconToggleGroup<GroupOverview>>().Instance.Options.Single(option => option.Value == GroupOverview.Summary).Icon);
    }

    [Fact]
    public async Task Render_SelectionChanges_UpdatesHomeWithoutChangingTheLayout()
    {
        SetupSummary();
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(DesktopViewport));
        cut.WaitForAssertion(() => Assert.Equal("1 of 3 resources need attention", cut.Find(".home-hero-headline").TextContent));

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["api", "worker"])));
        cut.WaitForAssertion(() => Assert.Equal("All 2 resources are running", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal(["api", "worker"], cut.Instance.ResourceNames);
        Assert.Single(cut.FindAll(".home-activity"));
        Assert.Single(cut.FindAll(".home-insights"));
        Assert.Single(cut.FindAll(".home-problems"));
        Assert.Empty(cut.FindAll(".resource-card, .home-resource-table"));

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["worker", "broken"])));
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 resources need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal(["worker", "broken"], cut.Instance.ResourceNames);
        Assert.Equal(GroupOverview.Summary, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
    }

    [Fact]
    public async Task Render_SelectedResources_ScopesActivityInsightsErrorsAndTraces()
    {
        SetupSummary();
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var now = Services.GetRequiredService<BrowserTimeProvider>().GetUtcNow().UtcDateTime.AddSeconds(-5);
        foreach (var name in new[] { "api", "worker", "broken" })
        {
            var resource = CreateResource(name);
            resource.Attributes.Remove(resource.Attributes.Single(a => a.Key == "service.instance.id"));
            await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = resource,
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(time: now, message: $"{name} error", severity: SeverityNumber.Error) } } }
                }
            });
            await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = resource,
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = CreateScope(),
                            Spans =
                            {
                                CreateSpan(traceId: $"trace-{name}", spanId: $"span-{name}", startTime: now, endTime: now.AddMilliseconds(100),
                                    kind: Span.Types.SpanKind.Server, status: new Status { Code = Status.Types.StatusCode.Error })
                            }
                        }
                    }
                }
            });
        }
        Assert.Equal(["api", "broken", "worker"], repository.GetResources().Select(r => r.ResourceKey.GetCompositeName()).Order());
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(DesktopViewport).Add(p => p.ResourceNames, ["api", "worker"]));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["2", "2", "0"], cut.FindAll(".home-activity-totals strong").Select(e => e.TextContent));
            Assert.Equal(["api error", "worker error"], cut.FindAll("#home-errors-title").Single().ParentElement!.ParentElement!
                .QuerySelectorAll(".home-event-title").Select(e => e.TextContent).Order());
            Assert.Equal(["api: Test span. Id: span-api", "worker: Test span. Id: span-worker"], cut.FindAll("#home-traces-title").Single().ParentElement!.ParentElement!
                .QuerySelectorAll(".home-event-title").Select(e => e.TextContent).Order());
            Assert.Equal(["api", "worker"], cut.FindAll(".home-insight").ElementAt(1).QuerySelectorAll(".home-insight-name").Select(e => e.TextContent.Trim()).Order());
            Assert.Equal(["api", "worker"], cut.FindAll(".home-insight").ElementAt(2).QuerySelectorAll(".home-insight-name").Select(e => e.TextContent.Trim()).Order());
            Assert.Equal(2, cut.FindAll(".home-insight")[0].QuerySelectorAll(".home-insight-name").Length);
            Assert.Single(cut.FindAll(".home-chart-bar"));
        });

        await cut.InvokeAsync(() => cut.Render(builder => builder.Add(p => p.ResourceNames, ["no-telemetry"])));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["0", "0", "0"], cut.FindAll(".home-activity-totals strong").Select(e => e.TextContent));
            Assert.Empty(cut.FindAll(".home-event-list li, .home-insight-list li, .home-chart-bar"));
        });
    }

    [Fact]
    public async Task Render_SidebarSearch_DoesNotChangeHomeScopeAndGroupSelectionIsPreserved()
    {
        SetupSummary();
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.ResourcePaneGrouping ? (true, "ResourceType") : (false, null);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources");
        var cut = Render<ResourcesLayout>(p => p.AddCascadingValue(DesktopViewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.CloseComponent();
        }));
        cut.WaitForAssertion(() => Assert.Equal("1 of 3 resources need attention", cut.Find(".home-hero-headline").TextContent));
        var search = cut.FindComponent<FluentTextInput>();

        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("api"));

        cut.WaitForAssertion(() => Assert.Equal(["api"], cut.FindAll(".resource-row-name").Select(e => e.TextContent.Trim())));
        Assert.Equal("1 of 3 resources need attention", cut.Find(".home-hero-headline").TextContent);
        Assert.Empty(cut.Instance.SelectedResourceNames);

        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync(string.Empty));
        cut.Find("[data-resource-group-key='ResourceType:Project'] .resource-tag-link").Click();
        cut.Render();

        cut.WaitForAssertion(() => Assert.Equal(["api", "worker"], cut.Instance.SelectedResourceNames.Order()));
        Assert.Equal(["api", "worker"], cut.FindComponent<ApplicationSummary>().Instance.ResourceNames!.Order());
        Assert.Equal("All 2 resources are running", cut.Find(".home-hero-headline").TextContent);
        Assert.Empty(cut.FindAll(".resource-card, .home-resource-table"));
    }

    [Fact]
    public async Task Render_GraphSetting_ShowsTheResourceGraph()
    {
        var module = JSInterop.SetupModule("/js/app-resourcegraph.js");
        module.Mode = JSRuntimeMode.Loose;
        SetupSummary();
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(DesktopViewport));

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden")));
        Assert.Empty(cut.FindAll(".home-hero, .resource-card, .home-resource-table"));
        Assert.Single(module.Invocations, invocation => invocation.Identifier == "initializeResourcesGraph");
        Assert.Contains(module.Invocations, invocation => invocation.Identifier == "updateResourcesGraph");

        await cut.InvokeAsync(() => Services.GetRequiredService<DashboardViewPreferences>().SetGroupOverviewAsync(GroupOverview.Summary));
        Assert.Single(cut.FindAll(".home-hero"));
        Assert.True(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
    }

    [Fact]
    public async Task Render_GraphSettingWhenGraphDisabled_ShowsHome()
    {
        Services.Configure<DashboardOptions>(options => options.UI.DisableResourceGraph = true);
        SetupSummary();
        var cut = Render<ApplicationSummary>(builder => builder.AddCascadingValue(DesktopViewport));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        await cut.InvokeAsync(() => preferences.SetGroupOverviewAsync(GroupOverview.Graph));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".home-hero")));
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.True(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
    }

    [Fact]
    public void Render_MobileViewport_CollapsesLayoutOptionsIntoMenu()
    {
        var viewport = new ViewportInformation(IsDesktop: false, IsUltraLowHeight: false, IsUltraLowWidth: true);
        SetupSummary(viewport);
        var outlet = Render<SectionOutlet>(builder => builder.Add(p => p.SectionName, ResourcesLayout.HeaderActionsSectionName));
        _ = Render<ApplicationSummary>(builder => builder.AddCascadingValue(viewport));

        outlet.WaitForAssertion(() =>
        {
            Assert.Empty(outlet.FindAll(".icon-toggle-group"));
            Assert.Equal("Summary", outlet.Find(".overview-layout-menu").TextContent.Trim());
            Assert.Equal("Layout: Summary", outlet.Find(".overview-layout-menu").GetAttribute("aria-label"));
        });
        Assert.IsType<Icons.Regular.Size20.DataBarVertical>(outlet.FindComponent<AspireMenuButton>().Instance.IconStart);
        outlet.Find(".overview-layout-menu").Click();
        Assert.Equal(["Summary", "Graph"], outlet.FindAll("[role='menuitemradio']").Select(e => e.TextContent.Trim()));
    }

    private static ViewportInformation DesktopViewport => new(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

    private void SetupSummary(ViewportInformation? viewport = null)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        ResourceSetupHelpers.SetupResourceOverview(this, viewport ?? DesktopViewport, new TestDashboardClient(isEnabled: true,
            initialResources:
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("worker", state: KnownResourceState.Running, resourceType: "Project"),
                ModelTestHelpers.CreateResource("broken", state: KnownResourceState.FailedToStart, resourceType: "Container")
            ],
            resourceChannelProvider: () => channel));
    }
}
