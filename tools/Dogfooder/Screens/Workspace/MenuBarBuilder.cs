// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Builds the top menu bar for the workspace screen. Pure: every callback
/// closes over the same shared state/services that the rest of the
/// workspace uses, so adding a new menu item is a one-liner here and a
/// state mutation elsewhere.
/// </summary>
internal static class MenuBarBuilder
{
    public static Hex1bWidget Build(
        WidgetContext<VStackWidget> ctx,
        AppState state,
        IDogfoodSessionPreparer preparer,
        SessionWindowRegistry windowRegistry,
        SessionTerminalRegistry terminalRegistry)
    {
        return ctx.MenuBar(m =>
        [
            m.Menu("File", m =>
            [
                m.MenuItem("New Session").OnActivated(e =>
                {
                    // Create the session up front with a placeholder name so
                    // the user can immediately edit it in the opened window.
                    // The session lives in the store from this moment on;
                    // pressing Cancel inside the window removes it again.
                    var ordinal = state.Sessions.Sessions.Count + 1;
                    var session = state.Sessions.Add(
                        name: $"session-{ordinal}",
                        config: DogfoodSessionConfig.Empty);
                    SessionWindowOpener.OpenOrFocus(
                        e.Windows,
                        session,
                        state,
                        preparer,
                        windowRegistry,
                        terminalRegistry);
                }),
                m.Separator(),
                m.MenuItem("Quit").OnActivated(e => e.Context.RequestStop()),
            ]),
            m.Menu("Sessions", m =>
            {
                // Build the submenu dynamically from the store. We can't
                // hand the menu builder an empty list (it would render an
                // unclickable header), so when there are no sessions we
                // show a disabled-looking placeholder item.
                var sessions = state.Sessions.Sessions;
                if (sessions.Count == 0)
                {
                    return new IMenuChild[]
                    {
                        m.MenuItem("(no sessions yet)").OnActivated(_ => { }),
                    };
                }

                return sessions
                    .Select(s => (IMenuChild)m.MenuItem($"{s.Name}  [{s.Status}]")
                        .OnActivated(e => SessionWindowOpener.OpenOrFocus(
                            e.Windows,
                            s,
                            state,
                            preparer,
                            windowRegistry,
                            terminalRegistry)))
                    .ToArray();
            }),
            m.Menu("Help", m =>
            [
                m.MenuItem("About").OnActivated(e => OpenAboutWindow(e.Windows, state)),
            ]),
        ]);
    }

    private static void OpenAboutWindow(Hex1b.WindowManager windows, AppState state)
    {
        var about = windows.Window(w => w.VStack(v =>
        [
            v.Text(""),
            v.Text("  Aspire Dogfooder"),
            v.Text("  Hex1b TUI test rig for the CLI identity sidecar."),
            v.Text(""),
            v.Text("  ASPIRE_CLI_CHANNEL / VERSION / COMMIT /"),
            v.Text("  NUGET_SERVICE_INDEX overrides per dogfooding session,"),
            v.Text("  typed visibly into the embedded shell."),
            v.Text(""),
            v.HStack(h =>
            [
                h.Text("  "),
                h.Button("Close").OnClick(ev => ev.Windows.Close(w.Window)),
            ]),
        ]))
        .Title("About")
        .Size(56, 14);

        windows.Open(about);
        state.SetStatus("Opened: About");
    }
}
