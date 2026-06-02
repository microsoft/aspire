// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.State;

/// <summary>
/// Which top-level screen is currently presented.
/// </summary>
internal enum AppPhase
{
    Validation,
    Main,
}

/// <summary>
/// Right-pane mode in the main screen — depends on whether the user is
/// configuring a new session or interacting with a running one.
/// </summary>
internal enum DetailMode
{
    None,
    Config,
    Terminal,
}

/// <summary>
/// Top-level app state. Everything the UI needs to render is reachable from
/// here. Hex1b stays out of this type and its dependencies so the future
/// <c>self-test</c> command (which runs the same state machine without a TTY)
/// can drive it directly.
/// </summary>
internal sealed class AppState
{
    public AppState(
        ChangeNotifier notifier,
        EnvironmentValidationState validation,
        DogfoodSessionStore sessions)
    {
        _notifier = notifier;
        Validation = validation;
        Sessions = sessions;
    }

    private readonly ChangeNotifier _notifier;

    public ChangeNotifier Notifier => _notifier;

    public AppPhase Phase { get; private set; } = AppPhase.Validation;

    public EnvironmentValidationState Validation { get; }
    public DogfoodSessionStore Sessions { get; }

    /// <summary>
    /// Currently selected session in the left pane, or null when the list is
    /// empty / nothing has been picked yet.
    /// </summary>
    public DogfoodSession? ActiveSession { get; private set; }

    public DetailMode DetailMode { get; private set; } = DetailMode.None;

    /// <summary>
    /// The draft config the user is editing in the right pane when
    /// <see cref="DetailMode"/> is <see cref="DetailMode.Config"/>. Lives on
    /// app state (not in the form widget) so the form stays a pure render of
    /// state and the future self-test can assert/mutate it.
    /// </summary>
    public DogfoodSessionConfig DraftConfig { get; private set; } = DogfoodSessionConfig.Empty;

    public string DraftSessionName { get; private set; } = "untitled";

    public void EnterMainScreen()
    {
        Phase = AppPhase.Main;
        _notifier.Notify();
    }

    public void BeginNewSession()
    {
        ActiveSession = null;
        DetailMode = DetailMode.Config;
        DraftConfig = DogfoodSessionConfig.Empty;
        DraftSessionName = $"session-{Sessions.Sessions.Count + 1}";
        _notifier.Notify();
    }

    public void SelectSession(DogfoodSession session)
    {
        ActiveSession = session;
        DetailMode = session.Status == SessionStatus.Idle
            ? DetailMode.Config
            : DetailMode.Terminal;
        DraftConfig = session.Config;
        DraftSessionName = session.Name;
        _notifier.Notify();
    }

    public void UpdateDraftConfig(DogfoodSessionConfig config)
    {
        DraftConfig = config;
        _notifier.Notify();
    }

    public void UpdateDraftName(string name)
    {
        DraftSessionName = name;
        _notifier.Notify();
    }

    public void SwitchToTerminal(DogfoodSession session)
    {
        ActiveSession = session;
        DetailMode = DetailMode.Terminal;
        _notifier.Notify();
    }
}
