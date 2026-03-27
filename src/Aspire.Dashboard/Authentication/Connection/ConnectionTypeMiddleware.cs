// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Connections;

namespace Aspire.Dashboard.Authentication.Connection;

/// <summary>
/// This connection middleware registers a connection type feature on the connection.
/// OTLP services check for this feature when authorizing incoming requests to
/// ensure services are only available on specified connections.
/// </summary>
internal sealed class ConnectionTypeMiddleware
{
    private readonly List<ConnectionType> _connectionTypes;
    private readonly ConnectionDelegate _next;

    public ConnectionTypeMiddleware(ConnectionType[] connectionTypes, ConnectionDelegate next)
    {
        _connectionTypes = connectionTypes.ToList();
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task OnConnectionAsync(ConnectionContext context)
    {
        context.Features.Set<IConnectionTypeFeature>(new ConnectionTypeFeature { ConnectionTypes = _connectionTypes });
        await _next(context).ConfigureAwait(false);
    }

    private sealed class ConnectionTypeFeature : IConnectionTypeFeature
    {
        public required List<ConnectionType> ConnectionTypes { get; init; }
    }
}
