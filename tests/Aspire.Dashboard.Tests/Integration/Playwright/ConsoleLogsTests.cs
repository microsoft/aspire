// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

[RequiresFeature(TestFeature.Playwright)]
public class ConsoleLogsTests : PlaywrightTestsBase<ConsoleLogsTests.ConsoleLogsDashboardServerFixture>
{
    public ConsoleLogsTests(ConsoleLogsDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task SettingsMenu_ItemSelectionRestoresFocusToTrigger()
    {
        await RunTestAsync(async page =>
        {
            await GoToConsoleLogsAsync(page);

            var settingsButton = page.Locator("header.content-header").Locator($"fluent-button[title='{Dashboard.Resources.ConsoleLogs.ConsoleLogsSettings}']");
            await settingsButton.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.ConsoleLogs.ConsoleLogsTimestampShow, Exact = true }).ClickAsync();

            await Assertions.Expect(settingsButton).ToBeFocusedAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceActionsMenu_CommandSelectionRestoresFocusToTrigger()
    {
        await RunTestAsync(async page =>
        {
            await GoToConsoleLogsAsync(page);

            var resourceActionsButton = page.Locator($"fluent-button[title='{ControlsStrings.ResourceActions}']");
            await resourceActionsButton.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = "Test command", Exact = true }).ClickAsync();

            await Assertions.Expect(resourceActionsButton).ToBeFocusedAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ClearDataMenu_ItemSelectionRestoresFocusToTrigger()
    {
        await RunTestAsync(async page =>
        {
            await GoToConsoleLogsAsync(page);

            var clearDataButton = page.Locator($"fluent-button[title='{ControlsStrings.ClearSignalsButtonTitle}']");
            await clearDataButton.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = ControlsStrings.ClearAllResources, Exact = true }).ClickAsync();

            await Assertions.Expect(clearDataButton).ToBeFocusedAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceSelect_SelectionKeepsFocusOnNativeSelect()
    {
        await RunTestAsync(async page =>
        {
            await GoToConsoleLogsAsync(page);

            var resourceSelect = page.Locator("fluent-select.resource-list");
            await resourceSelect.ClickAsync();
            await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "OtherResource", Exact = true }).ClickAsync();

            await Assertions.Expect(resourceSelect).ToBeFocusedAsync();
        });
    }

    private static async Task GoToConsoleLogsAsync(IPage page)
    {
        await page.GotoAsync("/consolelogs/resource/TestResource");
        await Assertions.Expect(page.Locator("fluent-select.resource-list")).ToBeVisibleAsync();
    }

    public sealed class ConsoleLogsDashboardServerFixture : DashboardServerFixture
    {
        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: "TestResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                commands:
                [
                    new CommandViewModel(
                        name: "test-command",
                        state: CommandViewModelState.Enabled,
                        displayName: "Test command",
                        displayDescription: "Test command",
                        confirmationMessage: string.Empty,
                        argumentInputs: [],
                        isHighlighted: false,
                        iconName: string.Empty,
                        iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular)
                ]),
            ModelTestHelpers.CreateResource(
                resourceName: "OtherResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running)
        ];
    }
}
