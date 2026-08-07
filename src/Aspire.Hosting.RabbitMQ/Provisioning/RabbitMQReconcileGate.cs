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
    public CancellationToken BeginNew(CancellationToken linkedTo)
    {
        CancellationTokenSource next;
        CancellationTokenSource? previous;

        lock (_lock)
        {
            previous = _current;
            next = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
            _current = next;
        }

        // Cancel and dispose outside the lock to avoid holding the lock during potentially slow cleanup.
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return next.Token;
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
        // because we hold a local reference and CancellationTokenSource.Cancel() is thread-safe.
        current?.Cancel();
    }
}
