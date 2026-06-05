// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.State;

/// <summary>
/// Runtime lifecycle status of a single dogfooding session.
/// </summary>
internal enum SessionStatus
{
    /// <summary>Created but never launched (or relaunched after exit).</summary>
    Idle,

    /// <summary>Preparer is currently spinning up the embedded terminal.</summary>
    Preparing,

    /// <summary>Embedded terminal is running; user is interactively dogfooding.</summary>
    Running,

    /// <summary>The shell process has exited.</summary>
    Exited,
}

/// <summary>
/// A configured dogfooding session — the persisted <see cref="DogfoodSessionConfig"/>
/// plus the in-memory <see cref="Status"/> that tracks whether the embedded
/// terminal is live. Mutable because the status flips as the user interacts
/// with the terminal panel; raises <see cref="ChangeNotifier.Notify"/> via the
/// owning <c>DogfoodSessionStore</c>.
/// </summary>
internal sealed class DogfoodSession
{
    public DogfoodSession(string id, string name, DogfoodSessionConfig config)
    {
        Id = id;
        Name = name;
        Config = config;
    }

    /// <summary>Stable opaque id. Used as the persistence key.</summary>
    public string Id { get; }

    /// <summary>Human-facing label shown in the session list.</summary>
    public string Name { get; set; }

    public DogfoodSessionConfig Config { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Idle;

    /// <summary>
    /// Cached environment plan produced by the preparer. Null until the
    /// session is launched at least once. Kept on the session (rather than
    /// recomputed on every render) so the terminal panel can replay the
    /// same shell commands consistently if the user re-selects the session.
    /// </summary>
    public Services.SessionEnvironmentPlan? Plan { get; set; }

    /// <summary>
    /// Cached scenario plan produced by the chosen <c>IDogfoodScenario</c>
    /// for this session's inputs. Set alongside <see cref="Plan"/> by the
    /// preparer so window-level dispatch (which tabs to show, etc.) can
    /// consult the same values that drove preparation.
    /// </summary>
    public Scenarios.ScenarioPlan? ScenarioPlan { get; set; }

    /// <summary>
    /// Live preparation log while the preparer is running (build script
    /// output + NuGet server startup). Created lazily when Continue is
    /// clicked; remains accessible after preparation finishes so the user
    /// can review the log even after the terminal has launched.
    /// </summary>
    public SessionPreparationState? Preparation { get; set; }

    /// <summary>
    /// Live <c>DogfoodingNuGetServer</c> traffic snapshot powering the NuGet
    /// analyzer tab. Null when the session was started without
    /// <c>UseLocalNuGetProxy</c>.
    /// </summary>
    public NuGetTrafficState? NuGetTraffic { get; set; }

    /// <summary>
    /// Set by <c>SessionTerminalContent.GetOrCreateTerminal</c> when the
    /// background PTY/shell task throws on startup or mid-run (e.g. when
    /// the user's <c>DOGFOODER_SHELL</c> override points at a missing
    /// binary). The terminal tab renders this in place of the (empty)
    /// terminal view so the user can see the actual failure instead of
    /// staring at a blank black rectangle.
    /// </summary>
    public string? TerminalCrashMessage { get; set; }

    /// <summary>
    /// Per-session scratch workspace owning every file the run produces
    /// (build log, copied <c>.nupkg</c> packages, NuGet global-packages
    /// cache, terminal cwd, <c>dogfood.json</c> manifest). Created by the
    /// preparer at the start of <c>PrepareAsync</c>. Null until the user
    /// hits Continue for the first time.
    /// </summary>
    public SessionWorkspace? Workspace { get; set; }
}
