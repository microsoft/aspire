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
/// Top-level app state for the single-session Dogfooder. There is exactly one
/// dogfooding session per process — if you want a second one, launch a second
/// Dogfooder (typically in a second worktree). No persistence: each launch
/// starts fresh.
/// </summary>
internal sealed class AppState
{
    public AppState(
        ChangeNotifier notifier,
        EnvironmentValidationState validation)
    {
        _notifier = notifier;
        Validation = validation;
    }

    private readonly ChangeNotifier _notifier;

    public ChangeNotifier Notifier => _notifier;

    public AppPhase Phase { get; private set; } = AppPhase.Validation;

    public EnvironmentValidationState Validation { get; }

    /// <summary>
    /// Free-form status string surfaced in the info bar. Mutating callers
    /// must follow up with <c>Notifier.Notify()</c> to push the update
    /// through to the render loop.
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
