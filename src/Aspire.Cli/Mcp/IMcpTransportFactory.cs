// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp;

/// <summary>
/// Factory for creating MCP transport instances.
/// This allows transport creation to be deferred until actually needed,
/// avoiding issues with stdin/stdout being captured too early.
/// </summary>
internal interface IMcpTransportFactory
{
    /// <summary>
    /// Creates a new MCP transport instance.
    /// </summary>
    /// <returns>A new transport instance.</returns>
    ITransport CreateTransport();
}
