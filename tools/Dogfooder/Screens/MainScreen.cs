// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Screens.Panels;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens;

/// <summary>
/// Main screen: a horizontal splitter with the session list on the left and
/// either the config form or the embedded terminal on the right depending on
/// <see cref="AppState.DetailMode"/>.
/// </summary>
internal static class MainScreen
{
    public static Hex1bWidget Build(
        RootContext ctx,
        AppState state,
        IDogfoodSessionPreparer preparer,
        IPrCatalog prCatalog)
    {
        // 32-column left pane is enough for typical session names and the
        // "[+] Add" button without crowding the right-pane form/terminal.
        return ctx.HSplitter(
            left => [SessionListPanel.Build(left, state)],
            right => [BuildDetail(right, state, preparer, prCatalog)],
            leftWidth: 32);
    }

    private static Hex1bWidget BuildDetail(
        WidgetContext<VStackWidget> ctx,
        AppState state,
        IDogfoodSessionPreparer preparer,
        IPrCatalog prCatalog) =>
        state.DetailMode switch
        {
            DetailMode.Config => SessionConfigPanel.Build(ctx, state, preparer, prCatalog),
            DetailMode.Terminal => SessionTerminalPanel.Build(ctx, state),
            _ => ctx.Text("Select a session on the left, or [+] Add a new one."),
        };
}
