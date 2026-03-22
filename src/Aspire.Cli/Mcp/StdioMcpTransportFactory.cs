// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Cli.Mcp;

/// <summary>
/// Default factory that creates <see cref="StdioServerTransport"/> instances for production use.
/// </summary>
internal sealed class StdioMcpTransportFactory(ILoggerFactory? loggerFactory) : IMcpTransportFactory
{
    /// <inheritdoc />
    public ITransport CreateTransport()
    {
        return new StdioServerTransport("aspire-mcp-server", loggerFactory);
    }
}
