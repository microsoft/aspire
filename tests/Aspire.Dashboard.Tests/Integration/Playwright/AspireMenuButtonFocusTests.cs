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
public class AspireMenuButtonFocusTests : PlaywrightTestsBase<DashboardServerFixture>
{
    public AspireMenuButtonFocusTests(DashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task MenuButton_ItemSelected_RestoresFocusToMenuButton()
    {
        await RunTestAsync(async page =>
        {
            await page.GotoAsync("/structuredlogs").DefaultTimeout();

            // The structured logs "Remove data" menu relies on the AspireMenuButton default for focus
            // restoration rather than opting in explicitly, so it covers the menus that regressed in
            // https://github.com/microsoft/aspire/issues/17656. Its items only clear the telemetry
            // repository, so nothing else legitimately claims focus and the assertion stays meaningful.
            // Focus after a menu closes is a browser-only behavior that bUnit can't observe.
            //
            // Keep the host for focus restoration and inspect the state FluentMenu writes to its anchor.
            var clearButton = page.Locator($"fluent-button[title='{ControlsStrings.ClearSignalsButtonTitle}'][aria-haspopup='menu']").First;
            var initialExpandedState = await clearButton.GetAttributeAsync("aria-expanded");
            Assert.Null(initialExpandedState);

            var clearButtonId = await clearButton.GetAttributeAsync("id");
            Assert.False(string.IsNullOrEmpty(clearButtonId));

            await clearButton.ClickAsync();
            await Assertions.Expect(clearButton).ToHaveAttributeAsync("aria-expanded", "true");

            await page.Locator("fluent-menu-item#clear-menu-all").ClickAsync();
            await Assertions.Expect(clearButton).ToHaveAttributeAsync("aria-expanded", "false");

            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                async () => string.Equals(await page.EvaluateAsync<string?>("() => document.activeElement?.id"), clearButtonId, StringComparison.Ordinal),
                "Focus should return to the menu button after a menu item is selected.");
        });
    }
}
