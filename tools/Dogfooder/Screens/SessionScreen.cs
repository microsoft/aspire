// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Scenarios;
using Aspire.Dogfooder.Screens.Workspace;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens;

/// <summary>
/// Full-screen, single-session Dogfooder screen. Replaces the prior MDI
/// window manager: one process owns exactly one dogfooding session. If you
/// want a second session run a second Dogfooder (typically in a second
/// worktree).
/// </summary>
/// <remarks>
/// Lifecycle:
/// <list type="number">
///   <item>Config phase — scenario picker + per-scenario inputs.</item>
///   <item>Preparing — streaming build / proxy startup log.</item>
///   <item>Running — terminal (in a TabPanel alongside NuGet/Build-log tabs when relevant).</item>
/// </list>
/// State lives on a single in-memory <see cref="DogfoodSession"/>; nothing
/// is persisted between launches.
/// </remarks>
internal sealed class SessionScreen
{
    public SessionScreen(
        AppState state,
        DogfoodScenarioRegistry scenarioRegistry,
        IDogfoodSessionPreparer preparer)
    {
        _state = state;
        _scenarioRegistry = scenarioRegistry;
        _preparer = preparer;
        _session = new DogfoodSession(
            id: "dogfooder",
            name: "dogfooder",
            config: DogfoodSessionConfig.ForScenario(scenarioRegistry.Default.Id));
    }

    private readonly AppState _state;
    private readonly DogfoodScenarioRegistry _scenarioRegistry;
    private readonly IDogfoodSessionPreparer _preparer;
    private readonly DogfoodSession _session;
    private readonly SessionTerminalRegistry _terminals = new();
    private DogfoodingNuGetServer? _nugetServer;

    /// <summary>
    /// Resources to dispose at app shutdown. RunCommand pulls this once and
    /// awaits it during the post-Hex1b-loop cleanup so the per-session NuGet
    /// server and PTY are torn down deterministically rather than waiting
    /// for the process to exit.
    /// </summary>
    public IAsyncDisposable Disposables => new Bundle(this);

    public Hex1bWidget Build(RootContext ctx)
    {
        return ctx.VStack(v =>
        [
            BuildInfoBar(v),
            BuildBody(v),
        ]);
    }

    private Hex1bWidget BuildInfoBar(WidgetContext<VStackWidget> ctx)
    {
        // Single-line info bar. Hex1b doesn't expose a dedicated status-bar
        // widget at the root; a TextBlock with two formatted segments works
        // and keeps the layout cheap (no extra widget tree).
        var scenario = _scenarioRegistry.GetOrDefault(_session.Config.ScenarioId);
        var line = $"  Aspire Dogfooder  ·  {scenario.DisplayName}  ·  {_state.StatusMessage}";
        return ctx.Text(line);
    }

    private Hex1bWidget BuildBody(WidgetContext<VStackWidget> ctx)
    {
        // Three-way dispatch mirrors the original window opener but anchored
        // directly to the root context (no window indirection).
        if (_session.Status == SessionStatus.Preparing)
        {
            return SessionPreparationContent.Build(ctx, _session, _state);
        }

        if (_session.Plan is null)
        {
            return SessionConfigContent.Build(
                ctx, _session, _state, _scenarioRegistry,
                onContinue: () => _ = RunPreparationAsync());
        }

        var hasNuGet = _session.ScenarioPlan?.UseLocalNuGetProxy == true;
        var hasBuildLog = _session.ScenarioPlan?.BuildPackagesBeforeLaunch == true && _session.Preparation is not null;
        if (!hasNuGet && !hasBuildLog)
        {
            return SessionTerminalContent.Build(ctx, _session, _terminals);
        }

        return ctx.TabPanel(tp =>
        {
            var tabs = new List<TabItemWidget>
            {
                tp.Tab("Terminal", t => new[] { SessionTerminalContent.Build(t, _session, _terminals) }),
            };
            if (hasNuGet)
            {
                var count = _session.NuGetTraffic?.Events.Count ?? 0;
                var label = count > 0 ? $"NuGet ({count})" : "NuGet";
                tabs.Add(tp.Tab(label, t => new[] { SessionNuGetTrafficContent.Build(t, _session, _nugetServer) }));
            }
            if (hasBuildLog)
            {
                tabs.Add(tp.Tab("Build log", t => new[] { SessionPreparationContent.Build(t, _session, _state) }));
            }
            return tabs;
        });
    }

    private async Task RunPreparationAsync()
    {
        try
        {
            _session.Status = SessionStatus.Preparing;
            _state.SetStatus("Preparing …");
            var result = await _preparer.PrepareAsync(_session, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                _state.SetStatus("Preparation failed (see log).");
                _state.Notifier.Notify();
                return;
            }
            _nugetServer = result.NuGetServer;
            _session.Status = SessionStatus.Idle;
            _state.SetStatus("Launching terminal …");
            _state.Notifier.Notify();
        }
        catch (Exception ex)
        {
            _session.Preparation?.SetPhase(SessionPreparationState.Phase.Failed, ex.Message);
            _state.SetStatus($"Preparation crashed: {ex.Message}");
            _state.Notifier.Notify();
        }
    }

    private sealed class Bundle : IAsyncDisposable
    {
        private readonly SessionScreen _owner;
        public Bundle(SessionScreen owner) => _owner = owner;
        public async ValueTask DisposeAsync()
        {
            // Tear down the embedded PTY first (cancels its run-loop) then
            // the NuGet proxy. Per-session PTYs don't expose a bulk-dispose
            // path here; they're cleaned up by SessionTerminalRegistry.Dispose.
            _owner._terminals.Dispose(_owner._session.Id);
            if (_owner._nugetServer is { } server)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
