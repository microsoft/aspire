// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class HelpDialog
{
    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    private List<KeyboardShortcutCategory> GetShortcutsByCategory()
    {
        List<KeyboardShortcut> navigationShortcuts =
        [
            new(["?"], Loc[nameof(Resources.Dialogs.HelpDialogGoToHelp)]),
            new(["shift", "s"], Loc[nameof(Resources.Dialogs.HelpDialogGoToSettings)])
        ];

        if (DashboardClient.IsEnabled && !DashboardClient.IsReadOnly)
        {
            navigationShortcuts.Add(new(["`"], Loc[nameof(Resources.Dialogs.HelpDialogToggleTerminalDock)]));
        }

        return
        [
            new(Loc[nameof(Resources.Dialogs.HelpDialogCategoryPanels)],
            [
                new KeyboardShortcut(["+"], Loc[nameof(Resources.Dialogs.HelpDialogIncreasePanelSize)]),
                new KeyboardShortcut(["-"], Loc[nameof(Resources.Dialogs.HelpDialogDecreasePanelSize)]),
                new KeyboardShortcut(["shift", "r"], Loc[nameof(Resources.Dialogs.HelpDialogResetPanelSize)]),
                new KeyboardShortcut(["shift", "t"], Loc[nameof(Resources.Dialogs.HelpDialogTogglePanelOrientation)]),
                new KeyboardShortcut(["shift", "x"], Loc[nameof(Resources.Dialogs.HelpDialogTogglePanelOpen)]),
            ]),
            new(Loc[nameof(Resources.Dialogs.HelpDialogCategoryPageNavigation)],
            [
                new KeyboardShortcut(["r"], Loc[nameof(Resources.Dialogs.HelpDialogGoToResources)]),
                new KeyboardShortcut(["c"], Loc[nameof(Resources.Dialogs.HelpDialogGoToConsoleLogs)]),
                new KeyboardShortcut(["s"], Loc[nameof(Resources.Dialogs.HelpDialogGoToStructuredLogs)]),
                new KeyboardShortcut(["t"], Loc[nameof(Resources.Dialogs.HelpDialogGoToTraces)]),
                new KeyboardShortcut(["m"], Loc[nameof(Resources.Dialogs.HelpDialogGoToMetrics)]),
            ]),
            new(Loc[nameof(Resources.Dialogs.HelpDialogCategoryNavigation)], navigationShortcuts)
        ];
    }
}
