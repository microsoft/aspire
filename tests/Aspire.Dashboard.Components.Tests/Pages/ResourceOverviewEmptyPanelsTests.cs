// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Aspire.Tests.Shared.Telemetry;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewEmptyPanelsTests : DashboardTestContext
{
    private static readonly ViewportInformation s_viewport = new(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

    [Fact]
    public async Task TelemetryStats_LinkToAllMetricsAndFailedTracesForResource()
    {
        const string resourceName = "test-TestId";
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource(resourceName)],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/resources/{resourceName}");
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, AspireDataType.Metrics);
        await TelemetryHelpSetupHelpers.AddSignalAsync(repository, AspireDataType.Traces);

        var cut = RenderOverview(s_viewport, resourceName);
        cut.WaitForAssertion(() =>
        {
            var metricsLinks = cut.FindAll("[aria-labelledby='overview-metrics-title'] a.overview-stat");
            Assert.Equal(["Instruments", "Meters"], metricsLinks.Select(link => link.QuerySelector(".overview-stat-label")!.TextContent));
            Assert.All(metricsLinks, link => Assert.Equal($"/metrics/resource/{resourceName}?all=true", link.GetAttribute("href")));

            var traceLinks = cut.FindAll("[aria-labelledby='overview-traces-title'] a.overview-stat");
            Assert.Equal(["Total", "Failed"], traceLinks.Select(link => link.QuerySelector(".overview-stat-label")!.TextContent));
            Assert.Equal($"/traces/resource/{resourceName}", traceLinks[0].GetAttribute("href"));
            Assert.Equal("0", traceLinks[1].QuerySelector(".overview-stat-value")!.TextContent);
            var failedUrl = navigation.ToAbsoluteUri(traceLinks[1].GetAttribute("href")!);
            Assert.Equal($"/traces/resource/{resourceName}", failedUrl.AbsolutePath);
            var filter = Assert.Single(TelemetryFilterFormatter.DeserializeFiltersFromString(QueryHelpers.ParseQuery(failedUrl.Query)["filters"].ToString()));
            Assert.True(filter.Enabled);
            Assert.Equal(KnownTraceFields.StatusField, filter.Field);
            Assert.Equal(FilterCondition.Equals, filter.Condition);
            Assert.Equal(nameof(OtlpSpanStatusCode.Error), filter.Value);

            var traceRow = Assert.Single(cut.FindAll("[aria-labelledby='overview-traces-title'] a.overview-list-row-link"));
            Assert.Equal("test: Test span. Id: span", traceRow.GetAttribute("data-tooltip"));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuredLogPreview_ShowsCompactSeverityWithAccessibleText(bool showAttentionLogs)
    {
        const string resourceName = "test-TestId";
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource(resourceName)],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/resources/{resourceName}");
        var logs = new ScopeLogs { Scope = TelemetryTestHelpers.CreateScope() };
        (SeverityNumber Number, string Level)[] levels = showAttentionLogs
            ? [(SeverityNumber.Error, "Error"), (SeverityNumber.Fatal, "Critical"), (SeverityNumber.Warn, "Warning")]
            : [(SeverityNumber.Info, "Information"), (SeverityNumber.Debug, "Debug"), (SeverityNumber.Trace, "Trace")];
        foreach (var (number, level) in levels)
        {
            logs.LogRecords.Add(TelemetryTestHelpers.CreateLogRecord(message: $"{level} message", severity: number));
        }
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        await repository.AddLogsAsync(new(), [new ResourceLogs { Resource = TelemetryTestHelpers.CreateResource("test"), ScopeLogs = { logs } }]);

        var cut = RenderOverview(s_viewport, resourceName);
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[aria-labelledby='overview-logs-title'] a.overview-list-row-link");
            Assert.Equal(levels.Length, rows.Count);
            foreach (var (_, level) in levels)
            {
                var row = Assert.Single(rows, r => r.QuerySelector(".overview-list-value")!.TextContent == $"{level} message");
                Assert.Equal($"{level} message", row.GetAttribute("data-tooltip"));
                var severity = row.QuerySelector(".overview-level")!;
                Assert.Equal(level, severity.GetAttribute("data-tooltip"));
                Assert.Equal(level, severity.QuerySelector(".visually-hidden")!.TextContent);
                var indicatorSelector = showAttentionLogs ? "svg[aria-hidden='true']" : $".overview-log-level-dot[data-log-level='{level}'][aria-hidden='true']";
                Assert.Single(severity.QuerySelectorAll(indicatorSelector));
            }
        });
    }

    [Theory]
    [InlineData(KnownResourceTypes.Container)]
    [InlineData(KnownResourceTypes.Executable)]
    [InlineData(KnownResourceTypes.Project)]
    public async Task Panels_DefaultsToHideEmptyAndRestoresAllPanelsWithoutNavigation(string resourceType)
    {
        var console = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        await console.Writer.WriteAsync([]);
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test", resourceType: resourceType)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            consoleLogsChannelProvider: _ => console));
        var cut = RenderOverview(s_viewport);
        cut.WaitForAssertion(() => Assert.Contains("No console output", cut.Find("[aria-labelledby='overview-console-title']").TextContent));
        Assert.Equal(["overview-tags-title", "overview-status-title"], GetVisiblePanels(cut));
        Assert.Contains("--overview-span: 12;", cut.Find("[aria-labelledby='overview-status-title']").GetAttribute("style"));
        var tabs = cut.FindAll("[role='tab']").Select(t => t.TextContent).ToArray();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var initialUrl = navigation.Uri;
        var selector = cut.Find(".resource-header-controls .overview-panel-selector [role='group']");
        Assert.Equal("Panels", selector.GetAttribute("aria-label"));
        Assert.Equal(["Hide empty panels", "Show all panels"], selector.QuerySelectorAll("button").Select(b => b.GetAttribute("aria-label")));
        var all = cut.Find(".overview-panel-selector [aria-label='Show all panels']");
        var withContent = cut.Find(".overview-panel-selector [aria-label='Hide empty panels']");
        Assert.Equal("false", all.GetAttribute("aria-pressed"));
        Assert.Equal("true", withContent.GetAttribute("aria-pressed"));

        await SelectPanelsAsync(cut, hideEmpty: false);
        var titles = GetVisiblePanels(cut);
        Assert.Equal(12, titles.Length);
        Assert.Contains("--overview-span: 3;", cut.Find("[aria-labelledby='overview-status-title']").GetAttribute("style"));

        await SelectPanelsAsync(cut, hideEmpty: true);
        await SelectPanelsAsync(cut, hideEmpty: true);

        withContent = cut.Find(".overview-panel-selector [aria-label='Hide empty panels']");
        all = cut.Find(".overview-panel-selector [aria-label='Show all panels']");
        Assert.Equal("true", withContent.GetAttribute("aria-pressed"));
        Assert.Equal("false", all.GetAttribute("aria-pressed"));
        Assert.Equal(["overview-tags-title", "overview-status-title"], GetVisiblePanels(cut));
        Assert.Equal(tabs, cut.FindAll("[role='tab']").Select(t => t.TextContent).ToArray());
        Assert.Equal(initialUrl, navigation.Uri);
        Assert.Equal(["test"], cut.Instance.SelectedResourceNames);

        await SelectPanelsAsync(cut, hideEmpty: false);

        withContent = cut.Find(".overview-panel-selector [aria-label='Hide empty panels']");
        all = cut.Find(".overview-panel-selector [aria-label='Show all panels']");
        Assert.Equal("false", withContent.GetAttribute("aria-pressed"));
        Assert.Equal("true", all.GetAttribute("aria-pressed"));
        Assert.Equal(titles, GetVisiblePanels(cut));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Panels_IconOnlyChoicesExposeNamesTooltipsAndState(bool isDesktop)
    {
        var viewport = s_viewport with { IsDesktop = isDesktop };
        ResourceSetupHelpers.SetupResourceOverview(this, viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource("test")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test");
        var cut = RenderOverview(viewport);
        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        var selector = cut.Find(".resource-header-controls .overview-panel-selector [role='group']");
        var options = cut.FindAll(".overview-panel-selector button");
        // The choices are icons with tooltips; the group's name is for assistive technologies, not shown.
        Assert.Equal("Panels", selector.GetAttribute("aria-label"));
        Assert.Equal(string.Empty, selector.TextContent.Trim());
        Assert.Equal(["Hide empty panels", "Show all panels"], options.Select(option => option.GetAttribute("aria-label")));
        Assert.All(options, option =>
        {
            Assert.Equal(option.GetAttribute("aria-label"), option.GetAttribute("data-tooltip"));
        });
        Assert.Equal(["true", "false"], options.Select(option => option.GetAttribute("aria-pressed")));
        Assert.True(cut.Find("[aria-labelledby='overview-endpoints-title']").HasAttribute("hidden"));

        await SelectPanelsAsync(cut, hideEmpty: false);

        options = cut.FindAll(".overview-panel-selector button");
        Assert.False(cut.Find("[aria-labelledby='overview-endpoints-title']").HasAttribute("hidden"));
        Assert.Equal(["false", "true"], options.Select(option => option.GetAttribute("aria-pressed")));

        await SelectPanelsAsync(cut, hideEmpty: true);

        options = cut.FindAll(".overview-panel-selector button");
        Assert.True(cut.Find("[aria-labelledby='overview-endpoints-title']").HasAttribute("hidden"));
        Assert.Equal(["true", "false"], options.Select(option => option.GetAttribute("aria-pressed")));
    }

    [Fact]
    public async Task ConsolePreview_KeepsTenNewestLinesAcrossUpdatesAndPanelChoices()
    {
        var console = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            consoleLogsChannelProvider: _ => console));
        var cut = RenderOverview(s_viewport);
        await console.Writer.WriteAsync(Enumerable.Range(1, 16)
            .Select(number => new ResourceLogLine(number, $"line-{number}", false)).ToArray());
        cut.WaitForAssertion(() => Assert.Equal(
            Enumerable.Range(7, 10).Select(number => $"line-{number}"),
            cut.FindAll(".overview-console-line").Select(line => line.TextContent)));

        await SelectPanelsAsync(cut, hideEmpty: true);
        await console.Writer.WriteAsync(Enumerable.Range(17, 4)
            .Select(number => new ResourceLogLine(number, $"line-{number}", false)).ToArray());
        cut.WaitForAssertion(() => Assert.Equal(
            Enumerable.Range(11, 10).Select(number => $"line-{number}"),
            cut.FindAll(".overview-console-line").Select(line => line.TextContent)));

        await SelectPanelsAsync(cut, hideEmpty: false);
        Assert.Equal(
            Enumerable.Range(11, 10).Select(number => $"line-{number}"),
            cut.FindAll(".overview-console-line").Select(line => line.TextContent));
        Assert.False(cut.Find("[aria-labelledby='overview-console-title']").HasAttribute("hidden"));
    }

    [Fact]
    public async Task HiddenPanels_AppearWhenResourceAndConsoleDataArrive()
    {
        var changes = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var console = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        await console.Writer.WriteAsync([]);
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test"), ModelTestHelpers.CreateResource("other")],
            resourceChannelProvider: () => changes,
            consoleLogsChannelProvider: _ => console));
        var cut = RenderOverview(s_viewport);
        await SelectPanelsAsync(cut, hideEmpty: true);
        cut.WaitForAssertion(() => Assert.Equal(["overview-tags-title", "overview-status-title"], GetVisiblePanels(cut)));

        var populated = ModelTestHelpers.CreateResource("test",
            urls: [new("http", new Uri("http://localhost:5000"), false, false, UrlDisplayPropertiesViewModel.Empty)],
            volumes: [new(0, "data", "/data", "volume", false)],
            healthReports: [new("pending-check", null, null, null)],
            relationships: [new("other", "Reference")],
            properties: new()
            {
                ["enabled"] = new("enabled", Value.ForBool(false), false, null, 0, null, true)
            },
            environment: [new("EMPTY_VALUE", string.Empty, true)]);
        await changes.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, populated)]);
        await console.Writer.WriteAsync([new(1, "Application output", false)]);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                ["overview-tags-title", "overview-status-title", "overview-endpoints-title", "overview-console-title",
                 "overview-relationships-title", "overview-properties-title", "overview-environment-title",
                 "overview-volumes-title", "overview-health-title"],
                GetVisiblePanels(cut));
            Assert.Contains("Application output", cut.Find("[aria-labelledby='overview-console-title']").TextContent);
            Assert.Contains("false", cut.Find("[aria-labelledby='overview-properties-title']").TextContent);
            Assert.Contains("EMPTY_VALUE", cut.Find("[aria-labelledby='overview-environment-title']").TextContent);
            Assert.Contains("pending-check", cut.Find("[aria-labelledby='overview-health-title']").TextContent);
        });
        Assert.Equal(["test"], cut.Instance.SelectedResourceNames);

        await changes.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, ModelTestHelpers.CreateResource("test"))]);
        cut.WaitForAssertion(() => Assert.Equal(
            ["overview-tags-title", "overview-status-title", "overview-console-title"], GetVisiblePanels(cut)));
    }

    [Fact]
    public async Task Properties_DefaultToShowAllAndRetainExplicitCoreFilterControls()
    {
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test",
                properties: new()
                {
                    ["custom"] = new("custom", Value.ForString("custom-value"), false, null, 0, null, false)
                },
                environment: [new("RUNTIME_ONLY", "runtime-value", false)])],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = RenderOverview(s_viewport);
        var properties = cut.Find("[aria-labelledby='overview-properties-title']");
        var environment = cut.Find("[aria-labelledby='overview-environment-title']");
        Assert.False(properties.HasAttribute("hidden"));
        Assert.Equal("1", properties.QuerySelector(".overview-card-count")!.TextContent);
        Assert.Contains("custom-value", properties.TextContent);
        Assert.False(environment.HasAttribute("hidden"));
        Assert.NotEmpty(properties.QuerySelectorAll(".overview-card-actions fluent-button"));
        Assert.NotEmpty(environment.QuerySelectorAll(".overview-card-actions fluent-button"));

        cut.Find("[aria-labelledby='overview-properties-title'] [aria-label='Show core properties']").Click();
        properties = cut.Find("[aria-labelledby='overview-properties-title']");
        Assert.False(properties.HasAttribute("hidden"));
        Assert.Equal("0", properties.QuerySelector(".overview-card-count")!.TextContent);
        Assert.NotNull(properties.QuerySelector("[aria-label='Show all properties']"));

        await SelectPanelsAsync(cut, hideEmpty: false);
        await SelectPanelsAsync(cut, hideEmpty: true);
        properties = cut.Find("[aria-labelledby='overview-properties-title']");
        Assert.False(properties.HasAttribute("hidden"));
        Assert.Equal("0", properties.QuerySelector(".overview-card-count")!.TextContent);
        cut.Find("[aria-labelledby='overview-properties-title'] [aria-label='Show all properties']").Click();
        properties = cut.Find("[aria-labelledby='overview-properties-title']");
        Assert.False(properties.HasAttribute("hidden"));
        Assert.Equal("1", properties.QuerySelector(".overview-card-count")!.TextContent);
        Assert.Contains("custom-value", properties.TextContent);
        Assert.NotNull(properties.QuerySelector("[aria-label='Show core properties']"));
        Assert.False(cut.Find("[aria-labelledby='overview-environment-title']").HasAttribute("hidden"));
    }

    [Fact]
    public async Task ConsoleLoadingAndPause_RetainPanelAndResumeControl()
    {
        var console = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            consoleLogsChannelProvider: _ => console));
        var cut = RenderOverview(s_viewport);
        Assert.False(cut.Find("[aria-labelledby='overview-console-title']").HasAttribute("hidden"));
        await cut.InvokeAsync(() => cut.Find("[aria-label='Pause incoming data']").Click());
        await console.Writer.WriteAsync([]);
        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find("[aria-labelledby='overview-console-title']");
            Assert.False(panel.HasAttribute("hidden"));
            Assert.NotNull(panel.QuerySelector("[aria-label='Resume incoming data']"));
            Assert.NotNull(panel.QuerySelector(".overview-console-paused"));
        });

        await SelectPanelsAsync(cut, hideEmpty: false);
        await SelectPanelsAsync(cut, hideEmpty: true);
        await cut.InvokeAsync(() => cut.Find("[aria-label='Resume incoming data']").Click());
        cut.WaitForAssertion(() => Assert.True(cut.Find("[aria-labelledby='overview-console-title']").HasAttribute("hidden")));
    }

    [Fact]
    public async Task Terminal_PanelChoicesPreserveViewerAndConnection()
    {
        TerminalSetupHelpers.SetupTerminalView(this);
        TerminalSetupHelpers.SetupTerminalWindows(this);
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("test", autoFit: true)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = RenderOverview(s_viewport);
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<TerminalView>()));
        var viewer = cut.FindComponent<TerminalView>().Instance;

        await SelectPanelsAsync(cut, hideEmpty: false);
        await SelectPanelsAsync(cut, hideEmpty: true);

        Assert.Same(viewer, cut.FindComponent<TerminalView>().Instance);
        Assert.False(cut.Find(".overview-terminal-card").HasAttribute("hidden"));
        Assert.Single(JSInterop.Invocations, i => i.Identifier == "initTerminal");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Terminal_DockTransitionsPackOnlyRenderedOverviewCards(bool hideEmpty)
    {
        TerminalSetupHelpers.SetupTerminalView(this);
        TerminalSetupHelpers.SetupTerminalWindows(this);
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [TerminalSetupHelpers.CreateTerminalResource("test", autoFit: true)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        var cut = RenderOverview(s_viewport);
        await SelectPanelsAsync(cut, hideEmpty);
        var dock = Services.GetRequiredService<TerminalDockState>();

        await cut.InvokeAsync(() => dock.Update(isVisible: true, resourceName: "test"));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".overview-terminal-card"));
            Assert.Empty(cut.FindAll("[aria-labelledby='overview-console-title']"));
            Assert.Equal(
                hideEmpty ? "--overview-span: 5; --overview-span-md: 6; --overview-card-height: 260px;"
                    : "--overview-span: 3; --overview-span-md: 6; --overview-card-height: 260px;",
                cut.Find("[aria-labelledby='overview-status-title']").GetAttribute("style"));
            Assert.Equal(
                hideEmpty ? "--overview-span: 7; --overview-span-md: 6; --overview-card-height: 340px;"
                    : "--overview-span: 6; --overview-span-md: 6; --overview-card-height: 340px;",
                cut.Find("[aria-labelledby='overview-properties-title']").GetAttribute("style"));
            if (!hideEmpty)
            {
                Assert.Equal("--overview-span: 6; --overview-span-md: 6; --overview-card-height: 300px;",
                    cut.Find("[aria-labelledby='overview-logs-title']").GetAttribute("style"));
            }
        });

        await cut.InvokeAsync(() => dock.Update(isVisible: false, resourceName: null));

        cut.WaitForAssertion(() =>
        {
            var terminal = Assert.Single(cut.FindAll(".overview-terminal-card"));
            Assert.Equal(
                hideEmpty ? "--overview-span: 8; --overview-span-md: 12; --overview-card-height: 300px;"
                    : "--overview-span: 7; --overview-span-md: 12; --overview-card-height: 300px;",
                terminal.GetAttribute("style"));
        });
    }

    [Theory]
    [InlineData(null, KnownResourceTypes.Project)]
    [InlineData("test", KnownResourceTypes.Parameter)]
    public void SummaryAndParameter_DoNotOfferPanelChoices(string? resourceName, string resourceType)
    {
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test", resourceType: resourceType)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo(resourceName is null ? "/resources" : "/resources/test");
        var cut = RenderOverview(s_viewport, resourceName);

        cut.WaitForAssertion(() => Assert.True(cut.Instance.IsLoaded));
        Assert.Empty(cut.FindAll(".overview-panel-selector"));
    }

    [Theory]
    [InlineData(KnownResourceTypes.Parameter)]
    [InlineData(KnownResourceTypes.Project)]
    public void Status_ShowsOnlyBasicMetadataAndNoInlineCommandsForParameters(string resourceType)
    {
        var command = new CommandViewModel("refresh", CommandViewModelState.Enabled, "Refresh status", "",
            confirmationMessage: "", argumentInputs: [], isHighlighted: true, iconName: "", iconVariant: IconVariant.Regular);
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Resource.Source] = new(KnownProperties.Resource.Source, Value.ForString("test-source"), false, null, 0, null, false)
        };
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport, new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test", displayName: "test-item", resourceType: resourceType, state: KnownResourceState.Running,
                properties: properties, commands: [command])],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test");
        var cut = RenderOverview(s_viewport);
        var status = cut.FindComponent<OverviewStatusCard>();
        var isParameter = resourceType == KnownResourceTypes.Parameter;

        status.WaitForAssertion(() =>
        {
            var names = status.FindAll("td.nameColumn");
            Assert.Equal(isParameter ? ["Display name", "State", "Type"] : ["Display name", "State", "Health state", "Type", "Start time", "Stop time", "Source"],
                names.Select(cell => cell.TextContent.Trim()));
            Assert.Equal(isParameter ? ["test-item", "Running", resourceType] : ["test-item", "Running", "Healthy", resourceType],
                names.Take(4).Select(cell => cell.NextElementSibling!.TextContent.Trim()));
            Assert.Single(status.FindAll("td.valueColumn svg.resource-icon"));
        });

        var actions = status.FindAll(".overview-status-actions fluent-button");
        Assert.Equal(isParameter ? 1 : 2, actions.Count);
        if (!isParameter)
        {
            Assert.Equal("Refresh status", actions[0].TextContent.Trim());
        }
        Assert.Equal("Actions for test-item", actions[^1].GetAttribute("aria-label"));
        var menu = status.FindComponent<AspireMenuButton>();
        Assert.Contains(menu.Instance.ItemsProvider(), item => item.Text == "View JSON");
        Assert.Contains(menu.Instance.ItemsProvider(), item => item.Text == "Refresh status");

        if (isParameter)
        {
            Assert.Equal(["overview-status-title", "parameter-value-title"],
                cut.FindComponent<ParameterOverview>().FindAll("section").Select(section => section.GetAttribute("aria-labelledby")));
        }
    }

    [Theory]
    [InlineData("2026-06-15T09:04:05.1234567+02:00", "2026-06-15T09:04:05.1234567+02:00")]
    [InlineData(null, "2026-06-15T07:04:05.0000000Z")]
    public void Status_SourceAndStartTimeVisualizersUseRawValues(string? startProperty, string expectedStart)
    {
        var projectPath = Path.Combine("projects", "test", "Test.csproj");
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Project.Path] = new(KnownProperties.Project.Path, Value.ForString(projectPath), false, null, 0, null, false)
        };
        if (startProperty is not null)
        {
            properties[KnownProperties.Resource.StartTime] = new(KnownProperties.Resource.StartTime, Value.ForString(startProperty), false, null, 0, null, false);
        }

        var resource = ModelTestHelpers.CreateResource("test", state: KnownResourceState.Running, resourceType: KnownResourceTypes.Project,
            properties: properties, startTimeStamp: new DateTime(2026, 6, 15, 7, 4, 5, DateTimeKind.Utc));
        SetupOverview(new TestDashboardClient(isEnabled: true, initialResources: [resource],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));

        var cut = RenderOverview(s_viewport);
        var status = cut.FindComponent<OverviewStatusCard>();
        var values = status.FindComponents<GridValue>()
            .Where(cell => cell.Instance.TextVisualizerTitle is not null)
            .ToDictionary(cell => cell.Instance.TextVisualizerTitle!, cell => cell.Instance);

        Assert.Equal("Test.csproj", status.Find(".overview-status-source").TextContent);
        Assert.Equal(projectPath, values["Source"].ValueToVisualize);
        Assert.Equal(expectedStart, values["Start time"].ValueToVisualize);
        Assert.NotEqual(expectedStart, values["Start time"].Value);
    }

    [Fact]
    [UseCulture("cs-CZ")]
    public void Status_RendersStateAndHealthStateWithIdenticalLocalizedLabels()
    {
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test", state: KnownResourceState.Running)],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));

        var cut = RenderOverview(s_viewport);

        var rows = cut.FindComponent<OverviewStatusCard>().FindAll("td.nameColumn")
            .Where(cell => cell.TextContent.Trim() == "Stav");
        Assert.Collection(rows,
            state => Assert.Contains(KnownResourceState.Running.ToString(), state.NextElementSibling!.TextContent),
            health => Assert.Contains("Healthy", health.NextElementSibling!.TextContent));
    }

    [Fact]
    public async Task Status_MasksSensitiveSourceAndWarnsInVisualizer()
    {
        const string rawSource = "sample-sensitive-source";
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Resource.Source] = new(KnownProperties.Resource.Source, Value.ForString(rawSource), true, null, 0, null, false)
        };
        var resource = ModelTestHelpers.CreateResource("test", properties: properties);
        var dialogService = new TestDialogService();
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport,
            new TestDashboardClient(isEnabled: true, initialResources: [resource],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<IDialogService>(dialogService));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test");
        var status = RenderOverview(s_viewport).FindComponent<OverviewStatusCard>();
        var source = Assert.Single(status.FindComponents<GridValue>(), cell => cell.Instance.TextVisualizerTitle == "Source");
        var mask = DashboardUIHelpers.GetMaskingText(8).Text;
        var maskedValue = source.Find(".grid-value.masked");

        Assert.Equal(mask, maskedValue.TextContent.Trim());
        Assert.Equal(mask, maskedValue.GetAttribute("data-tooltip"));

        await AssertVisualizerWarnsAsync();

        await source.Find(".grid-value-mask-button").ClickAsync(new());
        Assert.Equal(rawSource, source.Find(".overview-status-source").TextContent);
        Assert.Equal(rawSource, source.Find(".grid-value").GetAttribute("data-tooltip"));
        await AssertVisualizerWarnsAsync();

        await source.Find(".grid-value-mask-button").ClickAsync(new());
        maskedValue = source.Find(".grid-value.masked");
        Assert.Equal(mask, maskedValue.TextContent.Trim());
        Assert.Equal(mask, maskedValue.GetAttribute("data-tooltip"));

        async Task AssertVisualizerWarnsAsync()
        {
            var previousDialog = dialogService.LastInstance;
            await source.Find("fluent-button[aria-label='Open in text visualizer']").ClickAsync(new());
            var instance = Assert.IsAssignableFrom<IDialogInstance>(dialogService.LastInstance);
            Assert.NotSame(previousDialog, instance);
            var dialog = Assert.IsType<TextVisualizerDialogViewModel>(instance.Options.Parameters["Content"]);
            Assert.True(dialog.ContainsSecret);
            Assert.Equal(rawSource, dialog.Text);
            await instance.CloseAsync();
        }
    }

    [Fact]
    public async Task Status_PreservesSourceRevealOnUpdateAndResetsOnSelectionChange()
    {
        var changes = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        SetupOverview(new TestDashboardClient(isEnabled: true,
            initialResources: [CreateResource("test", "initial-source"), CreateResource("other", "other-source")],
            resourceChannelProvider: () => changes));
        var cut = RenderOverview(s_viewport);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var mask = DashboardUIHelpers.GetMaskingText(8).Text;

        await cut.Find(".overview-status-card .grid-value-mask-button").ClickAsync(new());
        AssertRevealedSource("initial-source");

        await changes.Writer.WriteAsync([new(ResourceViewModelChangeType.Upsert, CreateResource("test", "updated-source"))]);
        cut.WaitForAssertion(() => AssertRevealedSource("updated-source"));

        navigation.NavigateTo("/resources/other?pane=resources#details");
        cut.Render();
        cut.WaitForAssertion(() => AssertMaskedSource("other"));

        navigation.NavigateTo("/resources/test?pane=resources#details");
        cut.Render();
        cut.WaitForAssertion(() => AssertMaskedSource("test"));

        void AssertRevealedSource(string expected)
        {
            var value = cut.Find(".overview-status-card .overview-status-source");
            Assert.Equal(expected, value.TextContent);
            Assert.Equal(expected, value.ParentElement!.GetAttribute("data-tooltip"));
        }

        void AssertMaskedSource(string resourceName)
        {
            var status = cut.FindComponent<OverviewStatusCard>();
            Assert.Equal(resourceName, status.Instance.Resource.Name);
            var value = status.Find(".grid-value.masked");
            Assert.Equal(mask, value.TextContent.Trim());
            Assert.Equal(mask, value.GetAttribute("data-tooltip"));
        }

        static ResourceViewModel CreateResource(string name, string source) =>
            ModelTestHelpers.CreateResource(name, properties: new()
            {
                [KnownProperties.Resource.Source] = new(KnownProperties.Resource.Source, Value.ForString(source), true, null, 0, null, false)
            });
    }

    [Theory]
    [InlineData(AspireDataType.StructuredLogs, "overview-logs-title")]
    [InlineData(AspireDataType.Traces, "overview-traces-title")]
    [InlineData(AspireDataType.Metrics, "overview-metrics-title")]
    public async Task HiddenTelemetryPanels_AppearWhileLoadingAndKeepReceivedSignals(AspireDataType signal, string expectedPanel)
    {
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource("test-TestId")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ITelemetryRepository>(provider =>
            {
                var inner = provider.GetRequiredService<SqliteTelemetryRepository>();
                return new TestTelemetryRepository(inner)
                {
                    GetOverviewLogsAsyncHandler = async (resourceKeys, recentCount, errorsOnly, cancellationToken) =>
                    {
                        queryStarted.TrySetResult();
                        await continueQuery.Task.WaitAsync(cancellationToken);
                        return await inner.GetOverviewLogsAsync(resourceKeys, recentCount, errorsOnly, cancellationToken);
                    }
                };
            }));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test-TestId");
        var repository = Services.GetRequiredService<SqliteTelemetryRepository>();
        var cut = RenderOverview(s_viewport, "test-TestId");
        cut.Find(".overview-panel-selector [aria-label='Hide empty panels']").Click();
        string[] telemetryPanels = ["overview-metrics-title", "overview-logs-title", "overview-traces-title"];
        Assert.All(telemetryPanels, title => Assert.True(cut.Find($"[aria-labelledby='{title}']").HasAttribute("hidden")));

        try
        {
            await TelemetryHelpSetupHelpers.AddSignalAsync(repository, signal);
            await queryStarted.Task.WaitAsync(DefaultWaitTimeout);
            cut.WaitForAssertion(() =>
            {
                Assert.All(telemetryPanels, title =>
                {
                    var panel = cut.Find($"[aria-labelledby='{title}']");
                    Assert.False(panel.HasAttribute("hidden"));
                    Assert.NotEmpty(panel.QuerySelectorAll(".overview-stats"));
                });
                Assert.NotEmpty(cut.FindComponent<ResourceOverview>().FindComponents<FluentSkeleton>());
            });
        }
        finally
        {
            continueQuery.TrySetResult();
        }

        cut.WaitForAssertion(() =>
        {
            Assert.All(telemetryPanels, title =>
            {
                var panel = cut.Find($"[aria-labelledby='{title}']");
                Assert.Equal(title != expectedPanel, panel.HasAttribute("hidden"));
            });
            Assert.Empty(cut.FindComponent<ResourceOverview>().FindComponents<FluentSkeleton>());
        });
        Assert.Equal("true", cut.Find(".overview-panel-selector [aria-label='Hide empty panels']").GetAttribute("aria-pressed"));
        Assert.Equal(["test-TestId"], cut.Instance.SelectedResourceNames);
    }

    [Fact]
    public async Task TelemetryRefresh_CoalescesNotificationsWhileFirstReadIsRunning()
    {
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveDelayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLiveDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Func<Task>? notifyLogs = null;
        ComponentFactories.Add(() =>
        {
            var overview = Activator.CreateInstance<ResourceOverview>();
            overview.TelemetryRefreshDelayAsync = (_, token) =>
            {
                liveDelayStarted.TrySetResult();
                return releaseLiveDelay.Task.WaitAsync(token);
            };
            return overview;
        });
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport,
            new TestDashboardClient(isEnabled: true,
                initialResources: [ModelTestHelpers.CreateResource("test-TestId")],
                resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>),
            configureServices: services => services.AddSingleton<ITelemetryRepository>(provider =>
                new TestTelemetryRepository(provider.GetRequiredService<SqliteTelemetryRepository>())
                {
                    OnOverviewLogsRegistered = callback => notifyLogs = callback,
                    OnNewResourcesSubscribed = subscription => subscription.Dispose(),
                    GetOverviewLogsAsyncHandler = async (_, _, _, token) =>
                    {
                        var count = Interlocked.Increment(ref reads);
                        if (count == 1)
                        {
                            firstReadStarted.TrySetResult();
                            await releaseFirstRead.Task.WaitAsync(token);
                        }
                        return new OverviewLogs(count, 0, 0, []);
                    }
                }));
        await TelemetryHelpSetupHelpers.AddSignalAsync(Services.GetRequiredService<SqliteTelemetryRepository>(), AspireDataType.StructuredLogs);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test-TestId");
        var cut = RenderOverview(s_viewport, "test-TestId");

        try
        {
            await firstReadStarted.Task.WaitAsync(DefaultWaitTimeout);
            Assert.NotNull(notifyLogs);
            await notifyLogs();
            await notifyLogs();
            await notifyLogs();

            // A live refresh must not even start its delay while the first read is held.
            await Assert.ThrowsAsync<TimeoutException>(() => liveDelayStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
            releaseFirstRead.TrySetResult();
            await liveDelayStarted.Task.WaitAsync(DefaultWaitTimeout);
            await cut.WaitForAssertionAsync(() => Assert.Equal("1", GetLogCount()));

            await notifyLogs();
            releaseLiveDelay.TrySetResult();
            await cut.WaitForAssertionAsync(() => Assert.Equal("2", GetLogCount()));
            Assert.Equal(2, Volatile.Read(ref reads));
        }
        finally
        {
            releaseFirstRead.TrySetResult();
            releaseLiveDelay.TrySetResult();
        }

        string GetLogCount() => cut.Find("[aria-labelledby='overview-logs-title'] .overview-stat-value").TextContent;
    }

    private void SetupOverview(TestDashboardClient client)
    {
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test?pane=resources#details");
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(ViewportInformation viewport, string? resourceName = "test") =>
        Render<ResourcesLayout>(p => p.AddCascadingValue(viewport).Add(p => p.Body, builder =>
        {
            builder.OpenComponent<ResourceOverview>(0);
            builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), resourceName);
            builder.CloseComponent();
        }));

    private static string[] GetVisiblePanels(IRenderedComponent<ResourcesLayout> cut) =>
        cut.FindAll(".overview-grid > .overview-card:not([hidden])").Select(c => c.GetAttribute("aria-labelledby")!).ToArray();

    private static Task SelectPanelsAsync(IRenderedComponent<ResourcesLayout> cut, bool hideEmpty) =>
        cut.InvokeAsync(() => cut.FindComponent<IconToggleGroup<bool>>()
            .Find(hideEmpty ? "[aria-label='Hide empty panels']" : "[aria-label='Show all panels']").ClickAsync(new()));
}
