// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal sealed class GitOperationTimeout : IDisposable
{
    private static readonly AsyncLocal<GitOperationTimeout?> s_current = new();
    private readonly GitOperationTimeout? _previous = s_current.Value;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private TimeSpan _remaining;
    private long _started;
    private int _pauses;

    public CancellationToken Token => _linked.Token;
    public bool IsCancellationRequested => _deadline.IsCancellationRequested;

    public GitOperationTimeout(TimeSpan budget, TimeProvider clock, CancellationToken cancellationToken)
    {
        _clock = clock;
        _remaining = budget;
        _started = clock.GetTimestamp();
        _deadline = new CancellationTokenSource(budget, clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        s_current.Value = this;
    }

    // The acquisition owns the budget across multiple Git commands. Flow that scope
    // into the askpass listener without adding timeout parameters to the Git protocol.
    public static IDisposable? PauseCurrent() => s_current.Value?.Pause();

    private IDisposable Pause()
    {
        var previous = _previous?.Pause();
        lock (_gate)
        {
            if (_pauses++ == 0)
            {
                _remaining -= _clock.GetElapsedTime(_started);
                _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        return new PauseScope(this, previous);
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (--_pauses == 0)
            {
                _started = _clock.GetTimestamp();
                _deadline.CancelAfter(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero);
            }
        }
    }

    public void Dispose()
    {
        s_current.Value = _previous;
        _linked.Dispose();
        _deadline.Dispose();
    }

    private sealed class PauseScope(GitOperationTimeout owner, IDisposable? previous) : IDisposable
    {
        public void Dispose()
        {
            owner.Resume();
            previous?.Dispose();
        }
    }
}
