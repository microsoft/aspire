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

            async Task SetAndVerifyTheme(string checkboxText, string? expected)
            {
                var settingsButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Layout.MainLayoutLaunchSettings });
                await settingsButton.ClickAsync();

                // Set theme
                var checkbox = await GetThemeRadioAsync();
                await checkbox.ClickAsync();

                if (expected != null)
                {
                    await Assertions
                        .Expect(page.Locator("html"))
                        .ToHaveAttributeAsync("data-theme", expected);
                }

                // Close settings.
                var closeButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Layout.MainLayoutSettingsDialogClose });
                await closeButton.First.ClickAsync();

                // Re-open settings and assert that the correct checkbox is checked.
                await settingsButton.ClickAsync();

                checkbox = await GetThemeRadioAsync();

                await AsyncTestHelpers.AssertIsTrueRetryAsync(
                    async () => await checkbox.EvaluateAsync<bool>("element => element.checked"),
                    "Checkbox isn't immediately checked.");

                await closeButton.First.ClickAsync();

                async Task<ILocator> GetThemeRadioAsync()
                {
                    var label = page.GetByText(checkboxText, new PageGetByTextOptions { Exact = true }).First;
                    var radioId = await label.GetAttributeAsync("for");
                    Assert.False(string.IsNullOrEmpty(radioId));
                    return page.Locator($"fluent-radio[id='{radioId}']");
                }
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
                    const brand = rect('.brand-logo');
                    const application = rect('a.logo:not(.brand-logo)');
                    const title = rect('.page-title-slot');
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
                        titleLeft: title.left,
                        titleRight: title.right,
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
            Assert.True(root.GetProperty("applicationRight").GetDouble() <= root.GetProperty("titleLeft").GetDouble());
            Assert.True(root.GetProperty("titleRight").GetDouble() <= root.GetProperty("firstActionLeft").GetDouble());
            Assert.InRange(root.GetProperty("widestAction").GetDouble(), 38, 42);
            Assert.InRange(
                root.GetProperty("headerRight").GetDouble() - root.GetProperty("lastActionRight").GetDouble(),
                0,
                20);
        });
    }

}
