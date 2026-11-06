// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenTelemetry.Proto.Logs.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public sealed class ResourcePaneInteractionTests : PlaywrightTestsBase<ResourcesTests.ResourcesDashboardServerFixture>
{
    public ResourcePaneInteractionTests(ResourcesTests.ResourcesDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task TelemetryOnlyResourceSelection_KeepsCurrentTabAndSupportsConsecutiveChanges()
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            CreateResourceLogs("resource-a"),
            CreateResourceLogs("resource-b")
        });

        await RunTestAsync(async page =>
        {
            await page.GotoAsync(DashboardUrls.TelemetrySourcesUrl());

            var pane = page.Locator(".resource-pane");
            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "resource-a", Exact = true })).ToBeVisibleAsync();
            var telemetryToggle = pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.NavMenuTelemetrySourcesTab, Exact = true });
            if (await telemetryToggle.GetAttributeAsync("aria-pressed") != "true")
            {
                await telemetryToggle.ClickAsync();
            }
            var telemetrySection = pane.Locator("button.resource-pane-section-header", new() { HasTextRegex = new Regex("^Telemetry sources") });
            await Assertions.Expect(telemetrySection.First).ToBeVisibleAsync();
            if (await telemetrySection.First.GetAttributeAsync("aria-expanded") == "false")
            {
                await telemetrySection.First.ClickAsync();
            }
            await Assertions.Expect(pane.GetByText("resource-a", new() { Exact = true }).Last).ToBeVisibleAsync();

            await SelectResourceAsync("resource-a");
            await SelectResourceAsync("resource-b");

            async Task SelectResourceAsync(string resourceName)
            {
                var link = pane.GetByText(resourceName, new() { Exact = true }).Last.Locator("xpath=ancestor::a");
                await link.ClickAsync();

                await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/structuredlogs/resource/{resourceName}$"));
                await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(resourceName);
                await Assertions.Expect(link).ToHaveAttributeAsync("aria-current", "page");
            }
        });

    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task AllResourcesRow_ClearsSelectionAndSummarizesAllResources()
    {
        const string resourceName = "TestResource";

        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync($"/resources/{resourceName}");

            var pane = page.Locator(".resource-pane");
            var title = page.Locator("h1.resource-title");
            var selectAll = pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneSelectAll, Exact = true });
            await Assertions.Expect(title).ToHaveTextAsync(resourceName);
            await Assertions.Expect(selectAll).ToHaveTextAsync(Resources.Layout.ResourcePaneSelectAll);
            Assert.Null(await selectAll.GetAttributeAsync("aria-pressed"));

            await selectAll.ClickAsync();
            await Assertions.Expect(title).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
            await Assertions.Expect(selectAll).ToHaveTextAsync(Resources.Layout.ResourcePaneSelectAll);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("data-tooltip", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(pane.Locator("a[aria-current]")).ToHaveCountAsync(0);

            var overviewHeadline = page.Locator(".home-hero-headline");
            await Assertions.Expect(overviewHeadline).ToHaveTextAsync("All 3 resources are running");

            // Switching tabs keeps all resources selected, including when returning to the overview.
            await ClickResourceTabAndWaitForRenderAsync(page, () => page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = Resources.Layout.NavMenuConsoleLogsTab }).ClickAsync());
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/consolelogs$"));
            await Assertions.Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = Resources.Layout.NavMenuConsoleLogsTab })).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(title).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);

            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = Resources.Layout.ResourceTabOverview }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
            await Assertions.Expect(overviewHeadline).ToHaveTextAsync("All 3 resources are running");
            await Assertions.Expect(selectAll).ToHaveTextAsync(Resources.Layout.ResourcePaneSelectAll);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("data-tooltip", Resources.Layout.ResourcePaneAllSelected);

            // The cleared selection is remembered when entering the resources view again.
            await page.GotoAsync("/resources");
            await Assertions.Expect(overviewHeadline).ToHaveTextAsync("All 3 resources are running");
            await Assertions.Expect(title).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
        });
    }

    [Theory]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    [InlineData(640, false)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SelectAll_VisibleLabelAndKeyboardActivationRestoreAllResources(int viewportWidth, bool minimumPaneWidth)
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(viewportWidth, 900);
            await page.GotoAsync("/resources/TestResource");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("TestResource");
            if (viewportWidth < 769)
            {
                await page.Locator(".resource-pane-open-button").ClickAsync();
            }

            if (minimumPaneWidth)
            {
                var resizer = page.GetByRole(AriaRole.Separator, new() { Name = Resources.Layout.ResourcePaneResize });
                await resizer.FocusAsync();
                await page.Keyboard.PressAsync("Home");
                await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", "420");
            }

            var selectAll = page.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.ResourcePaneSelectAll, Exact = true });
            await Assertions.Expect(selectAll).ToHaveTextAsync(Resources.Layout.ResourcePaneSelectAll);
            await Assertions.Expect(selectAll).ToBeInViewportAsync(new() { Ratio = 1 });
            var paneBounds = await page.Locator(".resource-pane").BoundingBoxAsync();
            var actionBounds = await selectAll.BoundingBoxAsync();
            Assert.NotNull(paneBounds);
            Assert.NotNull(actionBounds);
            Assert.InRange(actionBounds.X, paneBounds.X, paneBounds.X + paneBounds.Width);
            Assert.InRange(actionBounds.X + actionBounds.Width, paneBounds.X, paneBounds.X + paneBounds.Width);
            Assert.Null(await selectAll.GetAttributeAsync("aria-pressed"));
            await selectAll.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
            if (viewportWidth < 769)
            {
                await Assertions.Expect(page.Locator(".resource-pane")).ToBeHiddenAsync();
                await page.Locator(".resource-pane-open-button").ClickAsync();
            }
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("data-tooltip", Resources.Layout.ResourcePaneAllSelected);

            var resourceRow = page.Locator(".resource-pane").GetByRole(AriaRole.Link, new() { Name = "TestResource", Exact = true });
            await resourceRow.ClickAsync();
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync("TestResource");
            if (viewportWidth < 769)
            {
                await Assertions.Expect(page.Locator(".resource-pane")).ToBeHiddenAsync();
                await page.Locator(".resource-pane-open-button").ClickAsync();
            }

            await selectAll.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(page.Locator("h1.resource-title")).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
            await Assertions.Expect(page.Locator(".resource-pane a[aria-current]")).ToHaveCountAsync(0);
            if (viewportWidth < 769)
            {
                await Assertions.Expect(page.Locator(".resource-pane")).ToBeHiddenAsync();
                await page.Locator(".resource-pane-open-button").ClickAsync();
            }
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-description", Resources.Layout.ResourcePaneAllSelected);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("data-tooltip", Resources.Layout.ResourcePaneAllSelected);
        });
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("macOS", "MacIntel", 0)]
    [InlineData(null, "MacIntel", 5)]
    [InlineData(null, "iPad", 5)]
    [InlineData("Windows", "MacIntel", 0)]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceRowModifierClicks_SelectSeveralResources(string? userAgentPlatform, string? platform, int touchPoints)
    {
        var repository = DashboardServerFixture.DashboardApp.Services.GetRequiredService<ITelemetryRepositoryWriter>();
        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            CreateResourceLogs("multi-a"),
            CreateResourceLogs("multi-b"),
            CreateResourceLogs("multi-c")
        });

        await RunTestAsync(async page =>
        {
            if (platform is not null)
            {
                await page.AddInitScriptAsync($$$"""
                    (() => {
                        const data={{{JsonSerializer.Serialize(new { userAgentPlatform, platform, touchPoints })}}};
                        Object.defineProperty(navigator,'userAgentData',{value:data.userAgentPlatform===null ? undefined : {platform:data.userAgentPlatform}});
                        Object.defineProperty(navigator,'platform',{value:data.platform});
                        Object.defineProperty(navigator,'maxTouchPoints',{value:data.touchPoints});
                    })();
                    """);
            }
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync(DashboardUrls.AddTelemetryPane(DashboardUrls.StructuredLogsUrl(resource: "multi-a")));

            // Read the browser, not the test host: iPadOS desktop mode reports MacIntel.
            var usesCommand = await page.EvaluateAsync<bool>("""
                () => /^(mac|iphone|ipad|ipod|ios)/i.test(navigator.userAgentData?.platform || navigator.platform)
                """);
            var selectionModifier = usesCommand ? KeyboardModifier.Meta : KeyboardModifier.Control;
            var pane = page.Locator(".resource-pane");
            var title = page.Locator("h1.resource-title");
            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "multi-a", Exact = true }).First).ToBeVisibleAsync();
            var telemetryToggle = pane.GetByRole(AriaRole.Button, new() { Name = Resources.Layout.NavMenuTelemetrySourcesTab, Exact = true });
            if (await telemetryToggle.GetAttributeAsync("aria-pressed") != "true")
            {
                await telemetryToggle.ClickAsync();
            }
            var telemetrySection = pane.Locator("button.resource-pane-section-header", new() { HasTextRegex = new Regex("^Telemetry sources") });
            if (await telemetrySection.CountAsync() > 0 &&
                await telemetrySection.GetAttributeAsync("aria-expanded") == "false")
            {
                await telemetrySection.ClickAsync();
            }
            await Assertions.Expect(title).ToHaveTextAsync("multi-a");

            await GetRowLink("multi-c").ClickAsync(new LocatorClickOptions { Modifiers = [selectionModifier] });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/structuredlogs\?resource=multi-a&resource=multi-c$"));
            await Assertions.Expect(title).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourceHeaderSelectedResources, 2));
            await Assertions.Expect(pane.Locator("a[aria-current='true']")).ToHaveCountAsync(2);

            // Shift+click selects the rows between the last clicked row (multi-c) and the clicked row.
            Assert.NotEmpty(await page.EvaluateAsync<string>("""
                () => {
                    const range=document.createRange(),selection=window.getSelection();
                    range.selectNodeContents(document.querySelector('.resource-content'));
                    selection.removeAllRanges();
                    selection.addRange(range);
                    return selection.toString();
                }
                """));
            await GetRowLink("multi-a").ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.Shift] });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/structuredlogs\?resource=multi-a&resource=multi-b&resource=multi-c$"));
            await Assertions.Expect(title).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourceHeaderSelectedResources, 3));
            Assert.Equal(string.Empty, await page.EvaluateAsync<string>("() => window.getSelection().toString()"));
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.getSelection().rangeCount"));

            var hint = page.Locator(".resource-selection-hint");
            await Assertions.Expect(hint).ToBeVisibleAsync();
            var hintGeometry = await hint.EvaluateAsync<double[]>("""
                hint => {
                    const h=hint.getBoundingClientRect(),f=hint.parentElement.getBoundingClientRect();
                    const groups=[...hint.querySelectorAll('.resource-selection-instruction')].map(e=>e.getBoundingClientRect());
                    const counts=hint.parentElement.firstElementChild.getBoundingClientRect();
                    const actions=hint.parentElement.querySelector('.resource-pane-foot-actions').getBoundingClientRect();
                    return [Math.abs(h.x+h.width/2-f.x-f.width/2),Math.abs(groups[0].y-groups[1].y),
                        h.height,counts.right<=h.left && actions.left>=h.right ? 1:0];
                }
                """);
            Assert.InRange(hintGeometry[0], 0, .5);
            Assert.Equal(0, hintGeometry[1]);
            Assert.InRange(hintGeometry[2], 0, 18);
            Assert.Equal(1, hintGeometry[3]);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(hint).ToBeVisibleAsync();
            await page.WaitForFunctionAsync("""
                () => Math.abs(document.querySelector('.resource-pane').getBoundingClientRect().width -
                    document.querySelector('.resources-layout').getBoundingClientRect().width) < .5
                """);
            var footerBounds = await page.Locator(".resource-workspace-foot .resource-pane-foot").BoundingBoxAsync();
            var hintBounds = await hint.BoundingBoxAsync();
            Assert.NotNull(footerBounds);
            Assert.NotNull(hintBounds);
            Assert.InRange(Math.Abs(hintBounds.X + hintBounds.Width / 2 - footerBounds.X - footerBounds.Width / 2), 0, .5);
            await Assertions.Expect(hint.Locator("kbd").First).ToHaveTextAsync(usesCommand ? "⌘ + click" : "Ctrl + click");
            await Assertions.Expect(hint.Locator("kbd").Last).ToHaveTextAsync("Shift + click");
            await Assertions.Expect(hint).ToContainTextAsync("Add/remove");
            await Assertions.Expect(hint).ToContainTextAsync("Select range");
            await Assertions.Expect(hint).ToHaveAttributeAsync("aria-label", usesCommand
                ? Resources.Layout.ResourcePaneSelectionCommandDescription
                : Resources.Layout.ResourcePaneSelectionControlDescription);
            await ResourcePaneNavigation.ToggleResourceListAsync(page);
            await Assertions.Expect(hint).ToBeVisibleAsync();

            await GetRowLink("multi-a").ClickAsync();
            await Assertions.Expect(GetRowLink("multi-a")).ToHaveAttributeAsync("aria-current", "page");
            await GetRowLink("multi-b").ClickAsync(new LocatorClickOptions { Modifiers = [selectionModifier] });
            await Assertions.Expect(pane.Locator("a[aria-current='true']")).ToHaveCountAsync(2);
            await GetRowLink("multi-c").ClickAsync(new LocatorClickOptions { Modifiers = [selectionModifier] });
            await Assertions.Expect(pane.Locator("a[aria-current='true']")).ToHaveCountAsync(3);
            await GetRowLink("multi-b").ClickAsync(new LocatorClickOptions { Modifiers = [selectionModifier] });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/structuredlogs\?resource=multi-a&resource=multi-c$"));

            await GetRowLink("multi-b").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/structuredlogs/resource/multi-b$"));
            await Assertions.Expect(title).ToHaveTextAsync("multi-b");
            await Assertions.Expect(GetRowLink("multi-b")).ToHaveAttributeAsync("aria-current", "page");

            ILocator GetRowLink(string resourceName) => pane.GetByText(resourceName, new() { Exact = true }).Last.Locator("xpath=ancestor::a");
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcePaneResizer_ResizesAndPersistsWidth()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync("/structuredlogs");
            await page.EvaluateAsync($"localStorage.removeItem('{BrowserStorageKeys.ResourcePaneWidth}')");
            await page.ReloadAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Actions for TestResource" }).WaitForAsync();

            var pane = page.Locator(".resource-pane");
            var resizer = page.GetByRole(AriaRole.Separator, new PageGetByRoleOptions { Name = Resources.Layout.ResourcePaneResize });
            await resizer.DblClickAsync();
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", "420");

            var box = await resizer.BoundingBoxAsync();
            Assert.NotNull(box);
            var x = box.X + box.Width / 2;
            var y = box.Y + box.Height / 2;
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(x + 200, y, new MouseMoveOptions { Steps = 5 });
            await page.Mouse.UpAsync();

            const int draggedWidth = 560;
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", draggedWidth.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(draggedWidth, await GetPaneWidthAsync());

            await resizer.FocusAsync();
            await page.Keyboard.PressAsync("ArrowLeft");
            var storedWidth = draggedWidth - 10;
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", storedWidth.ToString(CultureInfo.InvariantCulture));

            await AssertStoredWidthAsync(storedWidth.ToString(CultureInfo.InvariantCulture));
            await page.ReloadAsync();
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", storedWidth.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(storedWidth, await GetPaneWidthAsync());

            await resizer.FocusAsync();
            await page.Keyboard.PressAsync("End");
            const int maximumWidth = 560;
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", maximumWidth.ToString(CultureInfo.InvariantCulture));

            await resizer.DblClickAsync();
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", "420");
            Assert.Equal(420, await GetPaneWidthAsync());
            await AssertStoredWidthAsync("420");

            async Task<int> GetPaneWidthAsync() => await pane.EvaluateAsync<int>("e => Math.round(e.getBoundingClientRect().width)");

            async Task AssertStoredWidthAsync(string expected)
            {
                await AsyncTestHelpers.AssertIsTrueRetryAsync(
                    async () => await page.EvaluateAsync<string?>($"localStorage.getItem('{BrowserStorageKeys.ResourcePaneWidth}')") == expected,
                    "Resource pane width wasn't persisted.");
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourcePaneResizer_DelayedRendersDoNotLoseKeyboardSteps()
    {
        await RunTestAsync(async page =>
        {
            await page.SetViewportSizeAsync(1440, 900);
            await page.GotoAsync("/structuredlogs");
            await Assertions.Expect(page.Locator(".resource-pane-resizer")).ToHaveAttributeAsync("aria-disabled", "false");
            await page.EvaluateAsync("""
                async () => {
                    const asset = performance.getEntriesByType('resource').find(entry =>
                        entry.name.includes('/Components/Layout/ResourcesLayout.') &&
                        new URL(entry.name).pathname.endsWith('.js'));
                    if (!asset) throw new Error('Resource layout module was not loaded.');
                    const module = await import(asset.name);
                    const layout = document.createElement('div');
                    layout.id = 'resize-race-fixture';
                    layout.style.setProperty('--resource-pane-expanded-width', '420px');
                    layout.innerHTML = '<aside class="resource-pane"><div class="resource-pane-resizer" role="separator" aria-valuenow="420" style="width:10px;height:40px"></div></aside>';
                    document.body.append(layout);
                    const handle = layout.querySelector('[role="separator"]');
                    const requests = [];
                    module.registerPaneResizer(layout, {
                        invokeMethodAsync: (_, width) => new Promise(resolve => requests.push({ width, resolve }))
                    }, 420, 560);
                    window.resizeRace = {
                        requests,
                        render: width => {
                            layout.style.setProperty('--resource-pane-expanded-width', `${width}px`);
                            handle.setAttribute('aria-valuenow', `${width}`);
                        }
                    };
                }
                """);
            var resizer = page.Locator("#resize-race-fixture [role='separator']");
            await resizer.PressAsync("ArrowRight");
            await resizer.PressAsync("ArrowRight");
            await page.EvaluateAsync("() => window.resizeRace.render(430)");
            await resizer.PressAsync("ArrowRight");
            var requests = await page.EvaluateAsync<int[]>("() => window.resizeRace.requests.map(request => request.width)");
            Assert.Equal([430, 440, 450], requests);
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", "450");

            await page.EvaluateAsync("""
                () => {
                    window.resizeRace.render(440);
                    window.resizeRace.requests[1].resolve();
                    window.resizeRace.render(430);
                    window.resizeRace.requests[0].resolve();
                }
                """);
            await Assertions.Expect(resizer).ToHaveAttributeAsync("aria-valuenow", "450");
            await page.EvaluateAsync("""
                () => {
                    window.resizeRace.render(450);
                    window.resizeRace.requests[2].resolve();
                }
                """);
            // Once every commit is acknowledged, a restored preference becomes authoritative again.
            await page.EvaluateAsync("() => window.resizeRace.render(507)");
            await resizer.PressAsync("ArrowRight");
            requests = await page.EvaluateAsync<int[]>("() => window.resizeRace.requests.map(request => request.width)");
            Assert.Equal([430, 440, 450, 517], requests);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceTag_AddedFromOverviewGroupsResourceInResourcePane()
    {
        var resourceName = MockDashboardClient.TestResource1.DisplayName;
        const string tag = "Frontend team";

        await RunTestAsync(async page =>
        {
            await page.GotoAsync($"/resources/{resourceName}");

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Resources.Resources.ResourceTagsAdd, Exact = true }).ClickAsync();
            var input = page.GetByRole(AriaRole.Combobox);
            await input.FillAsync("  frontend   team ");
            await Assertions.Expect(page.GetByRole(AriaRole.Option)).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Resources.ResourceTagsCreate, "frontend team"));
            await input.PressAsync("Enter");

            // The editor stays open with an empty input so several tags can be added in a row.
            await Assertions.Expect(page.Locator(".tag-chip-name")).ToHaveTextAsync("frontend team");
            await Assertions.Expect(input).ToBeFocusedAsync();
            await Assertions.Expect(input).ToHaveValueAsync(string.Empty);
            await input.PressAsync("Backspace");
            await Assertions.Expect(page.Locator(".tag-chip-name")).ToHaveCountAsync(0);
            await input.FillAsync(tag);
            await input.PressAsync("Enter");
            await input.PressAsync("Escape");
            await Assertions.Expect(page.Locator(".tag-chip-name")).ToHaveTextAsync(tag);

            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new PageGetByRoleOptions { Name = Resources.Layout.NavMenuTagsTab, Exact = true }).ClickAsync();

            var pane = page.Locator(".resource-pane");
            var tagGroup = pane.GetByRole(AriaRole.Group, new LocatorGetByRoleOptions { Name = tag });
            await Assertions.Expect(tagGroup.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = resourceName })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("fluent-tab[aria-selected='true']")).ToHaveAccessibleNameAsync(Resources.Layout.ResourceTabOverview);

            // Grouping is remembered when reloading a resource URL.
            await page.GotoAsync($"/resources/{resourceName}");
            await Assertions.Expect(tagGroup).ToBeVisibleAsync();

            await page.Locator("#resourcePaneGroupingButton").ClickAsync();
            await page.GetByRole(AriaRole.Menuitemradio, new PageGetByRoleOptions { Name = Resources.Layout.ResourcePaneGroupingNone, Exact = true }).ClickAsync();
            await Assertions.Expect(tagGroup).ToHaveCountAsync(0);
            await Assertions.Expect(pane.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = resourceName })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("fluent-tab[aria-selected='true']")).ToHaveAccessibleNameAsync(Resources.Layout.ResourceTabOverview);
        });
    }

    private static ResourceLogs CreateResourceLogs(string resourceName)
    {
        return new ResourceLogs
        {
            Resource = CreateResource(name: resourceName, instanceId: resourceName),
            ScopeLogs =
            {
                new ScopeLogs
                {
                    Scope = CreateScope(),
                    LogRecords = { CreateLogRecord(message: resourceName) }
                }
            }
        };
    }
}
