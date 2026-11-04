// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Hex1b;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Resolves per-replica HMP v1 producer streams from the live resource
/// snapshots in <see cref="IDashboardClient"/>. The dashboard receives
/// the consumer UDS path inside each replica snapshot's properties; this
/// resolver looks up the requested resource by its unique instance name and
/// connects to the matching local socket.
/// </summary>
/// <remarks>
/// <para>The path itself is included in the gRPC stream from the AppHost. In Aspire's
/// single-user, single-machine local-dev scenario the path is not a privileged
/// secret (the user already controls the AppHost process and can read or write
/// anything in its temp directory), but the path never reaches the browser via
/// the terminal WebSocket because the proxy takes only
/// a <c>resource</c> instance identifier.</para>
/// <para>The resolver opens only the transport. The WebSocket handler owns
/// the HMP1 consumer and its per-browser HWT1 presentation lifetime.</para>
/// </remarks>
internal sealed class DefaultTerminalConnectionResolver : ITerminalConnectionResolver
{
    private readonly IDashboardClient _client;

    public DefaultTerminalConnectionResolver(IDashboardClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<Stream?> ConnectAsync(string resourceName, CancellationToken cancellationToken)
    {
        if (!_client.IsEnabled)
        {
            return null;
        }

        var match = _client.GetResource(resourceName);
        if (match is null || !match.HasTerminal())
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
