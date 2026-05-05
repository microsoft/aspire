// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Hex1b;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Resolves per-replica HMP v1 producer streams by walking the live resource
/// snapshot stream from <see cref="IDashboardClient"/>. The dashboard receives
/// the consumer UDS path inside each replica snapshot's properties; this
/// resolver looks up the requested resource by display name + replica index and
/// connects to the matching local socket.
/// </summary>
/// <remarks>
/// The path itself is included in the gRPC stream from the AppHost. In Aspire's
/// single-user, single-machine local-dev scenario the path is not a privileged
/// secret (the user already controls the AppHost process and can read or write
/// anything in its temp directory), but the path never reaches the browser via
/// the terminal WebSocket because the proxy takes only
/// <c>resource</c>/<c>replica</c> identifiers.
/// </remarks>
internal sealed class DefaultTerminalConnectionResolver : ITerminalConnectionResolver
{
    private readonly IDashboardClient _client;

    public DefaultTerminalConnectionResolver(IDashboardClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken)
    {
        if (!_client.IsEnabled)
        {
            return null;
        }

        // Locate the replica snapshot for (resourceName, replicaIndex). DCP names
        // each replica with a random suffix (e.g. myapp-abc123), but every snapshot
        // also carries the user-facing display name and the stable terminal
        // properties stamped by DashboardServiceData. We match by display name +
        // replica index rather than by the synthetic snapshot name.
        ResourceViewModel? match = null;
        foreach (var resource in _client.GetResources())
        {
            if (!string.Equals(resource.DisplayName, resourceName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!resource.HasTerminal())
            {
                continue;
            }

            if (!resource.TryGetTerminalReplicaInfo(out var index, out _) || index != replicaIndex)
            {
                continue;
            }

            match = resource;
            break;
        }

        if (match is null)
        {
            return null;
        }

        if (!match.TryGetTerminalConsumerUdsPath(out var udsPath))
        {
            return null;
        }

        return await Hmp1Transports.ConnectUnixSocket(udsPath, cancellationToken).ConfigureAwait(false);
    }
}
