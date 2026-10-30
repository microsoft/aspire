// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
public sealed class ResourcePaneInteractionTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public ResourcePaneInteractionTests(DashboardServerFixture dashboardServerFixture)
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
    public async Task AllResourcesRow_ClearsSelectionAndListsResourcesInOverview()
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
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-pressed", "false");

            // With a selection, "Select all" isn't pressed. Pressing it selects all resources again.
            await selectAll.ClickAsync();
            await Assertions.Expect(title).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-pressed", "true");
            await Assertions.Expect(pane.Locator("a[aria-current]")).ToHaveCountAsync(0);

            var overviewCards = page.Locator(".resource-card-grid");
            await Assertions.Expect(overviewCards.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = resourceName, Exact = true })).ToBeVisibleAsync();

            // Switching tabs keeps all resources selected, including when returning to the overview.
            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = Resources.Layout.NavMenuConsoleLogsTab }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/consolelogs$"));
            await Assertions.Expect(page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = Resources.Layout.NavMenuConsoleLogsTab })).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(title).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);

            await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = Resources.Layout.ResourceTabOverview }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/resources$"));
            await Assertions.Expect(overviewCards).ToBeVisibleAsync();
            await Assertions.Expect(selectAll).ToHaveAttributeAsync("aria-pressed", "true");

            // The cleared selection is remembered when entering the resources view again.
            await page.GotoAsync("/resources");
            await Assertions.Expect(overviewCards).ToBeVisibleAsync();
            await Assertions.Expect(title).ToHaveTextAsync(Resources.Layout.ResourceHeaderAllResources);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceRowModifierClicks_SelectSeveralResources()
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
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync(DashboardUrls.AddTelemetryPane(DashboardUrls.StructuredLogsUrl(resource: "multi-a")));

            var pane = page.Locator(".resource-pane");
            var title = page.Locator("h1.resource-title");
            await Assertions.Expect(page.GetByRole(AriaRole.Gridcell, new() { Name = "multi-a", Exact = true })).ToBeVisibleAsync();
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

            await GetRowLink("multi-c").ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.ControlOrMeta] });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/structuredlogs\?resource=multi-a&resource=multi-c$"));
            await Assertions.Expect(title).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourceHeaderSelectedResources, 2));
            await Assertions.Expect(pane.Locator("a[aria-current='true']")).ToHaveCountAsync(2);

            // Shift+click selects the rows between the last clicked row (multi-c) and the clicked row.
            await GetRowLink("multi-a").ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.Shift] });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"/structuredlogs\?resource=multi-a&resource=multi-b&resource=multi-c$"));
            await Assertions.Expect(title).ToHaveTextAsync(string.Format(CultureInfo.CurrentCulture, Resources.Layout.ResourceHeaderSelectedResources, 3));

            await GetRowLink("multi-b").ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.ControlOrMeta] });
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
            await page.WaitForTimeoutAsync(1000);
            var fittedWidth = int.Parse((await resizer.GetAttributeAsync("aria-valuenow"))!, CultureInfo.InvariantCulture);

            var box = await resizer.BoundingBoxAsync();
            Assert.NotNull(box);
            var x = box.X + box.Width / 2;
            var y = box.Y + box.Height / 2;
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(x + 200, y, new MouseMoveOptions { Steps = 5 });
            await page.Mouse.UpAsync();

            var draggedWidth = fittedWidth + 200;
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
            await page.WaitForTimeoutAsync(1000);
            var exactFitWidth = int.Parse((await resizer.GetAttributeAsync("aria-valuenow"))!, CultureInfo.InvariantCulture);
            Assert.InRange(exactFitWidth, 240, maximumWidth);
            Assert.Equal(exactFitWidth, await GetPaneWidthAsync());

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