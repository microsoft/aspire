// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Backend;

/// <summary>
/// Resolves the AppHost-provided HMP v1 stream for a terminal-enabled resource.
/// The browser supplies only the resource display name and replica index; the
/// consumer socket path remains inside the authoritative resource-service session.
/// </summary>
internal interface ITerminalConnectionResolver
{
    Task<Stream?> ConnectAsync(
        string resourceName,
        int replicaIndex,
        CancellationToken cancellationToken);
}
