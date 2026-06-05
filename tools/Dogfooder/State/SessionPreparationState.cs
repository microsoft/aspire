// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.State;

/// <summary>
/// Mutable per-session state observed by <c>SessionPreparationContent</c>
/// while the preparer is running (build script + NuGet server startup). Each
/// step appends progress lines and an overall phase string; the preparation
/// window subscribes to <see cref="OnChanged"/> via the shared
/// <see cref="ChangeNotifier"/> so the user sees build output streaming in
/// real time.
/// </summary>
internal sealed class SessionPreparationState
{
    public enum Phase
    {
        Pending,
        Building,
        StartingProxy,
        Complete,
        Failed,
    }

    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private Phase _phase = Phase.Pending;

    /// <summary>All log lines appended so far, in order.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return _log.ToArray();
            }
        }
    }

    public Phase CurrentPhase
    {
        get { lock (_gate) { return _phase; } }
    }

    public string? FailureReason { get; private set; }

    public event Action? OnChanged;

    public void Append(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
        }
        OnChanged?.Invoke();
    }

    public void SetPhase(Phase phase, string? failureReason = null)
    {
        lock (_gate)
        {
            _phase = phase;
            FailureReason = failureReason;
        }
        OnChanged?.Invoke();
    }
}
