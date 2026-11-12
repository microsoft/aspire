// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public class KeyboardShortcutsHelpTests : DashboardTestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageNavigation_OnlyShowsTerminalsShortcutWhenAvailable(bool expectTerminalsShortcut)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        var shortcutManager = Services.GetRequiredService<ShortcutManager>();
        shortcutManager.AddGlobalKeydownListener(new TestGlobalKeydownListener(AspireKeyboardShortcut.GoToResources));
        if (expectTerminalsShortcut)
        {
            shortcutManager.AddGlobalKeydownListener(new TestGlobalKeydownListener(AspireKeyboardShortcut.GoToTerminals));
        }

        var cut = Render<KeyboardShortcutsHelp>();

        var heading = Assert.Single(cut.FindAll("h6"), h => h.TextContent == Resources.Dialogs.HelpDialogCategoryPageNavigation);
        var navigationShortcuts = cut.Find($"dl[aria-labelledby='{heading.Id}']");
        List<string> expectedDescriptions = [Resources.Dialogs.HelpDialogGoToResources];
        List<string> expectedKeys = ["r"];
        if (expectTerminalsShortcut)
        {
            expectedDescriptions.Add(Resources.Dialogs.HelpDialogGoToTerminals);
            expectedKeys.Add("e");
        }

        Assert.Equal(expectedDescriptions, navigationShortcuts.QuerySelectorAll("dt").Select(element => element.TextContent));
        Assert.Equal(expectedKeys, navigationShortcuts.QuerySelectorAll("kbd").Select(element => element.TextContent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SiteWideNavigation_OnlyShowsTerminalShortcutWhenAvailable(bool expectTerminalShortcut)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        var shortcutManager = Services.GetRequiredService<ShortcutManager>();
        shortcutManager.AddGlobalKeydownListener(new TestGlobalKeydownListener(
            AspireKeyboardShortcut.Help,
            AspireKeyboardShortcut.Settings));
        if (expectTerminalShortcut)
        {
            shortcutManager.AddGlobalKeydownListener(new TestGlobalKeydownListener(AspireKeyboardShortcut.ToggleTerminalDock));
        }

        var cut = Render<KeyboardShortcutsHelp>();

        var heading = Assert.Single(cut.FindAll("h6"), h => h.TextContent == Resources.Dialogs.HelpDialogCategoryNavigation);
        var navigationShortcuts = cut.Find($"dl[aria-labelledby='{heading.Id}']");
        List<string> expectedDescriptions = [Resources.Dialogs.HelpDialogGoToHelp, Resources.Dialogs.HelpDialogGoToSettings];
        List<string> expectedKeys = ["?", "Shift", "S"];
        if (expectTerminalShortcut)
        {
            expectedDescriptions.Add(Resources.Dialogs.HelpDialogToggleTerminalDock);
            expectedKeys.Add("`");
        }

        Assert.Equal(expectedDescriptions, navigationShortcuts.QuerySelectorAll("dt").Select(element => element.TextContent));
        Assert.Equal(expectedKeys, navigationShortcuts.QuerySelectorAll("kbd").Select(element => element.TextContent));
        Assert.Equal(expectTerminalShortcut ? 1 : 0, cut.FindAll("kbd").Count(element => element.TextContent == "`"));
    }

    [Fact]
    public void Shortcuts_AlwaysShowsPanelsAndOnlyAvailableNavigation()
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        var shortcutManager = Services.GetRequiredService<ShortcutManager>();
        shortcutManager.AddGlobalKeydownListener(new TestGlobalKeydownListener(
            AspireKeyboardShortcut.Help,
            AspireKeyboardShortcut.Settings,
            AspireKeyboardShortcut.GoToStructuredLogs,
            AspireKeyboardShortcut.GoToTraces,
            AspireKeyboardShortcut.GoToMetrics));

        var cut = Render<KeyboardShortcutsHelp>();

        Assert.Equal(
            [
                Resources.Dialogs.HelpDialogCategoryTelemetry,
                Resources.Dialogs.HelpDialogCategoryPanels,
                Resources.Dialogs.HelpDialogCategoryNavigation
            ],
            cut.FindAll("h6").Select(element => element.TextContent));
        Assert.Equal(
            [
                Resources.Dialogs.HelpDialogGoToStructuredLogs,
                Resources.Dialogs.HelpDialogGoToTraces,
                Resources.Dialogs.HelpDialogGoToMetrics,
                Resources.Dialogs.HelpDialogIncreasePanelSize,
                Resources.Dialogs.HelpDialogDecreasePanelSize,
                Resources.Dialogs.HelpDialogResetPanelSize,
                Resources.Dialogs.HelpDialogTogglePanelOrientation,
                Resources.Dialogs.HelpDialogTogglePanelOpen,
                Resources.Dialogs.HelpDialogGoToHelp,
                Resources.Dialogs.HelpDialogGoToSettings
            ],
            cut.FindAll("dt").Select(element => element.TextContent));
    }

    [Fact]
    public void NavigationShortcuts_ShowsEveryAvailableDestinationWithItsBinding()
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        Services.GetRequiredService<ShortcutManager>().AddGlobalKeydownListener(new TestGlobalKeydownListener(
            AspireKeyboardShortcut.GoToResources,
            AspireKeyboardShortcut.GoToParameters,
            AspireKeyboardShortcut.GoToGraph,
            AspireKeyboardShortcut.GoToTerminals,
            AspireKeyboardShortcut.GoToExtensions,
            AspireKeyboardShortcut.GoToTelemetrySources,
            AspireKeyboardShortcut.GoToConsoleLogs,
            AspireKeyboardShortcut.GoToStructuredLogs,
            AspireKeyboardShortcut.GoToTraces,
            AspireKeyboardShortcut.GoToMetrics));

        var cut = Render<KeyboardShortcutsHelp>();
        var categories = cut.FindAll(".help-shortcut-category").Take(2).ToList();

        Assert.Equal(
            [Resources.Dialogs.HelpDialogCategoryPageNavigation, Resources.Dialogs.HelpDialogCategoryTelemetry],
            categories.Select(category => category.QuerySelector("h6")!.TextContent));
        Assert.Equal(
            [
                (Resources.Dialogs.HelpDialogGoToResources, "r"),
                (Resources.Dialogs.HelpDialogGoToParameters, "p"),
                (Resources.Dialogs.HelpDialogGoToGraph, "g"),
                (Resources.Dialogs.HelpDialogGoToTerminals, "e"),
                (Resources.Dialogs.HelpDialogGoToExtensions, "x"),
                (Resources.Dialogs.HelpDialogGoToTelemetrySources, "o"),
                (Resources.Dialogs.HelpDialogGoToConsoleLogs, "c"),
                (Resources.Dialogs.HelpDialogGoToStructuredLogs, "s"),
                (Resources.Dialogs.HelpDialogGoToTraces, "t"),
                (Resources.Dialogs.HelpDialogGoToMetrics, "m")
            ],
            categories.SelectMany(category => category.QuerySelectorAll(".help-shortcut"))
                .Select(row => (row.QuerySelector("dt")!.TextContent, row.QuerySelector("kbd")!.TextContent)));
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Resources.Dialogs.HelpDialogShortcutsHint, "F6"), cut.Find(".help-shortcuts-hint").TextContent);
        Assert.Equal("F6", cut.Find(".help-shortcuts-hint kbd").TextContent);
    }
}
