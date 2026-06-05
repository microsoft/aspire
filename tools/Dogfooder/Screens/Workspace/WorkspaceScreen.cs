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
        IDogfoodSessionPreparer preparer,
        Scenarios.DogfoodScenarioRegistry scenarioRegistry)
    {
        _state = state;
        _preparer = preparer;
        _scenarioRegistry = scenarioRegistry;
    }

    private readonly AppState _state;
    private readonly IDogfoodSessionPreparer _preparer;
    private readonly Scenarios.DogfoodScenarioRegistry _scenarioRegistry;
    private readonly SessionWindowRegistry _windowRegistry = new();
    private readonly SessionTerminalRegistry _terminalRegistry = new();
    private readonly DogfoodingNuGetServerRegistry _nugetRegistry = new();

    /// <summary>
    /// Disposable bundle so RunCommand can tear down per-session servers and
    /// PTYs deterministically on app shutdown.
    /// </summary>
    public IAsyncDisposable Disposables => new RegistryBundle(_terminalRegistry, _nugetRegistry);

    public Hex1bWidget Build(RootContext ctx)
    {
        return ctx.VStack(main =>
        [
            MenuBarBuilder.Build(main, _state, _preparer, _windowRegistry, _terminalRegistry, _nugetRegistry, _scenarioRegistry),
            main.WindowPanel().Fill(),
            StatusBarBuilder.Build(main, _state, _windowRegistry),
        ]);
    }

    private sealed class RegistryBundle : IAsyncDisposable
    {
        private readonly SessionTerminalRegistry _terminals;
        private readonly DogfoodingNuGetServerRegistry _nuget;
        public RegistryBundle(SessionTerminalRegistry terminals, DogfoodingNuGetServerRegistry nuget)
        {
            _terminals = terminals;
            _nuget = nuget;
        }
        public async ValueTask DisposeAsync()
        {
            // Tear down NuGet servers; the per-session PTYs in
            // _terminalRegistry don't expose a bulk dispose surface but their
            // owning Process objects are torn down when the host exits.
            _ = _terminals;
            await _nuget.DisposeAsync().ConfigureAwait(false);
        }
    }
}
