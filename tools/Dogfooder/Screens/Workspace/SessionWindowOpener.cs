// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;

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
        SessionTerminalRegistry terminalRegistry)
    {
        if (windowRegistry.TryGet(session.Id, out var existing) && windows.IsOpen(existing))
        {
            windows.BringToFront(existing);
            state.SetStatus($"Focused: {session.Name}");
            return;
        }

        var cascadeIndex = windowRegistry.OpenCount % CascadeWrap;

        var window = windows.Window(w =>
                session.Plan is null
                    ? SessionConfigContent.Build(w, session, state, preparer)
                    : SessionTerminalContent.Build(w, session, terminalRegistry))
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
                // Tear down the PTY first so its background tasks observe
                // cancellation before the registry forgets about them; then
                // forget the window mapping so a re-open creates fresh.
                terminalRegistry.Dispose(session.Id);
                windowRegistry.Unregister(session.Id);
                state.SetStatus($"Closed: {session.Name}");
            });

        windowRegistry.Register(session.Id, window);
        windows.Open(window);
        state.SetStatus($"Opened: {session.Name}");
    }
}
