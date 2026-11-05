// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Playwright;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePanelNavigationTests(ResourcePanelNavigationTests.NavigationResourcesFixture fixture)
    : PlaywrightTestsBase<ResourcePanelNavigationTests.NavigationResourcesFixture>(fixture)
{
    [Theory]
    [InlineData(320, 740)]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(769, 1024)]
    [InlineData(1024, 768)]
    [InlineData(1440, 900)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceNavigation_CloseOrDropdownReturnsToListAndSelectAllReopensContent(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/tag-a");
            var pane = page.Locator(".resource-pane");
            var content = page.Locator(".resource-content");
            var close = page.Locator(".detail-page-close");
            var picker = page.Locator(".resource-picker");
            var row = pane.Locator("[data-resource-name='tag-b'] .resource-row-link");

            if (width <= 768)
            {
                await Assertions.Expect(close).ToHaveCountAsync(0);
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await picker.ClickAsync();
                await Assertions.Expect(row).ToBeVisibleAsync();
                await Assertions.Expect(content).Not.ToBeVisibleAsync();
                await Assertions.Expect(content).ToHaveAttributeAsync("inert", "");
                await pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
                await Assertions.Expect(picker).ToBeVisibleAsync();
                await Assertions.Expect(content).ToBeVisibleAsync();
                await picker.ClickAsync();
            }
            else
            {
                await Assertions.Expect(close).ToBeVisibleAsync();
                await Assertions.Expect(picker).ToHaveCountAsync(0);
                await close.ClickAsync();
                await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
                await Assertions.Expect(content).Not.ToBeVisibleAsync();
            }

            await Assertions.Expect(page.Locator(".resource-pane-canvas-toggle")).ToHaveCountAsync(0);
            await row.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources/tag-b$"));
            await Assertions.Expect(content).ToBeVisibleAsync();
            await Assertions.Expect(content).Not.ToHaveAttributeAsync("inert", "");

            if (width <= 768)
            {
                await picker.ClickAsync();
            }
            else
            {
                await close.ClickAsync();
            }
            await pane.Locator(".resource-select-all-toggle").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources$"));
            await Assertions.Expect(content).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas,.drawer-open")).ToHaveCountAsync(0);
            await Assertions.Expect(close).ToHaveCountAsync(width <= 768 ? 0 : 1);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData(320, 740)]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceDrawer_ScrollsOnlyListAndKeepsFiltersAndFooterAvailable(int width, int height)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, height);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await page.GotoAsync("/resources/tag-a");
            await page.Locator(".resource-picker").ClickAsync();
            var list = page.Locator(".resource-pane-list");
            await Assertions.Expect(list.Locator("[data-resource-name='tag-a']").First).ToBeVisibleAsync();
            var header = page.Locator(".resource-pane-head");
            var footer = page.Locator(".resource-pane-foot").First;
            var headerBefore = await header.EvaluateAsync<double>("header => header.getBoundingClientRect().top - header.closest('.resource-pane').getBoundingClientRect().top");
            var footerBefore = await footer.EvaluateAsync<double>("footer => footer.closest('.resource-pane').getBoundingClientRect().bottom - footer.getBoundingClientRect().bottom");
            Assert.True(await list.EvaluateAsync<bool>("list => list.scrollHeight > list.clientHeight"));
            await list.EvaluateAsync("list => list.scrollTop = list.scrollHeight");
            Assert.True(await list.EvaluateAsync<bool>("list => list.scrollTop > 0"));
            Assert.Equal(headerBefore, await header.EvaluateAsync<double>("header => header.getBoundingClientRect().top - header.closest('.resource-pane').getBoundingClientRect().top"));
            Assert.Equal(footerBefore, await footer.EvaluateAsync<double>("footer => footer.closest('.resource-pane').getBoundingClientRect().bottom - footer.getBoundingClientRect().bottom"));
            await Assertions.Expect(header.GetByRole(AriaRole.Searchbox)).ToBeVisibleAsync();
            await Assertions.Expect(footer).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".resource-content")).Not.ToBeVisibleAsync();
            var overflowing = await page.Locator(".resource-pane").EvaluateAsync<bool>("pane => pane.scrollWidth > pane.clientWidth");
            Assert.False(overflowing);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("/traces?type=server")]
    [InlineData("/structuredlogs?logLevel=error")]
    [InlineData("/metrics?duration=30")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectAll_ReopensFilteredContentWithoutChangingUrl(string url)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync(url);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToBeVisibleAsync();
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(url) + "$"));
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("/traces/detail/missing?resource=tag-a&type=server")]
    [InlineData("/metrics/resource/tag-a/meter/Test")]
    [InlineData("/metrics/resource/tag-a?meter=Test&instrument=Requests&duration=30")]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task RetainedResource_ReopensParentFromFullCanvas(string url)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync(url);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            var parentUrl = page.Url;
            await page.Locator(".resource-pane [data-resource-name='tag-a'] .resource-row-link").ClickAsync();
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas")).ToHaveCountAsync(0);
            await Assertions.Expect(page).ToHaveURLAsync(parentUrl);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData("/traces/detail/missing", "/traces", 390)]
    [InlineData("/traces/detail/missing", "/traces", 1440)]
    [InlineData("/metrics?meter=Test&duration=30", "/metrics", 390)]
    [InlineData("/metrics?meter=Test&duration=30", "/metrics", 1440)]
    [InlineData("/metrics?meter=Test&instrument=Requests&duration=30", "/metrics", 390)]
    [InlineData("/metrics?meter=Test&instrument=Requests&duration=30", "/metrics", 1440)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectAll_FromUnscopedDetailNavigatesToTab(string url, string expectedDestination, int width)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 844);
            await page.GotoAsync(url);
            await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }

            await page.Locator(".resource-select-all-toggle").ClickAsync();

            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(expectedDestination) + "$"));
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".pane-full-canvas,.drawer-open")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData(AspireDataType.StructuredLogs)]
    [InlineData(AspireDataType.Traces)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ExistingTelemetry_RemainsVisibleWhenAllResourcesViewReloads(AspireDataType signal)
    {
        var writer = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        await writer.ClearSelectedSignalsAsync(new() { ["tag-a"] = [signal] });
        var resource = CreateResource("tag-a");
        resource.Attributes.Remove(resource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
        var now = DateTime.UtcNow;
        var (path, message) = signal switch
        {
            AspireDataType.StructuredLogs => ("structuredlogs", "Existing sidebar log"),
            AspireDataType.Traces => ("traces", "Test span. Id: preloaded-span"),
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };
        await (signal switch
        {
            AspireDataType.StructuredLogs => writer.AddLogsAsync(new(),
            [
                new ResourceLogs
                {
                    Resource = resource,
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: message) } } }
                }
            ]),
            AspireDataType.Traces => writer.AddTracesAsync(new(),
            [
                new ResourceSpans
                {
                    Resource = resource,
                    ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans = { CreateSpan("preloaded-trace", "preloaded-span", now, now.AddMilliseconds(10)) } } }
                }
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        });
        try
        {
            await RunTestAsync(async page =>
            {
                await page.GotoAsync($"/{path}");
                await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = message })).ToBeVisibleAsync();
                await page.ReloadAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = message })).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".resource-select-all-toggle.on")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator(".resource-row.selected")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
            });
        }
        finally
        {
            await writer.ClearSelectedSignalsAsync(new() { ["tag-a"] = [signal] });
        }
    }

    [Theory]
    [InlineData(390, AspireDataType.StructuredLogs)]
    [InlineData(768, AspireDataType.StructuredLogs)]
    [InlineData(1440, AspireDataType.StructuredLogs)]
    [InlineData(390, AspireDataType.Traces)]
    [InlineData(768, AspireDataType.Traces)]
    [InlineData(1440, AspireDataType.Traces)]
    [InlineData(390, AspireDataType.Metrics)]
    [InlineData(768, AspireDataType.Metrics)]
    [InlineData(1440, AspireDataType.Metrics)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task UnavailableRows_RemainInteractiveAndUpdateWhenSignalsArriveAndClear(int width, AspireDataType signal)
    {
        var writer = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        var (path, label, reason) = signal switch
        {
            AspireDataType.StructuredLogs => ("structuredlogs", Resources.Layout.ResourcePaneNoStructuredLogsLabel, Resources.Layout.ResourcePaneNoStructuredLogs),
            AspireDataType.Traces => ("traces", Resources.Layout.ResourcePaneNoTracesLabel, Resources.Layout.ResourcePaneNoTraces),
            AspireDataType.Metrics => ("metrics", Resources.Layout.ResourcePaneNoMetricsLabel, Resources.Layout.ResourcePaneNoMetrics),
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };
        await writer.ClearSelectedSignalsAsync(new() { ["tag-a"] = [signal] });
        if (signal == AspireDataType.StructuredLogs)
        {
            var otherResource = CreateResource("tag-b");
            otherResource.Attributes.Remove(otherResource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
            await writer.AddLogsAsync(new(),
            [
                new ResourceLogs
                {
                    Resource = otherResource,
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: "Another resource's log") } } }
                }
            ]);
        }
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 844);
            await page.GotoAsync($"/{path}");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            var row = page.Locator("[data-resource-name='tag-a']");
            var link = row.Locator(".resource-row-link");
            await Assertions.Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("resource-row-unavailable"));
            await Assertions.Expect(row.Locator(".resource-row-availability")).ToHaveTextAsync(label);
            await Assertions.Expect(link).ToHaveAttributeAsync("data-tooltip", reason);
            await Assertions.Expect(link).ToBeEnabledAsync();
            Assert.Null(await link.GetAttributeAsync("aria-disabled"));

            var tooltip = page.Locator("aspire-tooltip-provider fluent-tooltip[aria-hidden='false']");
            await link.Locator(".resource-row-name").HoverAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(reason);
            await page.Mouse.MoveAsync(1, 1);
            await page.Keyboard.PressAsync("Escape");
            await page.Keyboard.PressAsync("Tab");
            await link.FocusAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(reason);
            await link.PressAsync("Enter");
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/{path}/resource/tag-a$"));
            await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".telemetry-empty-content")).ToBeVisibleAsync();
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await link.Locator(".resource-row-name").HoverAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();

            var resource = CreateResource("tag-a");
            resource.Attributes.Remove(resource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
            var now = DateTime.UtcNow;
            await (signal switch
            {
                AspireDataType.StructuredLogs => writer.AddLogsAsync(new(),
                [
                    new ResourceLogs
                    {
                        Resource = resource,
                        ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: "Sidebar availability test") } } }
                    }
                ]),
                AspireDataType.Traces => writer.AddTracesAsync(new(),
                [
                    new ResourceSpans
                    {
                        Resource = resource,
                        ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans = { CreateSpan("availability-trace", "availability-span", now, now.AddMilliseconds(10)) } } }
                    }
                ]),
                AspireDataType.Metrics => writer.AddMetricsAsync(new(),
                [
                    new ResourceMetrics
                    {
                        Resource = resource,
                        ScopeMetrics = { new ScopeMetrics { Scope = CreateScope(), Metrics = { CreateSumMetric("availability.requests", now) } } }
                    }
                ]),
                _ => throw new ArgumentOutOfRangeException(nameof(signal))
            });
            try
            {
                await Assertions.Expect(row).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("resource-row-unavailable"));
                await Assertions.Expect(row.Locator(".resource-row-availability")).ToHaveCountAsync(0);
                await Assertions.Expect(link).Not.ToHaveAttributeAsync("data-tooltip", reason);
                await Assertions.Expect(tooltip).ToHaveCountAsync(0);
                await Assertions.Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("selected"));
                await Assertions.Expect(link).ToHaveAttributeAsync("aria-current", "page");
                await Assertions.Expect(page.Locator(".resource-row.selected")).ToHaveCountAsync(1);
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/{path}/resource/tag-a$"));
                await Assertions.Expect(page.Locator(".telemetry-empty-content")).Not.ToBeVisibleAsync();
                if (width > 768 && signal == AspireDataType.StructuredLogs)
                {
                    await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "Sidebar availability test", Exact = false })).ToBeVisibleAsync();
                    await Assertions.Expect(page.GetByText("Another resource's log", new() { Exact = true })).ToHaveCountAsync(0);
                }
            }
            finally
            {
                await writer.ClearSelectedSignalsAsync(new() { ["tag-a"] = [signal], ["tag-b"] = [signal] });
            }
            await Assertions.Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("resource-row-unavailable"));
            await Assertions.Expect(row.Locator(".resource-row-availability")).ToHaveTextAsync(label);
            await Assertions.Expect(link).ToHaveAttributeAsync("data-tooltip", reason);
            await page.Mouse.MoveAsync(1, 1);
            await link.Locator(".resource-row-name").HoverAsync();
            await Assertions.Expect(tooltip).ToBeVisibleAsync();
            await Assertions.Expect(tooltip).ToHaveTextAsync(reason);
            await Assertions.Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("selected"));
            await Assertions.Expect(link).ToHaveAttributeAsync("aria-current", "page");
            await Assertions.Expect(page.Locator(".resource-row.selected")).ToHaveCountAsync(1);
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/{path}/resource/tag-a$"));
            if (width <= 768)
            {
                await page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
            }
            await Assertions.Expect(page.Locator(".telemetry-empty-content")).ToBeVisibleAsync();
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            await page.Locator("[data-resource-name='tag-b'] .resource-row-link").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/{path}/resource/tag-b$"));
            await Assertions.Expect(page.Locator(".telemetry-empty-content")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        });
    }

    [Theory]
    [InlineData(390)]
    [InlineData(768)]
    [InlineData(1440)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ParameterRows_ExplainEachUnavailableFeatureAndKeyboardSelectionOpensOverview(int width)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 844);
            foreach (var (path, reason) in new[]
            {
                ("consolelogs", Resources.Layout.ResourcePaneParameterNoConsoleLogs),
                ("structuredlogs", Resources.Layout.ResourcePaneParameterNoStructuredLogs),
                ("traces", Resources.Layout.ResourcePaneParameterNoTraces),
                ("metrics", Resources.Layout.ResourcePaneParameterNoMetrics),
                ("terminals", Resources.Layout.ResourcePaneParameterNoTerminal)
            })
            {
                await page.GotoAsync($"/{path}");
                await Assertions.Expect(page.Locator(".resources-layout")).ToHaveAttributeAsync("data-pane-interactions-ready", "true");
                if (width <= 768)
                {
                    await page.Locator(".resource-picker").ClickAsync();
                }
                var row = page.Locator(".resource-row[data-resource-name='api-key']");
                var link = row.Locator(".resource-row-link");
                await Assertions.Expect(row.Locator(".resource-row-availability")).ToHaveTextAsync(Resources.Layout.ResourcePaneNotApplicableLabel);
                await Assertions.Expect(link).ToHaveAttributeAsync("href", "/resources/api-key");
                await Assertions.Expect(link).ToHaveAttributeAsync("aria-description", new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(reason)));
                await Assertions.Expect(link).ToBeEnabledAsync();
                Assert.Null(await link.GetAttributeAsync("aria-disabled"));
                await link.ScrollIntoViewIfNeededAsync();
                await page.Mouse.MoveAsync(1, 1);
                await page.Keyboard.PressAsync("Tab");
                await link.FocusAsync();
                var tooltip = page.Locator("aspire-tooltip-provider fluent-tooltip[aria-hidden='false']");
                await Assertions.Expect(tooltip).ToBeVisibleAsync();
                await Assertions.Expect(tooltip).ToHaveTextAsync(reason);
                await link.PressAsync("Enter");
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/resources/api-key$"));
                await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("api-key");
                await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
                if (width <= 768)
                {
                    await page.Locator(".resource-picker").ClickAsync();
                }
                await Assertions.Expect(row).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("resource-row-unavailable"));
                await Assertions.Expect(row.Locator(".resource-row-availability")).ToHaveCountAsync(0);
                await Assertions.Expect(link).ToHaveAttributeAsync("aria-current", "page");
                await Assertions.Expect(page.Locator(".resource-row.selected")).ToHaveCountAsync(1);
                await Assertions.Expect(page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
            }
        });
    }

    [Theory]
    [InlineData(390)]
    [InlineData(768)]
    [InlineData(1440)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task AvailabilityBadges_FollowTabChangesWithoutChangingSelection(int width)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(width, 844);
            await page.GotoAsync("/structuredlogs/resource/tag-a");
            if (width <= 768)
            {
                await page.Locator(".resource-picker").ClickAsync();
            }
            var row = page.Locator("[data-resource-name='tag-a']");
            var badge = row.Locator(".resource-row-availability");
            await Assertions.Expect(badge).ToHaveTextAsync(Resources.Layout.ResourcePaneNoStructuredLogsLabel);

            foreach (var (tabId, path, label) in new (string, string, string?)[]
            {
                ("Traces", "traces", Resources.Layout.ResourcePaneNoTracesLabel),
                ("Metrics", "metrics", Resources.Layout.ResourcePaneNoMetricsLabel),
                ("Console", "consolelogs", null),
                ("StructuredLogs", "structuredlogs", Resources.Layout.ResourcePaneNoStructuredLogsLabel)
            })
            {
                if (width <= 768)
                {
                    await page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneClose, Exact = true }).ClickAsync();
                    await Assertions.Expect(page.Locator(".resource-content")).ToBeVisibleAsync();
                }
                var navigation = page.Locator(".resource-tabs-nav");
                var tab = navigation.Locator($"#tab-{tabId}");
                if (await tab.IsVisibleAsync())
                {
                    await tab.ClickAsync();
                }
                else
                {
                    var compact = navigation.Locator(".resource-tabs-compact-button");
                    await (await compact.IsVisibleAsync() ? compact : navigation.Locator(".resource-tabs-overflow-button")).ClickAsync();
                    await page.Locator($"#tab-{tabId}-menu-item").ClickAsync();
                }
                await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/{path}/resource/tag-a$"));
                if (width <= 768)
                {
                    await page.Locator(".resource-picker").ClickAsync();
                }
                if (label is not null)
                {
                    await Assertions.Expect(badge).ToBeVisibleAsync();
                    await Assertions.Expect(badge).ToHaveTextAsync(label);
                }
                else
                {
                    await Assertions.Expect(badge).ToHaveCountAsync(0);
                }
                await Assertions.Expect(row.Locator(".resource-row-link")).ToHaveAttributeAsync("aria-current", "page");
            }
        });
    }

    public sealed class NavigationResourcesFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel>? Resources =>
        [
            ModelTestHelpers.CreateResource("tag-a", state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource("tag-b", state: KnownResourceState.Running),
            ModelTestHelpers.CreateResource("tag-c", state: KnownResourceState.Running,
                properties: new Dictionary<string, ResourcePropertyViewModel>
                {
                    [KnownProperties.Terminal.Enabled] = new(
                        KnownProperties.Terminal.Enabled,
                        new Value { StringValue = "true" },
                        isValueSensitive: false,
                        knownProperty: null,
                        sortOrder: 0,
                        displayName: null,
                        isHighlighted: false)
                }),
            ModelTestHelpers.CreateResource("api-key", resourceType: KnownResourceTypes.Parameter),
            .. Enumerable.Range(1, 40).Select(i => ModelTestHelpers.CreateResource($"worker-{i:D2}", state: KnownResourceState.Running))
        ];
    }
}
