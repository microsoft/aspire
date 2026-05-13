// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Resolves an HMP v1 producer stream for a specific (resourceName, replicaIndex)
/// pair. The dashboard's terminal WebSocket proxy uses this abstraction to keep
/// per-replica consumer UDS paths server-side, so an authenticated browser cannot
/// coerce the dashboard into connecting to arbitrary local sockets.
/// </summary>
/// <remarks>
/// The default registration is <see cref="NullTerminalConnectionResolver"/>, which
/// always returns <c>null</c>. The standalone dashboard uses this default since
/// terminal sessions are inherently a local-dev / DCP feature. The in-process
/// dashboard composition in <c>Aspire.Hosting</c> replaces this with a real
/// implementation that walks the resource graph and connects to the matching
/// <c>Hex1b.Hmp1</c> server.
/// </remarks>
public interface ITerminalConnectionResolver
{
    /// <summary>
    /// Connects to the HMP v1 producer for the requested replica and returns the
    /// raw bidirectional stream the caller should hand to
    /// <c>Hmp1WorkloadAdapter</c>. Returns <c>null</c> if the resource does not
    /// have an interactive terminal, the replica index is out of range, or the
    /// dashboard is not co-hosted with an AppHost that exposes terminal
    /// information.
    /// </summary>
    /// <param name="resourceName">
    /// The user-facing display name of the parent resource (e.g. <c>myapp</c>),
    /// not the per-replica DCP suffix.
    /// </param>
    /// <param name="replicaIndex">The stable 0-based replica index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken);
}
