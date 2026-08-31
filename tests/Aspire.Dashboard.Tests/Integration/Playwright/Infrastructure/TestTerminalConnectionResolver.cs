// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Dashboard.Terminal;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal enum TestHmp1FrameType : byte
{
    Hello = 0x01,
    StateSync = 0x02,
    Input = 0x04,
    RequestPrimary = 0x07,
    ClientHello = 0x0B,
}

internal readonly record struct TestHmp1Frame(TestHmp1FrameType Type, byte[] Payload);

internal sealed class TestTerminalConnectionResolver : ITerminalConnectionResolver
{
    private readonly Channel<TestTerminalConnection> _connections = Channel.CreateUnbounded<TestTerminalConnection>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public async Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        Socket? proxySocket = null;
        Socket? testSocket = null;

        try
        {
            listener.Start();

            proxySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connectTask = proxySocket.ConnectAsync(listener.LocalEndpoint, cancellationToken);
            testSocket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            await connectTask.ConfigureAwait(false);

            var connection = new TestTerminalConnection(testSocket);
            testSocket = null;
            await _connections.Writer.WriteAsync(connection, cancellationToken).ConfigureAwait(false);

            var stream = new NetworkStream(proxySocket, ownsSocket: true);
            proxySocket = null;
            return stream;
        }
        finally
        {
            listener.Stop();
            proxySocket?.Dispose();
            testSocket?.Dispose();
        }
    }

    public Task<TestTerminalConnection> AcceptConnectionAsync(CancellationToken cancellationToken)
    {
        return _connections.Reader.ReadAsync(cancellationToken).AsTask();
    }

    public async Task DiscardPendingConnectionsAsync()
    {
        while (_connections.Reader.TryRead(out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class TestTerminalConnection : IAsyncDisposable
{
    private const int HeaderLength = 5;
    private const int MaximumPayloadLength = 1024 * 1024;
    private readonly NetworkStream _stream;

    public TestTerminalConnection(Socket socket)
    {
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    public async Task<TestHmp1Frame> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (payloadLength is < 0 or > MaximumPayloadLength)
        {
            throw new InvalidDataException($"Invalid HMP frame payload length: {payloadLength}.");
        }

        var payload = new byte[payloadLength];
        await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new TestHmp1Frame((TestHmp1FrameType)header[0], payload);
    }

    public async Task<TestHmp1Frame> ReadUntilFrameAsync(TestHmp1FrameType type, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Type == type)
            {
                return frame;
            }
        }
    }

    public Task SendHelloAsync(int width, int height, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            peerId = "dashboard-peer",
            primaryPeerId = "existing-primary",
            width,
            height,
            peers = new[]
            {
                new { peerId = "dashboard-peer", displayName = "aspire-dashboard" },
                new { peerId = "existing-primary", displayName = "existing-primary" },
            },
        });

        return SendFrameAsync(TestHmp1FrameType.Hello, payload, cancellationToken);
    }

    public Task SendStateSyncAsync(CancellationToken cancellationToken)
    {
        return SendFrameAsync(TestHmp1FrameType.StateSync, [], cancellationToken);
    }

    private async Task SendFrameAsync(TestHmp1FrameType type, byte[] payload, CancellationToken cancellationToken)
    {
        var frame = new byte[HeaderLength + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return _stream.DisposeAsync();
    }
}
