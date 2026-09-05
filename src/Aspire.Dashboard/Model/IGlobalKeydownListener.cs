// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public interface IGlobalKeydownListener
{
    IReadOnlySet<AspireKeyboardShortcut> SubscribedShortcuts { get; }
    Task OnPageKeyDownAsync(AspireKeyboardShortcut shortcut);
}

public enum AspireKeyboardShortcut
{
    Help = 100,
    Settings = 110,

    GoToResources = 200,
    GoToConsoleLogs = 210,
    GoToStructuredLogs = 220,
    GoToTraces = 230,
    GoToMetrics = 240,

    ToggleOrientation = 300,
    ClosePanel = 310,
    ResetPanelSize = 320,
    IncreasePanelSize = 330,
    DecreasePanelSize = 340,

    ToggleTerminalDock = 400,
}
