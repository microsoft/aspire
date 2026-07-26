// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Backend;

/// <summary>
/// Terminates in-flight streaming hub invocations when the browser session ends.
/// </summary>
/// <remarks>
/// Authorization is enforced per HTTP request by <see cref="DashboardLegacyAuthentication"/>, but a
/// SignalR stream is authorized once at negotiate/upgrade time and then runs for as long as the
/// socket stays open. Without an explicit revocation signal, signing out clears the cookie while the
/// already-open socket keeps pushing resource snapshots - which carry environment variables,
/// connection strings, and properties the dashboard marks sensitive - to a session the operator
/// believes they ended.
///
/// Revocation is deliberately coarse: every live stream is cancelled rather than only the caller's.
/// The backend delegates identity to the existing dashboard and therefore has no per-user principal
/// to match connections against, and the dashboard is a single-operator tool, so cancelling
/// everything is both correct and cheap. Clients reconnect automatically, and the reconnect attempt
/// is authorized again like any other request, so a still-valid session recovers on its own.
/// </remarks>
internal sealed class DashboardStreamRevocation
{
    private readonly Lock _gate = new();
    private CancellationTokenSource _source = new();

    /// <summary>
    /// Creates a token source that is cancelled either by <paramref name="callerToken"/> or by the
    /// next call to <see cref="RevokeAll"/>. Callers must dispose the returned source.
    /// </summary>
    public CancellationTokenSource CreateLinkedTokenSource(CancellationToken callerToken)
    {
        // The revocation token is read under the lock so a concurrent RevokeAll either cancels the
        // source we link to, or replaces it after we captured the old (already cancelled) one. In
        // both orderings the returned source ends up cancelled, which is the safe direction.
        CancellationToken revocationToken;
        lock (_gate)
        {
            revocationToken = _source.Token;
        }

        return CancellationTokenSource.CreateLinkedTokenSource(callerToken, revocationToken);
    }

    /// <summary>
    /// Cancels every stream created before this call and arms a fresh token for later streams.
    /// </summary>
    public void RevokeAll()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            previous = _source;
            _source = new CancellationTokenSource();
        }

        // Cancel outside the lock: continuations registered on the token run inline on this thread,
        // and any of them re-entering CreateLinkedTokenSource would otherwise self-deadlock.
        try
        {
            previous.Cancel();
        }
        finally
        {
            previous.Dispose();
        }
    }
}
