// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Aspire.Terminal;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Tokens;

namespace Aspire.TerminalHost;

/// <summary>
/// A Hex1b presentation filter that serves terminal I/O over a Unix domain socket
/// using the Aspire Terminal Protocol. Supports client disconnect and reconnect with
/// automatic terminal state replay.
///
/// Architecture:
///   - The terminal runs headless (Hex1b manages internal state)
///   - This filter intercepts all output via OnOutputAsync and broadcasts to connected clients
///   - On client connect: snapshot the terminal, send HELLO + screen state, then stream live
///   - On client disconnect: terminal keeps running, ready for new connections
/// </summary>
internal sealed class TerminalSocketServer : IHex1bTerminalPresentationFilter, IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly int _columns;
    private readonly int _rows;
    private readonly CancellationTokenSource _disposeCts = new();

    private Socket? _serverSocket;
    private Hex1bTerminal? _terminal;
    private ClientSession? _activeSession;
    private readonly object _sessionLock = new();
    private bool _disposed;

    private int _currentWidth;
    private int _currentHeight;

    public TerminalSocketServer(string socketPath, int columns, int rows)
    {
        _socketPath = socketPath;
        _columns = columns;
        _rows = rows;
        _currentWidth = columns;
        _currentHeight = rows;
    }

    /// <summary>
    /// Sets the terminal reference so the server can create snapshots for reconnection.
    /// Must be called before the terminal starts running.
    /// </summary>
    public void SetTerminal(Hex1bTerminal terminal)
    {
        _terminal = terminal;
    }

    /// <summary>
    /// Starts the UDS listener and accepts client connections in the background.
    /// </summary>
    public Task StartListeningAsync(CancellationToken ct)
    {
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
        _serverSocket.Listen(backlog: 2);

        Console.Error.WriteLine($"[TerminalHost] Listening on {_socketPath}");

        // Accept connections in the background
        _ = AcceptConnectionsAsync(ct);

        return Task.CompletedTask;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                var clientSocket = await _serverSocket!.AcceptAsync(ct).ConfigureAwait(false);
                Console.Error.WriteLine("[TerminalHost] Client connected");

                _ = HandleClientAsync(clientSocket, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleClientAsync(Socket clientSocket, CancellationToken ct)
    {
        var stream = new NetworkStream(clientSocket, ownsSocket: true);
        var writer = new TerminalFrameWriter(stream);
        var reader = new TerminalFrameReader(stream);

        var outputChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var session = new ClientSession(stream, writer, reader, outputChannel);

        // Replace any existing session (single client at a time)
        ClientSession? oldSession;
        lock (_sessionLock)
        {
            oldSession = _activeSession;
            _activeSession = session;
        }

        if (oldSession is not null)
        {
            await oldSession.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            // Send HELLO
            var flags = TerminalProtocol.HelloFlags.Pty | TerminalProtocol.HelloFlags.Replay;
            await writer.WriteHelloAsync(
                (ushort)_currentWidth,
                (ushort)_currentHeight,
                flags,
                ct).ConfigureAwait(false);

            // Snapshot and send current terminal state
            if (_terminal is not null)
            {
                using var snapshot = _terminal.CreateSnapshot();
                var ansi = snapshot.ToAnsi(new TerminalAnsiOptions
                {
                    IncludeClearScreen = true,
                    IncludeCursorPosition = true,
                });
                var ansiBytes = Encoding.UTF8.GetBytes(ansi);
                await writer.WriteDataAsync(ansiBytes, ct).ConfigureAwait(false);
            }

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);

            // Stream output from channel to client
            var outputTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var data in outputChannel.Reader.ReadAllAsync(sessionCts.Token).ConfigureAwait(false))
                    {
                        await writer.WriteDataAsync(data, sessionCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }, sessionCts.Token);

            // Read input from client and forward to terminal
            var inputTask = Task.Run(async () =>
            {
                try
                {
                    while (!sessionCts.Token.IsCancellationRequested)
                    {
                        var frame = await reader.ReadFrameAsync(sessionCts.Token).ConfigureAwait(false);
                        if (frame is null)
                        {
                            break; // Client disconnected
                        }

                        switch (frame.Value.Type)
                        {
                            case TerminalProtocol.MessageType.Data:
                                // Forward input to terminal
                                if (_terminal is not null)
                                {
                                    await _terminal.SendInputAsync(frame.Value.Payload, sessionCts.Token).ConfigureAwait(false);
                                }
                                break;

                            case TerminalProtocol.MessageType.Resize:
                                var (cols, rows) = frame.Value.ParseResize();
                                _terminal?.Resize(cols, rows);
                                break;

                            case TerminalProtocol.MessageType.Close:
                                return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }, sessionCts.Token);

            await Task.WhenAny(outputTask, inputTask).ConfigureAwait(false);
            await sessionCts.CancelAsync().ConfigureAwait(false);

            try { await Task.WhenAll(outputTask, inputTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            lock (_sessionLock)
            {
                if (_activeSession == session)
                {
                    _activeSession = null;
                }
            }

            await session.DisposeAsync().ConfigureAwait(false);
            Console.Error.WriteLine("[TerminalHost] Client disconnected");
        }
    }

    #region IHex1bTerminalPresentationFilter

    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        _currentWidth = width;
        _currentHeight = height;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        var tokens = appliedTokens.Select(t => t.Token).ToList();

        // Broadcast output to the active client session
        lock (_sessionLock)
        {
            if (_activeSession is not null)
            {
                var ansi = AnsiTokenSerializer.Serialize(tokens);
                if (ansi.Length > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(ansi);
                    _activeSession.OutputChannel.Writer.TryWrite(bytes);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(tokens);
    }

    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
    {
        _currentWidth = width;
        _currentHeight = height;
        return ValueTask.CompletedTask;
    }

    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _disposeCts.CancelAsync().ConfigureAwait(false);

        lock (_sessionLock)
        {
            _activeSession?.OutputChannel.Writer.TryComplete();
        }

        _serverSocket?.Dispose();

        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); }
            catch { /* ignore */ }
        }

        _disposeCts.Dispose();
    }

    private sealed class ClientSession(
        NetworkStream stream,
        TerminalFrameWriter writer,
        TerminalFrameReader reader,
        Channel<byte[]> outputChannel) : IAsyncDisposable
    {
        public TerminalFrameWriter Writer => writer;
        public TerminalFrameReader Reader => reader;
        public Channel<byte[]> OutputChannel => outputChannel;

        public ValueTask DisposeAsync()
        {
            outputChannel.Writer.TryComplete();
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
