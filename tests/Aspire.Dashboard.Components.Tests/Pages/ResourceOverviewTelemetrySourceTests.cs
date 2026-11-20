// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Overview;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewTelemetrySourceTests : DashboardTestContext
{
    private static readonly ViewportInformation s_viewport = new(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TelemetrySource_ShowsResourceAttributesAndTelemetry(bool hasAppHost)
    {
        Setup(hasAppHost);
        // The source reported different attributes over time, so both environments are listed.
        await AddLogAsync("browser", [new("deployment.environment", "dev"), new("telemetry.sdk.language", "js")]);
        await AddLogAsync("browser", [new("deployment.environment", "prod"), new("telemetry.sdk.language", "js")]);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceOverviewUrl("browser"));

        var cut = RenderOverview("browser");

        cut.WaitForAssertion(() =>
        {
            var overview = cut.FindComponent<TelemetrySourceOverview>();
            Assert.Equal("browser", overview.Instance.Name);
            Assert.Equal(
                ["Telemetry source", "browser", "TestId"],
                overview.FindAll(".overview-properties dd").Select(dd => dd.TextContent.Trim()));
            Assert.Equal(
                [("deployment.environment", "dev"), ("deployment.environment", "prod"), ("telemetry.sdk.language", "js")],
                overview.FindComponent<PropertyGrid<TelemetryPropertyViewModel>>().Instance.Items!.Select(item => (item.Name, item.Value)));
            Assert.Equal(
                ["2", "0", "0"],
                overview.FindAll("[aria-labelledby='overview-logs-title'] .overview-stat-value").Select(stat => stat.TextContent));
        });

        // Like a parameter, a telemetry source has no console logs. The Console logs tab is still shown.
        Assert.Empty(cut.FindAll("[aria-labelledby='overview-console-title']"));
        Assert.True(cut.Instance.IsTabAvailable(ResourceTab.Overview));
        Assert.False(cut.Instance.IsTabAvailable(ResourceTab.Console));
        Assert.False(cut.Find(".resource-row-kind-telemetry").ClassList.Contains("resource-row-unavailable"));
        if (hasAppHost)
        {
            Assert.Equal(
                ["Resources", "Telemetry sources", "Parameters"],
                cut.FindAll(".resource-pane-section-header > span:first-child").Select(s => s.TextContent.Trim()));
        }
    }

    [Theory]
    [InlineData(0, "No telemetry", "state-warning")]
    [InlineData(2, "2 telemetry sources", "state-success")]
    public async Task Standalone_SummaryHeaderCountsTelemetrySourcesAndHasNoGraph(int sourceCount, string expectedHeadline, string expectedState)
    {
        Setup(hasAppHost: false);
        // A graph chosen for the app's resources doesn't apply to telemetry sources.
        var storage = Assert.IsType<TestLocalStorage>(Services.GetRequiredService<ILocalStorage>());
        storage.OnGetUnprotectedAsync = key => key == BrowserStorageKeys.GroupOverview ? (true, nameof(GroupOverview.Graph)) : (false, null);
        foreach (var name in new[] { "api", "browser" }.Take(sourceCount))
        {
            await AddLogAsync(name, []);
        }
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceListUrl());

        var cut = RenderOverview(resourceName: null);

        cut.WaitForAssertion(() => Assert.Equal(expectedHeadline, cut.Find(".home-telemetry-sources .home-hero-headline").TextContent));
        var header = cut.Find(".home-telemetry-sources");
        Assert.Contains(expectedState, header.ClassList);
        Assert.StartsWith("Started ", header.QuerySelector(".home-hero-sub")!.TextContent);
        Assert.Empty(header.QuerySelectorAll(".home-health-bar, .home-legend"));
        // Until the dashboard receives telemetry, the header explains how to send it, like the empty telemetry pages.
        string[] expectedHelpButtons = sourceCount == 0 ? ["Add telemetry"] : [];
        Assert.Equal(expectedHelpButtons, header.QuerySelectorAll(".telemetry-help-message .telemetry-help-button").Select(button => button.TextContent.Trim()));
        Assert.Single(cut.FindAll(".home-activity"));
        Assert.Empty(cut.FindAll(".home.graph-view"));
        Assert.Empty(cut.FindComponents<IconToggleGroup<GroupOverview>>());
        Assert.Equal(GroupOverview.Graph, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
    }

    [Fact]
    public async Task Standalone_SummaryHeaderAfterClearingTelemetryShowsNoSources()
    {
        Setup(hasAppHost: false);
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await AddLogAsync("browser", []);
        var resourceKey = new ResourceKey("browser", "TestId");
        await repository.ClearSelectedSignalsAsync(new() { [resourceKey.GetCompositeName()] = [AspireDataType.Resource] });
        Assert.True(repository.HasReceivedTelemetry);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.ResourceListUrl());

        var cut = RenderOverview(resourceName: null);

        cut.WaitForAssertion(() =>
        {
            var header = cut.Find(".home-telemetry-sources");
            Assert.Equal("No telemetry", header.QuerySelector(".home-hero-headline")!.TextContent);
            Assert.Contains("state-warning", header.ClassList);
            Assert.Empty(header.QuerySelectorAll(".telemetry-help-message"));
        });
    }

    // With an app host, the overview without a selection summarizes the app's resources, even in the telemetry pane.
    [Fact]
    public async Task AppHost_TelemetryPaneSummaryIsTheResourceSummary()
    {
        Setup(hasAppHost: true);
        await AddLogAsync("browser", []);
        Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardUrls.AddTelemetryPane(DashboardUrls.ResourceListUrl()));

        var cut = RenderOverview(resourceName: null);

        cut.WaitForAssertion(() => Assert.Equal("No resources need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.True(cut.Instance.IsTelemetryPane);
        Assert.False(cut.Instance.IsTabAvailable(ResourceTab.Console));
        Assert.Empty(cut.FindAll(".home-telemetry-sources"));
        Assert.Single(cut.FindComponents<IconToggleGroup<GroupOverview>>());
    }

    private void Setup(bool hasAppHost)
    {
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport, hasAppHost
            ? new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource("api-TestId"), ModelTestHelpers.CreateResource("api-key", resourceType: KnownResourceTypes.Parameter)],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
                consoleLogsChannelProvider: _ => Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>())
            : new TestDashboardClient(isEnabled: false));
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(string? resourceName) =>
        Render<ResourcesLayout>(p => p.AddCascadingValue(s_viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), resourceName);
            builder.CloseComponent();
        }));

    private Task AddLogAsync(string name, KeyValuePair<string, string>[] attributes) =>
        Services.GetRequiredService<SqliteTelemetryRepository>().AddLogsAsync(new AddContext(),
        [
            new ResourceLogs
            {
                Resource = TelemetryTestHelpers.CreateResource(name, attributes: attributes),
                ScopeLogs = { new ScopeLogs { Scope = TelemetryTestHelpers.CreateScope(), LogRecords = { TelemetryTestHelpers.CreateLogRecord() } } }
            }
        ]);
}
