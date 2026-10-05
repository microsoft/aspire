// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public class HelpDialogTests : DashboardTestContext
{
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

        var cut = Render<HelpDialog>();

        var heading = Assert.Single(cut.FindAll("h6"), h => h.TextContent == Resources.Dialogs.HelpDialogCategoryNavigation);
        var navigationShortcuts = cut.Find($"dl[aria-labelledby='{heading.Id}']");
        List<string> expectedDescriptions = [Resources.Dialogs.HelpDialogGoToHelp, Resources.Dialogs.HelpDialogGoToSettings];
        List<string> expectedKeys = ["?", "shift", "s"];
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

        var cut = Render<HelpDialog>();

        Assert.Equal(
            [
                Resources.Dialogs.HelpDialogCategoryPanels,
                Resources.Dialogs.HelpDialogCategoryPageNavigation,
                Resources.Dialogs.HelpDialogCategoryNavigation
            ],
            cut.FindAll("h6").Select(element => element.TextContent));
        Assert.Equal(
            [
                Resources.Dialogs.HelpDialogIncreasePanelSize,
                Resources.Dialogs.HelpDialogDecreasePanelSize,
                Resources.Dialogs.HelpDialogResetPanelSize,
                Resources.Dialogs.HelpDialogTogglePanelOrientation,
                Resources.Dialogs.HelpDialogTogglePanelOpen,
                Resources.Dialogs.HelpDialogGoToStructuredLogs,
                Resources.Dialogs.HelpDialogGoToTraces,
                Resources.Dialogs.HelpDialogGoToMetrics,
                Resources.Dialogs.HelpDialogGoToHelp,
                Resources.Dialogs.HelpDialogGoToSettings
            ],
            cut.FindAll("dt").Select(element => element.TextContent));
    }
}
