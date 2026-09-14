// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

            await SetAndVerifyTheme(Dialogs.SettingsDialogSystemTheme, null).DefaultTimeout(); // don't guess system theme
            await SetAndVerifyTheme(Dialogs.SettingsDialogLightTheme, "light").DefaultTimeout();
            await SetAndVerifyTheme(Dialogs.SettingsDialogDarkTheme, "dark").DefaultTimeout();

            async Task SetAndVerifyTheme(string checkboxText, string? expected)
            {
                var settingsButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = Layout.MainLayoutLaunchSettings });
                await settingsButton.ClickAsync();

                // Set theme
                var checkbox = page.GetByRole(AriaRole.Radio).And(page.GetByText(checkboxText)).First;
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

                checkbox = page.GetByRole(AriaRole.Radio).And(page.GetByText(checkboxText)).First;

                await AsyncTestHelpers.AssertIsTrueRetryAsync(
                    async () => await checkbox.IsCheckedAsync(),
                    "Checkbox isn't immediately checked.");

                await closeButton.First.ClickAsync();
            }
        });
    }

    [Theory]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    [InlineData("Light", "rgb(81, 43, 212)", "rgb(116, 85, 221)")]
    [InlineData("Dark", "rgb(185, 170, 238)", "rgb(220, 213, 246)")]
    public async Task AppBar_AccentColors_UseFluentDesignTokens(string theme, string expectedRest, string expectedHover)
    {
        await RunTestAsync(async page =>
        {
            await PlaywrightFixture.GoToHomeAndWaitForDataGridLoad(page).DefaultTimeout();

            await page.EvaluateAsync(
                """
                theme => import('/js/app-theme.js').then(module => module.updateTheme(theme))
                """,
                theme).DefaultTimeout();

            await Assertions
                .Expect(page.Locator("html"))
                .ToHaveAttributeAsync("data-theme", theme.ToLowerInvariant());

            var colors = await page.EvaluateAsync<string[]>(
                """
                async () => {
                    const fluent = await import('/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js');
                    const root = document.getElementById('aspire-design-system');
                    const style = getComputedStyle(root);

                    function normalize(color) {
                        const probe = document.createElement('span');
                        probe.style.color = color;
                        document.body.appendChild(probe);
                        const normalized = getComputedStyle(probe).color;
                        probe.remove();
                        return normalized;
                    }

                    return [
                        normalize(fluent.accentFillRest.getValueFor(root).createCSS()),
                        normalize(fluent.accentForegroundRest.getValueFor(root).createCSS()),
                        normalize(fluent.accentStrokeControlRest.getValueFor(root).createCSS()),
                        normalize(style.getPropertyValue('--accent-fill-rest')),
                        normalize(style.getPropertyValue('--accent-foreground-rest')),
                        normalize(style.getPropertyValue('--accent-stroke-control-rest')),
                        normalize(fluent.accentFillHover.getValueFor(root).createCSS()),
                        normalize(fluent.accentForegroundHover.getValueFor(root).createCSS()),
                        normalize(fluent.accentStrokeControlHover.getValueFor(root).createCSS()),
                        normalize(style.getPropertyValue('--accent-fill-hover')),
                        normalize(style.getPropertyValue('--accent-foreground-hover')),
                        normalize(style.getPropertyValue('--accent-stroke-control-hover')),
                        normalize(fluent.accentFillActive.getValueFor(root).createCSS()),
                        normalize(fluent.accentForegroundActive.getValueFor(root).createCSS()),
                        normalize(fluent.accentStrokeControlActive.getValueFor(root).createCSS()),
                        normalize(style.getPropertyValue('--accent-fill-active')),
                        normalize(style.getPropertyValue('--accent-foreground-active')),
                        normalize(style.getPropertyValue('--accent-stroke-control-active')),
                        normalize(fluent.accentFillFocus.getValueFor(root).createCSS()),
                        normalize(fluent.accentForegroundFocus.getValueFor(root).createCSS()),
                        normalize(fluent.accentStrokeControlFocus.getValueFor(root).createCSS()),
                        normalize(style.getPropertyValue('--accent-fill-focus')),
                        normalize(style.getPropertyValue('--accent-foreground-focus')),
                        normalize(style.getPropertyValue('--accent-stroke-control-focus')),
                        normalize(style.getPropertyValue('--dash-focus-ring-color')),
                    ];
                }
                """).DefaultTimeout();

            Assert.All(colors[..6], color => Assert.Equal(expectedRest, color));
            Assert.All(colors[6..12], color => Assert.Equal(expectedHover, color));
            Assert.All(colors[12..18], color => Assert.Equal(expectedRest, color));
            Assert.All(colors[18..24], color => Assert.Equal(expectedRest, color));
            Assert.Equal(expectedRest, colors[24]);
        });
    }
}
