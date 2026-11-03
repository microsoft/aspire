// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class AppBarTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public AppBarTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task AppBar_Change_Theme()
    {
        // Arrange
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            await SetAndVerifyTheme(Dialogs.SettingsDialogSystemTheme, null); // don't guess system theme
            await SetAndVerifyTheme(Dialogs.SettingsDialogLightTheme, "light");
            await SetAndVerifyTheme(Dialogs.SettingsDialogDarkTheme, "dark");

            async Task SetAndVerifyTheme(string optionText, string? expected)
            {
                var settingsButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Layout.MainLayoutLaunchSettings });
                await settingsButton.ClickAsync();

                var themeGroup = page.GetByRole(AriaRole.Group, new() { Name = Dialogs.SettingsDialogTheme });
                var option = themeGroup.GetByRole(AriaRole.Button, new() { Name = optionText, Exact = true });
                await option.ClickAsync();

                if (expected != null)
                {
                    await Assertions
                        .Expect(page.Locator("html"))
                        .ToHaveAttributeAsync("data-theme", expected);
                }

                // Close settings.
                var closeButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Layout.MainLayoutSettingsDialogClose });
                await closeButton.First.ClickAsync();

                await settingsButton.ClickAsync();
                themeGroup = page.GetByRole(AriaRole.Group, new() { Name = Dialogs.SettingsDialogTheme });
                option = themeGroup.GetByRole(AriaRole.Button, new() { Name = optionText, Exact = true });
                await Assertions.Expect(option).ToHaveAttributeAsync("aria-pressed", "true");

                await closeButton.First.ClickAsync();
            }
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task AppBar_ContentIsArrangedInOneRow()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            var header = page.Locator(".layout > header");
            await Assertions.Expect(header).ToBeVisibleAsync();

            var layout = await header.EvaluateAsync<string>(
                """
                header => {
                    const bounds = header.getBoundingClientRect();
                    const rect = selector => header.querySelector(selector).getBoundingClientRect();
                    const brand = rect('.brand-logo-container > a.logo');
                    const application = rect('.application-name');
                    const actions = [...header.querySelectorAll('.header-button')]
                        .map(element => element.getBoundingClientRect());
                    const visibleChildren = [...header.children]
                        .map(element => element.getBoundingClientRect())
                        .filter(child => child.width > 0 && child.height > 0);

                    return JSON.stringify({
                        height: bounds.height,
                        sameRow: visibleChildren.every(child =>
                            Math.abs((child.top + child.height / 2) - (bounds.top + bounds.height / 2)) < 1),
                        brandLeft: brand.left,
                        brandRight: brand.right,
                        applicationLeft: application.left,
                        applicationRight: application.right,
                        firstActionLeft: actions[0].left,
                        widestAction: Math.max(...actions.map(action => action.width)),
                        lastActionRight: actions[actions.length - 1].right,
                        headerRight: bounds.right
                    });
                }

                """);

            using var document = JsonDocument.Parse(layout);
            var root = document.RootElement;

            Assert.InRange(root.GetProperty("height").GetDouble(), 50, 54);
            Assert.True(root.GetProperty("sameRow").GetBoolean());
            Assert.True(root.GetProperty("brandRight").GetDouble() <= root.GetProperty("applicationLeft").GetDouble());
            Assert.True(root.GetProperty("applicationRight").GetDouble() <= root.GetProperty("firstActionLeft").GetDouble());
            Assert.InRange(root.GetProperty("widestAction").GetDouble(), 31, 33);
            Assert.InRange(
                root.GetProperty("headerRight").GetDouble() - root.GetProperty("lastActionRight").GetDouble(),
                0,
                20);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task AppBar_TooltipDoesNotBlockAdjacentHeaderActions()
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            await page.Locator("#dashboard-settings-button").HoverAsync();
            await Assertions.Expect(page.Locator(".dashboard-tooltip")).ToBeVisibleAsync();

            await page.Locator("#dashboard-help-button").ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        });
    }

}
