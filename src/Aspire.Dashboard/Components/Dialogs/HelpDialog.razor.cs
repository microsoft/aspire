// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class HelpDialog
{
    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required ShortcutManager ShortcutManager { get; init; }

    private List<KeyboardShortcutCategory> GetShortcutsByCategory()
    {
        List<KeyboardShortcutCategory> categories =
        [
            new(Loc[nameof(Resources.Dialogs.HelpDialogCategoryPanels)],
            [
                new KeyboardShortcut(AspireKeyboardShortcut.IncreasePanelSize, ["+"], Loc[nameof(Resources.Dialogs.HelpDialogIncreasePanelSize)]),
                new KeyboardShortcut(AspireKeyboardShortcut.DecreasePanelSize, ["-"], Loc[nameof(Resources.Dialogs.HelpDialogDecreasePanelSize)]),
                new KeyboardShortcut(AspireKeyboardShortcut.ResetPanelSize, ["shift", "r"], Loc[nameof(Resources.Dialogs.HelpDialogResetPanelSize)]),
                new KeyboardShortcut(AspireKeyboardShortcut.ToggleOrientation, ["shift", "t"], Loc[nameof(Resources.Dialogs.HelpDialogTogglePanelOrientation)]),
                new KeyboardShortcut(AspireKeyboardShortcut.ClosePanel, ["shift", "x"], Loc[nameof(Resources.Dialogs.HelpDialogTogglePanelOpen)]),
            ]),
            new(Loc[nameof(Resources.Dialogs.HelpDialogCategoryPageNavigation)],
            [
                new KeyboardShortcut(AspireKeyboardShortcut.GoToResources, ["r"], Loc[nameof(Resources.Dialogs.HelpDialogGoToResources)]),
                new KeyboardShortcut(AspireKeyboardShortcut.GoToConsoleLogs, ["c"], Loc[nameof(Resources.Dialogs.HelpDialogGoToConsoleLogs)]),
                new KeyboardShortcut(AspireKeyboardShortcut.GoToStructuredLogs, ["s"], Loc[nameof(Resources.Dialogs.HelpDialogGoToStructuredLogs)]),
                new KeyboardShortcut(AspireKeyboardShortcut.GoToTraces, ["t"], Loc[nameof(Resources.Dialogs.HelpDialogGoToTraces)]),
                new KeyboardShortcut(AspireKeyboardShortcut.GoToMetrics, ["m"], Loc[nameof(Resources.Dialogs.HelpDialogGoToMetrics)]),
            ]),
            new(Loc[nameof(Resources.Dialogs.HelpDialogCategoryNavigation)],
            [
                new KeyboardShortcut(AspireKeyboardShortcut.Help, ["?"], Loc[nameof(Resources.Dialogs.HelpDialogGoToHelp)]),
                new KeyboardShortcut(AspireKeyboardShortcut.Settings, ["shift", "s"], Loc[nameof(Resources.Dialogs.HelpDialogGoToSettings)]),
                new KeyboardShortcut(AspireKeyboardShortcut.ToggleTerminalDock, ["`"], Loc[nameof(Resources.Dialogs.HelpDialogToggleTerminalDock)]),
            ])
        ];

        foreach (var category in categories)
        {
            category.Shortcuts.RemoveAll(shortcut => !ShortcutManager.IsShortcutAvailable(shortcut.Shortcut));
        }

        categories.RemoveAll(category => category.Shortcuts.Count == 0);

        return categories;
    }
}
