// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Default no-op resolver used when the dashboard runs standalone (not co-hosted
/// inside an AppHost). It always reports that no terminal is available, which
/// causes <see cref="TerminalWebSocketProxy"/> to return <c>404</c>.
/// </summary>
internal sealed class NullTerminalConnectionResolver : ITerminalConnectionResolver
{
    public Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken)
    {
        return Task.FromResult<Stream?>(null);
    }
}
