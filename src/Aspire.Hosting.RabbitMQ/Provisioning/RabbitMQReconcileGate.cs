// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Thread-safe coordinator that ensures at most one reconcile is in flight per topology child at any time.
/// </summary>
/// <remarks>
/// <para>
/// The parent server's <c>ResourceReadyEvent</c> reconcile is authoritative: when the parent (broker container)
/// restarts, its topology state is torn down, so any reconcile currently in flight — whether from a prior ready
/// event OR from a manual Start/Stop/Restart command — must be cancelled and a fresh reconcile started from
/// scratch. This gate is the single point that enforces that invariant.
/// </para>
/// <para>
/// Both <c>WithRabbitMQParentLifecycle</c> (event-driven) and <c>WithRabbitMQTopologyCommands</c>
/// (command-driven) share the SAME gate instance stored on the child resource, so a parent ready event can
/// cancel a command's in-flight reconcile and vice-versa.
/// </para>
/// </remarks>
internal sealed class RabbitMQReconcileGate
{
    private readonly object _lock = new();
    private CancellationTokenSource? _current;

    /// <summary>
    /// Cancels and disposes any existing in-flight reconcile, then creates a fresh
    /// <see cref="CancellationTokenSource"/> linked to <paramref name="linkedTo"/> and returns its token.
    /// </summary>
    /// <remarks>
    /// Call this at the start of every reconcile (both event-driven and command-driven). The returned token
    /// will be cancelled if <paramref name="linkedTo"/> is cancelled (e.g. app shutdown) OR if a subsequent
    /// call to <see cref="BeginNew"/> or <see cref="CancelCurrent"/> supersedes this reconcile.
    /// </remarks>
    /// <param name="linkedTo">An outer token (e.g. the event's cancellation token) to link into the new CTS.</param>
    /// <returns>The token for the new reconcile; will be cancelled when this reconcile is superseded.</returns>
    public RabbitMQReconcileLease BeginNew(CancellationToken linkedTo)
    {
        CancellationTokenSource next;
        CancellationToken token;
        CancellationTokenSource? previous;

        lock (_lock)
        {
            previous = _current;
            next = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
            // Capture the token INSIDE the lock, before releasing. Reading next.Token after the lock is racy:
            // a concurrent BeginNew could supersede this reconcile and dispose its CTS, and reading Token on a
            // disposed CancellationTokenSource throws ObjectDisposedException. Capturing here decouples the
            // returned token from the CTS lifetime.
            token = next.Token;
            _current = next;
        }

        // Only CANCEL the superseded reconcile here — never dispose it. The superseded reconcile owns and
        // disposes its own CTS via its lease once it observes cancellation and unwinds. Disposing another
        // caller's CTS here is exactly the race that caused ObjectDisposedException: caller A installs its CTS,
        // caller B supersedes and disposes it, then A reads its now-disposed token. Cancel outside the lock so
        // registered callbacks do not run while holding it.
        previous?.Cancel();

        return new RabbitMQReconcileLease(this, next, token);
    }

    /// <summary>
    /// Cancels the current in-flight reconcile, if any. Used when the parent server stops or when a Stop
    /// command supersedes an in-flight reconcile before issuing a delete.
    /// </summary>
    public void CancelCurrent()
    {
        CancellationTokenSource? current;

        lock (_lock)
        {
            current = _current;
        }

        // Cancel outside the lock; the CTS is still valid even if BeginNew replaces _current concurrently,
        // because we hold a local reference and CancellationTokenSource.Cancel() is thread-safe. We do not
        // dispose here — the owning reconcile disposes its own CTS via its lease.
        current?.Cancel();
    }

    // Clears _current if it still points at 'owned'. Called by a lease on dispose so a stale reference to a
    // completed reconcile's CTS is not left behind. Does nothing if a newer reconcile has already superseded
    // this one (in that case the newer reconcile owns _current).
    private void ClearIfCurrent(CancellationTokenSource owned)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_current, owned))
            {
                _current = null;
            }
        }
    }

    // A lease over a single reconcile's CancellationTokenSource. The reconcile owns this lease and disposes it
    // when finished (e.g. after StartCore completes), which disposes the underlying CTS exactly once. The gate
    // itself never disposes a CTS, so there is no cross-caller disposal race.
    internal sealed class RabbitMQReconcileLease : IDisposable
    {
        private readonly RabbitMQReconcileGate _gate;
        private readonly CancellationTokenSource _cts;
        private int _disposed;

        internal RabbitMQReconcileLease(RabbitMQReconcileGate gate, CancellationTokenSource cts, CancellationToken token)
        {
            _gate = gate;
            _cts = cts;
            Token = token;
        }

        // The cancellation token for this reconcile. Captured before the lease was handed out, so it is safe to
        // read even after the CTS is disposed.
        public CancellationToken Token { get; }

        public void Dispose()
        {
            // Guard against double-dispose so the CTS is disposed exactly once.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _gate.ClearIfCurrent(_cts);
            _cts.Dispose();
        }
    }
}
