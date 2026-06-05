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
        SessionTerminalRegistry terminalRegistry,
        DogfoodingNuGetServerRegistry nuGetRegistry,
        Scenarios.DogfoodScenarioRegistry scenarioRegistry)
    {
        return ctx.MenuBar(m =>
        [
            m.Menu("File", m =>
            [
                m.MenuItem("New Session").OnActivated(e =>
                {
                    var ordinal = state.Sessions.Sessions.Count + 1;
                    var session = state.Sessions.Add(
                        name: $"session-{ordinal}",
                        config: DogfoodSessionConfig.ForScenario(scenarioRegistry.Default.Id));
                    SessionWindowOpener.OpenOrFocus(
                        e.Windows,
                        session,
                        state,
                        preparer,
                        windowRegistry,
                        terminalRegistry,
                        nuGetRegistry,
                        scenarioRegistry);
                }),
                m.Separator(),
                m.MenuItem("Quit").OnActivated(e => e.Context.RequestStop()),
            ]),
            m.Menu("Sessions", m =>
            {
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
                            terminalRegistry,
                            nuGetRegistry,
                            scenarioRegistry)))
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
