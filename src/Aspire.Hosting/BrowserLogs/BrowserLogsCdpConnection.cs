// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Browser-level CDP connection operations used by BrowserPageSession.
internal interface IBrowserLogsCdpConnection : IAsyncDisposable
{
    Task Completion { get; }

    Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken);

    Task<BrowserLogsGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken);

    Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken);

    Task<BrowserLogsCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken);

    Task<BrowserLogsCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken);

    Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken);

    Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, CancellationToken cancellationToken);

    Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken);
}

// Owns the browser-level websocket only. Protocol parsing stays in BrowserLogsCdpProtocol, while page lifecycle and
// reconnection policy stay in BrowserPageSession.
internal sealed class BrowserLogsCdpConnection : IBrowserLogsCdpConnection
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
    private readonly Func<BrowserLogsCdpProtocolEvent, ValueTask> _eventHandler;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly ConcurrentDictionary<long, IPendingCommand> _pendingCommands = new();
    private readonly Task _receiveLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly WebSocket _webSocket;
    private long _nextCommandId;

    private BrowserLogsCdpConnection(WebSocket webSocket, Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler, ILogger<BrowserLogsSessionManager> logger)
    {
        _eventHandler = eventHandler;
        _logger = logger;
        _webSocket = webSocket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public Task Completion => _receiveLoop;

    public static async Task<BrowserLogsCdpConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken)
    {
        return await ConnectAsync(
            webSocketUri,
            eventHandler,
            logger,
            cancellationToken,
            static () => new ClientWebSocketConnector()).ConfigureAwait(false);
    }

    internal static async Task<BrowserLogsCdpConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken,
        Func<IClientWebSocketConnector> connectorFactory)
    {
        using var connector = connectorFactory();
        // Browser-log sessions can sit idle while the page is loading or the developer is reading the dashboard.
        // Keep-alives make transport failures show up in the receive loop instead of only on the next CDP command.
        connector.SetKeepAliveInterval(s_keepAliveInterval);
        await connector.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
        return new BrowserLogsCdpConnection(connector.DetachConnectedWebSocket(), eventHandler, logger);
    }

    public Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsCdpProtocol.TargetCreateTargetMethod,
            sessionId: null,
            static writer => writer.WriteString("url", "about:blank"),
            BrowserLogsCdpProtocol.ParseCreateTargetResponse,
            cancellationToken);
    }

    public Task<BrowserLogsGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsCdpProtocol.TargetGetTargetsMethod,
            sessionId: null,
            writeParameters: null,
            BrowserLogsCdpProtocol.ParseGetTargetsResponse,
            cancellationToken);
    }

    public Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsCdpProtocol.TargetAttachToTargetMethod,
            sessionId: null,
            writer =>
            {
                writer.WriteString("targetId", targetId);
                writer.WriteBoolean("flatten", true);
            },
            BrowserLogsCdpProtocol.ParseAttachToTargetResponse,
            cancellationToken);
    }

    public Task<BrowserLogsCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsCdpProtocol.TargetCloseTargetMethod,
            sessionId: null,
            writer => writer.WriteString("targetId", targetId),
            BrowserLogsCdpProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public Task<BrowserLogsCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken)
    {
        // Target discovery is a browser-level CDP subscription. Enabling it tells Chromium to publish lifecycle
        // events for page targets (created, destroyed, crashed, detached) on this browser websocket. We need those
        // events to decide whether a tracked tab ended normally, crashed, or only lost its CDP socket and can be
        // reattached. Target.getTargets is just a point-in-time snapshot; setDiscoverTargets is the ongoing signal.
        return SendCommandAsync(
            BrowserLogsCdpProtocol.TargetSetDiscoverTargetsMethod,
            sessionId: null,
            static writer => writer.WriteBoolean("discover", true),
            BrowserLogsCdpProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public async Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
    {
        // These domains are per attached page session. In real browsers a successful browser-level websocket connection
        // is not enough; without these enables the page keeps running but console, exception, and network events stay
        // silent for this target.
        await SendCommandAsync(BrowserLogsCdpProtocol.RuntimeEnableMethod, sessionId, writeParameters: null, BrowserLogsCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserLogsCdpProtocol.LogEnableMethod, sessionId, writeParameters: null, BrowserLogsCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserLogsCdpProtocol.PageEnableMethod, sessionId, writeParameters: null, BrowserLogsCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserLogsCdpProtocol.NetworkEnableMethod, sessionId, writeParameters: null, BrowserLogsCdpProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
    }

    public Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsCdpProtocol.PageCaptureScreenshotMethod,
            sessionId,
            static writer =>
            {
                writer.WriteString("format", "png");
                writer.WriteBoolean("fromSurface", true);
            },
            BrowserLogsCdpProtocol.ParseCaptureScreenshotResponse,
            cancellationToken,
            s_screenshotCommandTimeout);
    }

    public Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsCdpProtocol.PageNavigateMethod,
            sessionId,
            writer => writer.WriteString("url", url.ToString()),
            BrowserLogsCdpProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(s_closeTimeout);
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

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch
        {
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
                ((IPendingCommand)state!).SetCanceled();
            }, pendingCommand);

            var payload = BrowserLogsCdpProtocol.CreateCommandFrame(commandId, method, sessionId, writeParameters);
            _logger.LogTrace("Tracked browser protocol -> {Frame}", BrowserLogsCdpProtocol.DescribeFrame(payload));

            await _sendLock.WaitAsync(sendCts.Token).ConfigureAwait(false);
            try
            {
                // ClientWebSocket does not allow overlapping sends, so startup, reconnect, and shutdown all share
                // this serialized path.
                await _webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
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
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();
        Exception? terminalException = null;

        try
        {
            while (!_disposeCts.IsCancellationRequested && _webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await _webSocket.ReceiveAsync(buffer, _disposeCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    terminalException = CreateUnexpectedConnectionClosureException(result);
                    break;
                }

                // Large CDP events can span multiple websocket frames. Buffer until EndOfMessage so protocol parsing
                // always sees one complete JSON message, matching the frames observed from a real browser.
                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = messageBuffer.ToArray();
                messageBuffer.SetLength(0);

                _logger.LogTrace("Tracked browser protocol <- {Frame}", BrowserLogsCdpProtocol.DescribeFrame(frame));

                try
                {
                    await HandleFrameAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    terminalException = new InvalidOperationException(
                        $"Tracked browser protocol receive loop failed while processing frame {BrowserLogsCdpProtocol.DescribeFrame(frame)}.",
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
        var header = BrowserLogsCdpProtocol.ParseMessageHeader(frame);
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

        if (header.Method is not null && BrowserLogsCdpProtocol.ParseEvent(header, frame) is { } protocolEvent)
        {
            await _eventHandler(protocolEvent).ConfigureAwait(false);
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

// Thin ownership wrapper around ClientWebSocket. It lets BrowserLogsCdpConnection transfer the connected socket into
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
