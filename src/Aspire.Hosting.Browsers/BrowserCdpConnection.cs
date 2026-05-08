// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Browser-level CDP connection operations used by BrowserPageSession.
internal interface IBrowserCdpConnection : IAsyncDisposable
{
    Task Completion { get; }

    Task<BrowserCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken);

    Task<BrowserGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken);

    Task<BrowserAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken);

    Task<BrowserCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken);

    Task<BrowserCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken);

    Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken);

    Task<BrowserCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken);

    Task<BrowserNavigateResult> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken);

    Task<BrowserRuntimeEvaluateResult> EvaluateAsync(string sessionId, string expression, TimeSpan? timeout, CancellationToken cancellationToken);

    Task<string> SendRawCommandAsync(string? sessionId, string method, string? parametersJson, CancellationToken cancellationToken);
}

// Owns one browser-level CDP transport. Protocol parsing stays in BrowserCdpProtocol, while page lifecycle and
// reconnection policy stay in BrowserPageSession.
internal sealed class BrowserCdpConnection : IBrowserCdpConnection
{
    // CDP commands should fail fast enough to surface a broken browser session in the dashboard. Close uses a shorter
    // budget because it runs during disposal, while the websocket keep-alive stays comfortably below common proxy idle
    // timers without sending frequent pings during normal local development.
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromSeconds(10);
    // Screenshot capture asks the browser to rasterize and encode the current surface. Real browsers can take longer
    // than lightweight lifecycle/enable commands, especially under CI or agent load, so give this command a larger
    // protocol budget without slowing down ordinary command failures.
    private static readonly TimeSpan s_screenshotCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_keepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Func<BrowserCdpProtocolEvent, ValueTask> _eventHandler;
    private readonly ILogger<BrowserSessionManager> _logger;
    private readonly ConcurrentDictionary<long, IPendingCommand> _pendingCommands = new();
    private readonly Task _receiveLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly IBrowserCdpTransport _transport;
    private long _nextCommandId;

    private BrowserCdpConnection(IBrowserCdpTransport transport, Func<BrowserCdpProtocolEvent, ValueTask> eventHandler, ILogger<BrowserSessionManager> logger)
    {
        _eventHandler = eventHandler;
        _logger = logger;
        _transport = transport;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public Task Completion => _receiveLoop;

    public static async Task<BrowserCdpConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserSessionManager> logger,
        CancellationToken cancellationToken)
    {
        return await ConnectAsync(
            webSocketUri,
            eventHandler,
            logger,
            cancellationToken,
            static () => new ClientWebSocketConnector()).ConfigureAwait(false);
    }

    internal static async Task<BrowserCdpConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserSessionManager> logger,
        CancellationToken cancellationToken,
        Func<IClientWebSocketConnector> connectorFactory)
    {
        using var connector = connectorFactory();
        // Browser-log sessions can sit idle while the page is loading or the developer is reading the dashboard.
        // Keep-alives make transport failures show up in the receive loop instead of only on the next CDP command.
        connector.SetKeepAliveInterval(s_keepAliveInterval);
        await connector.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
        return Create(
            new BrowserWebSocketCdpTransport(connector.DetachConnectedWebSocket(), s_closeTimeout),
            eventHandler,
            logger);
    }

    internal static BrowserCdpConnection Create(
        IBrowserCdpTransport transport,
        Func<BrowserCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserSessionManager> logger)
    {
        return new BrowserCdpConnection(transport, eventHandler, logger);
    }

    public Task<BrowserCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserCdpProtocol.TargetCreateTargetMethod,
            sessionId: null,
            static writer => writer.WriteString("url", "about:blank"),
            BrowserCdpProtocol.ParseCreateTargetResponse,
            cancellationToken);
    }

    public Task<BrowserGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserCdpProtocol.TargetGetTargetsMethod,
            sessionId: null,
            writeParameters: null,
            BrowserCdpProtocol.ParseGetTargetsResponse,
            cancellationToken);
    }

    public Task<BrowserAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserCdpProtocol.TargetAttachToTargetMethod,
            sessionId: null,
            writer =>
            {
                writer.WriteString("targetId", targetId);
                writer.WriteBoolean("flatten", true);
            },
            BrowserCdpProtocol.ParseAttachToTargetResponse,
            cancellationToken);
    }

    public Task<BrowserCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserCdpProtocol.TargetCloseTargetMethod,
            sessionId: null,
            writer => writer.WriteString("targetId", targetId),
            BrowserCdpProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public Task<BrowserCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken)
    {
        // Target discovery is a browser-level CDP subscription. Enabling it tells Chromium to publish lifecycle
        // events for page targets (created, destroyed, crashed, detached) on this browser websocket. We need those
        // events to decide whether a tracked tab ended normally, crashed, or only lost its CDP socket and can be
        // reattached. Target.getTargets is just a point-in-time snapshot; setDiscoverTargets is the ongoing signal.
        return SendCommandAsync(
            BrowserCdpProtocol.TargetSetDiscoverTargetsMethod,
            sessionId: null,
            static writer => writer.WriteBoolean("discover", true),
            BrowserCdpProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public async Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
    {
        // These domains are per attached page session. In real browsers a successful browser-level websocket connection
        // is not enough; without these enables the page keeps running but console, exception, and network events stay
        // silent for this target.
        await SendCommandAsync(BrowserCdpProtocol.RuntimeEnableMethod, sessionId, writeParameters: null, BrowserCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserCdpProtocol.LogEnableMethod, sessionId, writeParameters: null, BrowserCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserCdpProtocol.PageEnableMethod, sessionId, writeParameters: null, BrowserCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserCdpProtocol.NetworkEnableMethod, sessionId, writeParameters: null, BrowserCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
    }

    public Task<BrowserCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, BrowserScreenshotCaptureOptions options, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserCdpProtocol.PageCaptureScreenshotMethod,
            sessionId,
            writer =>
            {
                writer.WriteString("format", options.Format);
                writer.WriteBoolean("fromSurface", true);
                if (options.Quality is { } quality)
                {
                    writer.WriteNumber("quality", quality);
                }
                if (options.FullPage)
                {
                    writer.WriteBoolean("captureBeyondViewport", true);
                }
            },
            BrowserCdpProtocol.ParseCaptureScreenshotResponse,
            cancellationToken,
            s_screenshotCommandTimeout);
    }

    public Task<BrowserNavigateResult> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserCdpProtocol.PageNavigateMethod,
            sessionId,
            writer => writer.WriteString("url", url.ToString()),
            BrowserCdpProtocol.ParseNavigateResponse,
            cancellationToken);
    }

    public Task<BrowserRuntimeEvaluateResult> EvaluateAsync(string sessionId, string expression, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        return SendCommandAsync(
            BrowserCdpProtocol.RuntimeEvaluateMethod,
            sessionId,
            writer =>
            {
                // Runtime.evaluate is the final handoff into the page: the expression contains the embedded helper
                // runtime plus one command body, and returnByValue keeps the result as JSON text instead of a remote
                // object handle that would need separate lifetime management.
                writer.WriteString("expression", expression);
                writer.WriteBoolean("awaitPromise", true);
                writer.WriteBoolean("returnByValue", true);
                writer.WriteBoolean("userGesture", true);
            },
            BrowserCdpProtocol.ParseRuntimeEvaluateResponse,
            cancellationToken,
            timeout);
    }

    public Task<string> SendRawCommandAsync(string? sessionId, string method, string? parametersJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        return SendCommandAsync(
            method,
            sessionId,
            string.IsNullOrWhiteSpace(parametersJson)
                ? null
                : writer => BrowserCdpProtocol.WriteRawCommandParameters(writer, parametersJson),
            BrowserCdpProtocol.ParseRawCommandResponse,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Dispose is already tearing down the browser transport. Surface the original command/session failure through
            // the receive loop and pending commands instead of replacing it with a best-effort close failure.
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
            // The receive loop commonly ends because disposing the transport unblocks ReceiveAsync. That terminal
            // exception has already been propagated to in-flight commands in ReceiveLoopAsync, so disposal can continue.
        }

        _disposeCts.Dispose();
        _sendLock.Dispose();
    }

    private async Task<TResult> SendCommandAsync<TResult>(
        string method,
        string? sessionId,
        Action<Utf8JsonWriter>? writeParameters,
        ResponseParser<TResult> parseResponse,
        CancellationToken cancellationToken,
        TimeSpan? commandTimeout = null)
    {
        var commandId = Interlocked.Increment(ref _nextCommandId);
        var pendingCommand = new PendingCommand<TResult>(parseResponse);
        _pendingCommands[commandId] = pendingCommand;

        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            sendCts.CancelAfter(commandTimeout ?? s_commandTimeout);

            using var registration = sendCts.Token.Register(static state =>
            {
                // A timed-out command still has a dictionary entry until finally removes it. Cancel the pending task here
                // so callers unblock immediately; any late CDP response with the same id is ignored because the entry is
                // gone by the time the receive loop tries to match it.
                ((IPendingCommand)state!).SetCanceled();
            }, pendingCommand);

            var payload = BrowserCdpProtocol.CreateCommandFrame(commandId, method, sessionId, writeParameters);
            _logger.LogTrace("Tracked browser protocol -> {Frame}", BrowserCdpProtocol.DescribeFrame(payload));

            await _sendLock.WaitAsync(sendCts.Token).ConfigureAwait(false);
            try
            {
                // Browser-level CDP transports are serialized so startup, reconnect, screenshot, and shutdown never
                // interleave command frames on the same connection.
                await _transport.SendAsync(payload, sendCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await pendingCommand.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_disposeCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for a tracked browser protocol response to '{method}'.");
        }
        finally
        {
            _pendingCommands.TryRemove(commandId, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? terminalException = null;

        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                var frame = await _transport.ReceiveAsync(_disposeCts.Token).ConfigureAwait(false);
                _logger.LogTrace("Tracked browser protocol <- {Frame}", BrowserCdpProtocol.DescribeFrame(frame));

                try
                {
                    await HandleFrameAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    terminalException = new InvalidOperationException(
                        $"Tracked browser protocol receive loop failed while processing frame {BrowserCdpProtocol.DescribeFrame(frame)}.",
                        ex);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            terminalException = ex;
        }
        finally
        {
            terminalException ??= new InvalidOperationException("Browser debug connection closed.");

            // Any terminal transport failure must fault in-flight commands so callers can recover or shut down
            // instead of waiting forever on a response that will never arrive.
            foreach (var pendingCommand in _pendingCommands.Values)
            {
                pendingCommand.SetException(terminalException);
            }
        }

        if (!_disposeCts.IsCancellationRequested)
        {
            throw terminalException ?? new InvalidOperationException("Browser debug connection closed.");
        }
    }

    private async Task HandleFrameAsync(byte[] frame)
    {
        var header = BrowserCdpProtocol.ParseMessageHeader(frame);
        // CDP responses are matched by id, while events are identified by method and may arrive between responses for
        // unrelated commands. Handle responses first so callers waiting on commands are unblocked even when the browser
        // is also streaming network or console events.
        if (header.Id is long commandId)
        {
            if (_pendingCommands.TryGetValue(commandId, out var pendingCommand))
            {
                pendingCommand.SetResult(frame);
            }

            return;
        }

        if (header.Method is not null && BrowserCdpProtocol.ParseEvent(header, frame) is { } protocolEvent)
        {
            await _eventHandler(protocolEvent).ConfigureAwait(false);
        }
    }

    private interface IPendingCommand
    {
        void SetCanceled();

        void SetException(Exception exception);

        void SetResult(ReadOnlyMemory<byte> framePayload);
    }

    private delegate TResult ResponseParser<TResult>(ReadOnlySpan<byte> framePayload);

    private sealed class PendingCommand<TResult>(ResponseParser<TResult> parseResponse) : IPendingCommand
    {
        private readonly ResponseParser<TResult> _parseResponse = parseResponse;
        private readonly TaskCompletionSource<TResult> _taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Task => _taskCompletionSource.Task;

        public void SetCanceled()
        {
            _taskCompletionSource.TrySetCanceled();
        }

        public void SetException(Exception exception)
        {
            _taskCompletionSource.TrySetException(exception);
        }

        public void SetResult(ReadOnlyMemory<byte> framePayload)
        {
            try
            {
                _taskCompletionSource.TrySetResult(_parseResponse(framePayload.Span));
            }
            catch (Exception ex)
            {
                _taskCompletionSource.TrySetException(ex);
            }
        }
    }
}

// Test seam for websocket creation. Production code uses ClientWebSocketConnector; protocol/recovery tests can inject
// a connector that fails or returns a controlled socket without depending on a real browser.
internal interface IClientWebSocketConnector : IDisposable
{
    void SetKeepAliveInterval(TimeSpan interval);

    Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken);

    WebSocket DetachConnectedWebSocket();
}

// Thin ownership wrapper around ClientWebSocket. It lets BrowserCdpConnection transfer the connected socket into
// the receive/send pipeline while still disposing the socket on connection failures.
internal sealed class ClientWebSocketConnector : IClientWebSocketConnector
{
    private ClientWebSocket? _webSocket = new();

    public void SetKeepAliveInterval(TimeSpan interval)
    {
        GetWebSocket().Options.KeepAliveInterval = interval;
    }

    public Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken)
    {
        return GetWebSocket().ConnectAsync(webSocketUri, cancellationToken);
    }

    public WebSocket DetachConnectedWebSocket()
    {
        var webSocket = GetWebSocket();
        _webSocket = null;
        return webSocket;
    }

    public void Dispose()
    {
        _webSocket?.Dispose();
        _webSocket = null;
    }

    private ClientWebSocket GetWebSocket()
    {
        var webSocket = _webSocket;
        ObjectDisposedException.ThrowIf(webSocket is null, this);
        return webSocket;
    }
}

// Transport abstraction for browser-level CDP frames. CDP defines the JSON envelope
// (https://chromedevtools.github.io/devtools-protocol/), but the byte framing depends on how Chromium was launched:
//
// WebSocket:
//   {"id":1,"method":"Runtime.enable","sessionId":"A1B2C3"}
//
// Chromium --remote-debugging-pipe:
//   {"id":1,"method":"Runtime.enable","sessionId":"A1B2C3"}\0
//   {"method":"Runtime.consoleAPICalled","params":{"type":"log"},"sessionId":"A1B2C3"}\0
//
// Keeping framing here lets BrowserCdpConnection own command correlation independent of launch transport.
internal interface IBrowserCdpTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class BrowserWebSocketCdpTransport(WebSocket webSocket, TimeSpan closeTimeout) : IBrowserCdpTransport
{
    private readonly TimeSpan _closeTimeout = closeTimeout;
    private readonly WebSocket _webSocket = webSocket;

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _webSocket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();

        while (true)
        {
            var result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw CreateUnexpectedConnectionClosureException(result);
            }

            // Large CDP events can span multiple websocket frames. Buffer until EndOfMessage so protocol parsing
            // always sees one complete JSON message, matching the frames observed from a real browser.
            messageBuffer.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return messageBuffer.ToArray();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(_closeTimeout);
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            _webSocket.Abort();
        }
        finally
        {
            _webSocket.Dispose();
        }
    }

    private static InvalidOperationException CreateUnexpectedConnectionClosureException(WebSocketReceiveResult result)
    {
        // Preserve the remote close details; they become the reconnect/resource-log diagnostics when CDP drops.
        if (result.CloseStatus is { } closeStatus)
        {
            if (!string.IsNullOrWhiteSpace(result.CloseStatusDescription))
            {
                return new InvalidOperationException($"Browser debug connection closed by the remote endpoint with status '{closeStatus}' ({(int)closeStatus}): {result.CloseStatusDescription}");
            }

            return new InvalidOperationException($"Browser debug connection closed by the remote endpoint with status '{closeStatus}' ({(int)closeStatus}).");
        }

        return new InvalidOperationException("Browser debug connection closed by the remote endpoint without a close status.");
    }
}

internal sealed class BrowserPipeCdpTransport(Stream readStream, Stream writeStream) : IBrowserCdpTransport
{
    private const byte FrameTerminator = 0;
    private const int ReadBufferSize = 16 * 1024;
    private static readonly byte[] s_frameTerminator = [FrameTerminator];

    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly Stream _readStream = readStream;
    private readonly Stream _writeStream = writeStream;
    private int _readBufferCount;
    private int _readBufferOffset;

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _writeStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _writeStream.WriteAsync(s_frameTerminator, cancellationToken).ConfigureAwait(false);
        await _writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var messageBuffer = new MemoryStream();

        while (true)
        {
            if (_readBufferOffset == _readBufferCount)
            {
                _readBufferOffset = 0;
                _readBufferCount = await _readStream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                if (_readBufferCount == 0)
                {
                    throw new EndOfStreamException("Browser debug pipe closed.");
                }
            }

            var terminatorIndex = Array.IndexOf(_readBuffer, FrameTerminator, _readBufferOffset, _readBufferCount - _readBufferOffset);
            if (terminatorIndex >= 0)
            {
                // Pipe mode can batch several CDP JSON messages in one read, separated by raw NUL bytes:
                //
                // {"id":1,"result":{}}\0{"method":"Log.entryAdded","params":{...}}\0
                //
                // A JSON string containing a null character is escaped as "\u0000", not written as a raw 0x00 byte, so
                // scanning the raw buffer for FrameTerminator is safe and preserves the next frame for the next ReceiveAsync.
                messageBuffer.Write(_readBuffer, _readBufferOffset, terminatorIndex - _readBufferOffset);
                _readBufferOffset = terminatorIndex + 1;
                return messageBuffer.ToArray();
            }

            messageBuffer.Write(_readBuffer, _readBufferOffset, _readBufferCount - _readBufferOffset);
            _readBufferOffset = _readBufferCount;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _writeStream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _readStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
