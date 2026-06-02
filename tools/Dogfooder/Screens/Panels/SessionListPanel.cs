// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Panels;

/// <summary>
/// Left-pane session list. Pure view over <see cref="DogfoodSessionStore"/>;
/// selection drives <see cref="AppState.SelectSession(DogfoodSession)"/>.
/// </summary>
internal static class SessionListPanel
{
    public static Hex1bWidget Build(WidgetContext<VStackWidget> ctx, AppState state)
    {
        var sessions = state.Sessions.Sessions;

        // Build the displayable item list — we fall back to a placeholder
        // when empty so the list widget still has a defined selection target
        // and doesn't render as a void.
        var labels = sessions.Count == 0
            ? (IReadOnlyList<string>)["(no sessions yet)"]
            : sessions.Select(s => $"{s.Name}  [{s.Status}]").ToList();

        return ctx.Border(b =>
        [
            b.Text("Sessions"),
            b.Button("[+] Add").OnClick(_ => state.BeginNewSession()),
            b.Separator(),
            b.List(labels).OnSelectionChanged(e =>
            {
                if (sessions.Count == 0)
                {
                    return;
                }
                var idx = e.SelectedIndex;
                if (idx >= 0 && idx < sessions.Count)
                {
                    state.SelectSession(sessions[idx]);
                }
            }),
        ]);
    }
}
