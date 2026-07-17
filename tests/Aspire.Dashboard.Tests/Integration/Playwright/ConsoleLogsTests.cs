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
            var commandsItem = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourceActionCommandsText, Exact = true });
            await commandsItem.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = "Test command", Exact = true }).ClickAsync();

            await Assertions.Expect(resourceActionsButton).ToBeFocusedAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ResourceActionsMenu_EscapeInsideOpenSubmenu_ClosesOneLevelAtATime()
    {
        await RunTestAsync(async page =>
        {
            await GoToConsoleLogsAsync(page);

            var resourceActionsButton = page.Locator($"fluent-button[title='{ControlsStrings.ResourceActions}']");
            await resourceActionsButton.ClickAsync();
            var commandsItem = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = Dashboard.Resources.Resources.ResourceActionCommandsText, Exact = true });
            await commandsItem.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            await Assertions.Expect(commandsItem).ToHaveAttributeAsync("aria-expanded", "true");

            await page.Keyboard.PressAsync("Escape");

            await Assertions.Expect(page.GetByRole(AriaRole.Menu).First).ToBeVisibleAsync();
            await Assertions.Expect(commandsItem).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(commandsItem).ToBeFocusedAsync();

            await page.Keyboard.PressAsync("Escape");

            await Assertions.Expect(page.GetByRole(AriaRole.Menu).First).ToBeHiddenAsync();
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
                    CreateCommand("test-command", "Test command"),
                    CreateCommand("test-command-2", "Test command 2"),
                    CreateCommand("test-command-3", "Test command 3"),
                    CreateCommand("test-command-4", "Test command 4"),
                    CreateCommand("test-command-5", "Test command 5"),
                    CreateCommand("test-command-6", "Test command 6")
                ]),
            ModelTestHelpers.CreateResource(
                resourceName: "OtherResource",
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running)
        ];
    }

    private static CommandViewModel CreateCommand(string name, string displayName)
    {
        return new CommandViewModel(
            name: name,
            state: CommandViewModelState.Enabled,
            displayName: displayName,
            displayDescription: displayName,
            confirmationMessage: string.Empty,
            argumentInputs: [],
            isHighlighted: false,
            iconName: string.Empty,
            iconVariant: Microsoft.FluentUI.AspNetCore.Components.IconVariant.Regular);
    }
}
