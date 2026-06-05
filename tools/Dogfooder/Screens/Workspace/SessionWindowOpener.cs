// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Opens or focuses the floating window associated with a
/// <see cref="DogfoodSession"/>. The window's content lambda dispatches to
/// <see cref="SessionConfigContent"/> or <see cref="SessionTerminalContent"/>
/// based on whether the session has been Continue'd yet, so the same window
/// instance can transition from configure-mode to terminal-mode without the
/// caller doing anything other than mutating <see cref="DogfoodSession.Plan"/>
/// and invalidating the app.
/// </summary>
internal static class SessionWindowOpener
{
    // Slight offset per opened window so the second-and-later windows aren't
    // stacked exactly on top of the first. Wraps so we don't drift off
    // screen in long sessions.
    private const int CascadeOffsetSize = 2;
    private const int CascadeWrap = 8;

    // Terminal area is 80x24 inside; chrome adds 2 cols + 3 rows for the
    // borders and title bar (matches the constants used in the Hex1b
    // WindowingDemo sample so the embedded PTY size lines up with our
    // explicit WithDimensions(80, 24) call in SessionTerminalContent).
    private const int WindowContentColumns = 80;
    private const int WindowContentRows = 24;
    private const int WindowChromeWidth = 2;
    private const int WindowChromeHeight = 3;

    public static void OpenOrFocus(
        WindowManager windows,
        DogfoodSession session,
        AppState state,
        IDogfoodSessionPreparer preparer,
        SessionWindowRegistry windowRegistry,
        SessionTerminalRegistry terminalRegistry,
        DogfoodingNuGetServerRegistry nuGetRegistry,
        Scenarios.DogfoodScenarioRegistry scenarioRegistry)
    {
        if (windowRegistry.TryGet(session.Id, out var existing) && windows.IsOpen(existing))
        {
            windows.BringToFront(existing);
            state.SetStatus($"Focused: {session.Name}");
            return;
        }

        var cascadeIndex = windowRegistry.OpenCount % CascadeWrap;

        var window = windows.Window(w => BuildBody(w, session, state, preparer, terminalRegistry, nuGetRegistry, scenarioRegistry))
            .Title($"Session: {session.Name}")
            .Size(WindowContentColumns + WindowChromeWidth, WindowContentRows + WindowChromeHeight)
            .Position(new WindowPositionSpec(
                WindowPosition.Center,
                OffsetX: cascadeIndex * CascadeOffsetSize,
                OffsetY: cascadeIndex))
            .Resizable(
                minWidth: 50,
                minHeight: 12)
            .OnClose(() =>
            {
                // Tear down the PTY and per-session NuGet server first so
                // their background tasks observe cancellation before the
                // registry forgets about them; then forget the window
                // mapping so a re-open creates fresh.
                terminalRegistry.Dispose(session.Id);
                nuGetRegistry.Dispose(session.Id);
                windowRegistry.Unregister(session.Id);
                state.SetStatus($"Closed: {session.Name}");
            });

        windowRegistry.Register(session.Id, window);
        windows.Open(window);
        state.SetStatus($"Opened: {session.Name}");
    }

    private static Hex1bWidget BuildBody(
        WindowContentContext<Hex1bWidget> w,
        DogfoodSession session,
        AppState state,
        IDogfoodSessionPreparer preparer,
        SessionTerminalRegistry terminalRegistry,
        DogfoodingNuGetServerRegistry nuGetRegistry,
        Scenarios.DogfoodScenarioRegistry scenarioRegistry)
    {
        // Three-way dispatch: preparing → live build log; planned → terminal
        // (in tabs when there's a proxy or build log); not yet planned →
        // the configuration form.
        if (session.Status == SessionStatus.Preparing)
        {
            return SessionPreparationContent.Build(w, session, state);
        }

        if (session.Plan is null)
        {
            return SessionConfigContent.Build(w, session, state, preparer, nuGetRegistry, scenarioRegistry);
        }

        // Single terminal? Skip the tab chrome — saves a row of vertical
        // space in the common no-proxy case. Read flags from the scenario
        // plan stamped onto the session at preparation time so we agree with
        // whatever the preparer actually configured (the session's free-form
        // input dictionary doesn't tell us this directly).
        var hasNuGet = session.ScenarioPlan?.UseLocalNuGetProxy == true;
        var hasBuildLog = session.ScenarioPlan?.BuildPackagesBeforeLaunch == true && session.Preparation is not null;
        if (!hasNuGet && !hasBuildLog)
        {
            return SessionTerminalContent.Build(w, session, terminalRegistry);
        }

        return w.TabPanel(tp =>
        {
            var tabs = new List<TabItemWidget>
            {
                tp.Tab("Terminal", t => new[] { SessionTerminalContent.Build(t, session, terminalRegistry) }),
            };
            if (hasNuGet)
            {
                var count = session.NuGetTraffic?.Events.Count ?? 0;
                var label = count > 0 ? $"NuGet ({count})" : "NuGet";
                tabs.Add(tp.Tab(label, t => new[] { SessionNuGetTrafficContent.Build(t, session, nuGetRegistry) }));
            }
            if (hasBuildLog)
            {
                tabs.Add(tp.Tab("Build log", t => new[] { SessionPreparationContent.Build(t, session, state) }));
            }
            return tabs;
        });
    }
}
