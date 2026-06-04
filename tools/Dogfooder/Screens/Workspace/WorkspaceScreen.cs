// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Root composition for the main (post-validation) workspace screen. Lays
/// out a top menu bar, the floating-window MDI area, and a footer status bar.
/// </summary>
/// <remarks>
/// Stateful: owns the <see cref="SessionWindowRegistry"/> and
/// <see cref="SessionTerminalRegistry"/> for the lifetime of the workspace.
/// Instantiated once by <c>RunCommand</c> rather than re-created on every
/// render so the registries survive across frames (Hex1b re-invokes
/// content callbacks every frame; spinning fresh registries each time would
/// orphan every running PTY).
/// </remarks>
internal sealed class WorkspaceScreen
{
    public WorkspaceScreen(
        AppState state,
        IDogfoodSessionPreparer preparer)
    {
        _state = state;
        _preparer = preparer;
    }

    private readonly AppState _state;
    private readonly IDogfoodSessionPreparer _preparer;
    private readonly SessionWindowRegistry _windowRegistry = new();
    private readonly SessionTerminalRegistry _terminalRegistry = new();

    public Hex1bWidget Build(RootContext ctx)
    {
        return ctx.VStack(main =>
        [
            MenuBarBuilder.Build(main, _state, _preparer, _windowRegistry, _terminalRegistry),
            main.WindowPanel().Fill(),
            StatusBarBuilder.Build(main, _state, _windowRegistry),
        ]);
    }
}
