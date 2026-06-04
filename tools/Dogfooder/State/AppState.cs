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
/// Top-level app state. Everything the UI needs to render is reachable from
/// here. Hex1b stays out of this type and its dependencies so the future
/// <c>self-test</c> command (which runs the same state machine without a TTY)
/// can drive it directly.
/// </summary>
/// <remarks>
/// Window-local concerns (which window is focused, which session is being
/// edited in a window) deliberately do NOT live here. With the multi-window
/// workspace layout each session window owns its own draft and lifecycle;
/// AppState's job is just the phase machine and the bag of sub-states.
/// </remarks>
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
    /// Free-form status string surfaced in the workspace footer. Mutating
    /// callers must follow up with <c>Notifier.Notify()</c> to push the
    /// update through to the render loop.
    /// </summary>
    public string StatusMessage { get; set; } = "Ready";

    public void EnterMainScreen()
    {
        Phase = AppPhase.Main;
        _notifier.Notify();
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
        _notifier.Notify();
    }
}
