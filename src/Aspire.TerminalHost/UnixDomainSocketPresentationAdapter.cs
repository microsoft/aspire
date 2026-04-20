// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Terminal;
using Hex1b;

namespace Aspire.TerminalHost;

/// <summary>
/// A Hex1b presentation adapter that serves terminal I/O over a Unix domain socket
/// using the Aspire Terminal Protocol (see docs/specs/terminal-protocol.md).
/// </summary>
internal sealed class UnixDomainSocketPresentationAdapter : IHex1bTerminalPresentationAdapter
{
    private readonly string _socketPath;
    private readonly CancellationTokenSource _disposeCts = new();
    private Socket? _serverSocket;
    private Socket? _clientSocket;
    private NetworkStream? _clientStream;
    private TerminalFrameReader? _reader;
    private TerminalFrameWriter? _writer;
    private bool _disposed;
    private bool _helloSent;

    public UnixDomainSocketPresentationAdapter(string socketPath, int width, int height)
    {
        _socketPath = socketPath;
        Width = width;
        Height = height;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public TerminalCapabilities Capabilities => new()
    {
        SupportsTrueColor = true,
        Supports256Colors = true,
        SupportsAlternateScreen = true,
        SupportsBracketedPaste = true,
    };

    public event Action<int, int>? Resized;
    public event Action? Disconnected;

    /// <summary>
    /// Starts listening on the UDS and waits for a client to connect.
    /// Sends the HELLO frame once connected.
    /// </summary>
    public async Task WaitForClientAsync(CancellationToken ct)
    {
        // Clean up stale socket
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        var socketDir = Path.GetDirectoryName(_socketPath);
        if (socketDir is not null)
        {
            Directory.CreateDirectory(socketDir);
        }

        _serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _serverSocket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _serverSocket.Listen(backlog: 1);

        Console.Error.WriteLine($"[TerminalHost] Listening on {_socketPath}");

        _clientSocket = await _serverSocket.AcceptAsync(ct);
        _clientStream = new NetworkStream(_clientSocket, ownsSocket: false);
        _reader = new TerminalFrameReader(_clientStream);
        _writer = new TerminalFrameWriter(_clientStream);

        Console.Error.WriteLine("[TerminalHost] Client connected, sending HELLO");

        // Send HELLO as the first frame per protocol spec
        await _writer.WriteHelloAsync(
            (ushort)Width,
            (ushort)Height,
            TerminalProtocol.HelloFlags.Pty,
            ct);

        _helloSent = true;
    }

    public async ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disposed || _writer is null || !_helloSent)
        {
            return;
        }

        try
        {
            await _writer.WriteDataAsync(data, ct);
        }
        catch (IOException)
        {
            Disconnected?.Invoke();
        }
        catch (SocketException)
        {
            Disconnected?.Invoke();
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
    {
        if (_disposed || _reader is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var frame = await _reader.ReadFrameAsync(linkedCts.Token);

            if (frame is null)
            {
                Disconnected?.Invoke();
                return ReadOnlyMemory<byte>.Empty;
            }

            switch (frame.Value.Type)
            {
                case TerminalProtocol.MessageType.Data:
                    return frame.Value.Payload;

                case TerminalProtocol.MessageType.Resize:
                    var (cols, rows) = frame.Value.ParseResize();
                    Width = cols;
                    Height = rows;
                    Resized?.Invoke(cols, rows);
                    return await ReadInputAsync(ct);

                case TerminalProtocol.MessageType.Close:
                    Disconnected?.Invoke();
                    return ReadOnlyMemory<byte>.Empty;

                default:
                    // Unknown frame type — skip and read next
                    return await ReadInputAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        catch (IOException)
        {
            Disconnected?.Invoke();
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    // Raw mode is handled by the client side, not the server
    public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public (int Row, int Column) GetCursorPosition() => (0, 0);

    /// <summary>
    /// Sends an EXIT frame to the client.
    /// </summary>
    public async ValueTask SendExitAsync(int exitCode, TerminalProtocol.ExitReason reason, CancellationToken ct = default)
    {
        if (_writer is not null && !_disposed)
        {
            try
            {
                await _writer.WriteExitAsync(exitCode, reason, ct);
                await _writer.WriteCloseAsync(ct);
            }
            catch { /* best effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _disposeCts.CancelAsync();

        _clientStream?.Dispose();
        _clientSocket?.Dispose();
        _serverSocket?.Dispose();

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); }
            catch { /* ignore cleanup errors */ }
        }

        _disposeCts.Dispose();
    }
}
