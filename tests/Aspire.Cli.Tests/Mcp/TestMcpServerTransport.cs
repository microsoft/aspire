// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using Aspire.Cli.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Cli.Tests.Mcp;

/// <summary>
/// A test helper that creates in-memory pipe-based transports for testing the MCP server.
/// Provides both the server transport (for DI injection) and a way to create a connected client.
/// Implements <see cref="IMcpTransportFactory"/> so it can be registered in DI.
/// </summary>
internal sealed class TestMcpServerTransport : IMcpTransportFactory, IDisposable
{
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    /// The pipe for sending data from client to server.
    /// </summary>
    public Pipe ClientToServerPipe { get; } = new();

    /// <summary>
    /// The pipe for sending data from server to client.
    /// </summary>
    public Pipe ServerToClientPipe { get; } = new();

    /// <summary>
    /// The server transport that should be registered in DI.
    /// </summary>
    public ITransport ServerTransport { get; }

    public TestMcpServerTransport(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        ServerTransport = new StreamServerTransport(
            ClientToServerPipe.Reader.AsStream(),
            ServerToClientPipe.Writer.AsStream(),
            serverName: "aspire-mcp-server",
            loggerFactory: _loggerFactory);
    }

    /// <inheritdoc />
    public ITransport CreateTransport() => ServerTransport;

    /// <summary>
    /// Creates an MCP client that connects to the server through the in-memory pipes.
    /// </summary>
    /// <param name="loggerFactory">Logger factory for the client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected MCP client.</returns>
    public Task<McpClient> CreateClientAsync(ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var clientTransport = new StreamClientTransport(
            serverInput: ClientToServerPipe.Writer.AsStream(),
            serverOutput: ServerToClientPipe.Reader.AsStream(),
            loggerFactory: loggerFactory);

        return McpClient.CreateAsync(clientTransport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Completes the pipes to clean up resources.
    /// </summary>
    public void CompletePipes()
    {
        ClientToServerPipe.Reader.Complete();
        ClientToServerPipe.Writer.Complete();
        ServerToClientPipe.Reader.Complete();
        ServerToClientPipe.Writer.Complete();
    }

    public void Dispose()
    {
        CompletePipes();
    }
}

