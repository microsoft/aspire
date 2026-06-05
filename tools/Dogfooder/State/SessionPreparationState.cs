// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b.Documents;
using Hex1b.Widgets;

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
    private EditorState? _logEditor;

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
            // Invalidate any cached read-only editor so a subsequent
            // GetOrCreateLogEditor() picks up the new content. We don't
            // mutate the existing editor in place — it's only ever
            // materialized at terminal phases (Complete/Failed) when log
            // appends should have stopped.
            _logEditor = null;
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

    /// <summary>
    /// Lazily build (and cache) a read-only Hex1b EditorState containing the
    /// full log. Used by <c>SessionPreparationContent</c> after the build has
    /// reached a terminal phase so the user can scroll and copy from the log
    /// instead of staring at a static tail. Re-created on the next call if
    /// more lines arrive (see <see cref="Append"/>).
    /// </summary>
    public EditorState GetOrCreateLogEditor()
    {
        lock (_gate)
        {
            if (_logEditor is not null)
            {
                return _logEditor;
            }

            var text = _log.Count == 0 ? string.Empty : string.Join("\n", _log);
            var doc = new Hex1bDocument(text);
            _logEditor = new EditorState(doc) { IsReadOnly = true };
            return _logEditor;
        }
    }
}
