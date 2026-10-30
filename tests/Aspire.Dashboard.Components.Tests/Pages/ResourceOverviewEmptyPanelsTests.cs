// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewEmptyPanelsTests : DashboardTestContext
{
    private static readonly ViewportInformation s_viewport = new(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

    [Fact]
    public async Task ConsolePause_FreezesTwelveRowsIncludingQueuedHistoricalBatchesAndResumes()
    {
        var console = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var processed = Channel.CreateUnbounded<int>();
        var replayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestDashboardClient(isEnabled: true,
            initialResources: [ModelTestHelpers.CreateResource("test")],
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>,
            consoleLogsChannelProvider: _ => console)
        {
            BeforeConsoleLogsBatchAsync = async batch =>
            {
                if (batch[^1].LineNumber == 40)
                {
                    replayStarted.SetResult();
                    await releaseReplay.Task;
                }
            },
            OnConsoleLogsBatchProcessed = batch => processed.Writer.TryWrite(batch[^1].LineNumber)
        };
        SetupOverview(client);
        var cut = RenderOverview(s_viewport);
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
        await console.Writer.WriteAsync(Enumerable.Range(1, 20)
            .Select(number => new ResourceLogLine(number, $"{timestamp.AddSeconds(number):O} line-{number}", false)).ToArray());
        Assert.Equal(20, await processed.Reader.ReadAsync().AsTask().WaitAsync(DefaultWaitTimeout));
        cut.WaitForAssertion(() => Assert.Equal(12, cut.FindAll(".overview-console-line").Count));

        await console.Writer.WriteAsync(Enumerable.Range(21, 20)
            .Select(number => new ResourceLogLine(number, $"{timestamp.AddSeconds(number):O} line-{number}", false)).ToArray());
        await replayStarted.Task.WaitAsync(DefaultWaitTimeout);
        try
        {
            await cut.InvokeAsync(() => cut.Find("[aria-label='Pause incoming data']").Click());
            Assert.Equal(Enumerable.Range(9, 12).Select(number => $"line-{number}"), ReadRows());
        }
        finally
        {
            releaseReplay.TrySetResult();
        }
        Assert.Equal(40, await processed.Reader.ReadAsync().AsTask().WaitAsync(DefaultWaitTimeout));
        await cut.InvokeAsync(() => cut.Render());
        Assert.Equal(Enumerable.Range(9, 12).Select(number => $"line-{number}"), ReadRows());
        Assert.Contains("paused", cut.Find(".overview-console-paused").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cut.Find("[aria-labelledby='overview-console-title']"), cut.Find(".overview-console-paused").ParentElement);

        await cut.InvokeAsync(() => cut.Find("[aria-label='Resume incoming data']").Click());
        await console.Writer.WriteAsync(Enumerable.Range(41, 20)
            .Select(number => new ResourceLogLine(number, $"line-{number}", false)).ToArray());
        Assert.Equal(60, await processed.Reader.ReadAsync().AsTask().WaitAsync(DefaultWaitTimeout));
        cut.WaitForAssertion(() => Assert.Equal(Enumerable.Range(49, 12).Select(number => $"line-{number}"), ReadRows()));
        Assert.Empty(cut.FindAll(".overview-console-paused"));

        IEnumerable<string> ReadRows() => cut.FindAll(".overview-console-line > span:last-child").Select(line => line.TextContent);
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
        var tabs = cut.FindAll("[role='tab']").Select(t => t.TextContent).ToArray();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var initialUrl = navigation.Uri;
        var selector = cut.Find(".resource-header-row .overview-panel-selector [role='group']");
        Assert.Equal("Panels", selector.GetAttribute("aria-label"));
        Assert.Equal(["Hide empty panels", "Show all panels"], selector.QuerySelectorAll("button").Select(b => b.GetAttribute("aria-label")));
        var all = cut.Find(".overview-panel-selector [aria-label='Show all panels']");
        var withContent = cut.Find(".overview-panel-selector [aria-label='Hide empty panels']");
        Assert.Equal("false", all.GetAttribute("aria-pressed"));
        Assert.Equal("true", withContent.GetAttribute("aria-pressed"));

        await SelectPanelsAsync(cut, hideEmpty: false);
        var titles = GetVisiblePanels(cut);
        Assert.Equal(12, titles.Length);

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
        var selector = cut.Find(".resource-header-row .overview-panel-selector [role='group']");
        var options = cut.FindAll(".overview-panel-selector button");
        Assert.Equal("Panels", selector.TextContent.Trim());
        Assert.Equal(["Hide empty panels", "Show all panels"], options.Select(option => option.GetAttribute("aria-label")));
        Assert.All(options, option =>
        {
            Assert.Equal(option.GetAttribute("aria-label"), option.GetAttribute("data-tooltip"));
            Assert.Equal("before", option.GetAttribute("data-tooltip-positioning"));
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
    public async Task ConsolePreview_KeepsTwelveNewestLinesAcrossUpdatesAndPanelChoices()
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
            Enumerable.Range(5, 12).Select(number => $"line-{number}"),
            cut.FindAll(".overview-console-line").Select(line => line.TextContent)));

        await SelectPanelsAsync(cut, hideEmpty: true);
        await console.Writer.WriteAsync(Enumerable.Range(17, 4)
            .Select(number => new ResourceLogLine(number, $"line-{number}", false)).ToArray());
        cut.WaitForAssertion(() => Assert.Equal(
            Enumerable.Range(9, 12).Select(number => $"line-{number}"),
            cut.FindAll(".overview-console-line").Select(line => line.TextContent)));

        await SelectPanelsAsync(cut, hideEmpty: false);
        Assert.Equal(
            Enumerable.Range(9, 12).Select(number => $"line-{number}"),
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
    public void FilteredPropertiesAndEnvironment_RetainTheirControls()
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
        cut.Find(".overview-panel-selector [aria-label='Hide empty panels']").Click();

        var properties = cut.Find("[aria-labelledby='overview-properties-title']");
        var environment = cut.Find("[aria-labelledby='overview-environment-title']");
        Assert.False(properties.HasAttribute("hidden"));
        Assert.False(environment.HasAttribute("hidden"));
        Assert.NotEmpty(properties.QuerySelectorAll(".overview-card-actions fluent-button"));
        Assert.NotEmpty(environment.QuerySelectorAll(".overview-card-actions fluent-button"));
        properties.QuerySelector(".overview-card-actions fluent-button")!.Click();
        Assert.Contains("custom-value", cut.Find("[aria-labelledby='overview-properties-title']").TextContent);
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
                    GetLogSummariesAsyncHandler = async (context, cancellationToken) =>
                    {
                        queryStarted.TrySetResult();
                        await continueQuery.Task.WaitAsync(cancellationToken);
                        return await inner.GetLogSummariesAsync(context, cancellationToken);
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

    private void SetupOverview(TestDashboardClient client)
    {
        ResourceSetupHelpers.SetupResourceOverview(this, s_viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources/test?pane=resources#details");
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(ViewportInformation viewport, string resourceName = "test") =>
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
