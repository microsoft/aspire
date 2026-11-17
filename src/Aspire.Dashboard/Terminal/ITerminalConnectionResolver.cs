// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Resolves an HMP v1 producer stream for a specific resource instance.
/// The dashboard's terminal WebSocket proxy uses this abstraction to keep
/// per-replica consumer UDS paths server-side, so an authenticated browser cannot
/// coerce the dashboard into connecting to arbitrary local sockets.
/// </summary>
/// <remarks>
/// The default registration is <see cref="DefaultTerminalConnectionResolver"/>,
/// which looks up the resource instance in <c>IDashboardClient</c> to
/// resolve its name to the consumer UDS path that
/// the AppHost stamped onto the snapshot, then opens a stream against it.
/// </remarks>
public interface ITerminalConnectionResolver
{
    /// <summary>
    /// Connects to the HMP v1 producer for the requested replica and returns the
    /// raw bidirectional stream the caller should hand to
    /// <c>Hmp1WorkloadAdapter</c>. Returns <c>null</c> if the resource does not
    /// have an interactive terminal, the instance is unavailable, or the
    /// dashboard is not co-hosted with an AppHost that exposes terminal
    /// information.
    /// </summary>
    /// <param name="resourceName">
    /// The unique resource instance name (e.g. <c>myapp-abc123</c>), matching
    /// <c>ResourceViewModel.Name</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Stream?> ConnectAsync(string resourceName, CancellationToken cancellationToken);
}
