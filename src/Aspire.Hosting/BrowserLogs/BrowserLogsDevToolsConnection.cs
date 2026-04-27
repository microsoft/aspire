// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Owns the browser-level websocket only. Protocol parsing stays in BrowserLogsProtocol and
// higher-level recovery stays in BrowserLogsRunningSession.
internal sealed class ChromeDevToolsConnection : IAsyncDisposable
{
    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Func<BrowserLogsProtocolEvent, ValueTask> _eventHandler;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly ConcurrentDictionary<long, IPendingCommand> _pendingCommands = new();
    private readonly Task _receiveLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ClientWebSocket _webSocket;
    private long _nextCommandId;

    private ChromeDevToolsConnection(ClientWebSocket webSocket, Func<BrowserLogsProtocolEvent, ValueTask> eventHandler, ILogger<BrowserLogsSessionManager> logger)
    {
        _eventHandler = eventHandler;
        _logger = logger;
        _webSocket = webSocket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public Task Completion => _receiveLoop;

    public static async Task<ChromeDevToolsConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
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

    internal static async Task<ChromeDevToolsConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken,
        Func<IClientWebSocketConnector> connectorFactory)
    {
        using var connector = connectorFactory();
        connector.SetKeepAliveInterval(TimeSpan.FromSeconds(15));
        await connector.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
        return new ChromeDevToolsConnection(connector.DetachConnectedWebSocket(), eventHandler, logger);
    }

    public Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsProtocol.TargetCreateTargetMethod,
            sessionId: null,
            static writer => writer.WriteString("url", "about:blank"),
            BrowserLogsProtocol.ParseCreateTargetResponse,
            cancellationToken);
    }

    public Task<BrowserLogsGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsProtocol.TargetGetTargetsMethod,
            sessionId: null,
            writeParameters: null,
            BrowserLogsProtocol.ParseGetTargetsResponse,
            cancellationToken);
    }

    public Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsProtocol.TargetAttachToTargetMethod,
            sessionId: null,
            writer =>
            {
                writer.WriteString("targetId", targetId);
                writer.WriteBoolean("flatten", true);
            },
            BrowserLogsProtocol.ParseAttachToTargetResponse,
            cancellationToken);
    }

    public async Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
    {
        await SendCommandAsync(BrowserLogsProtocol.RuntimeEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserLogsProtocol.LogEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserLogsProtocol.PageEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync(BrowserLogsProtocol.NetworkEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
    }

    public Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsProtocol.PageNavigateMethod,
            sessionId,
            writer => writer.WriteString("url", url.ToString()),
            BrowserLogsProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public Task<BrowserLogsCommandAck> CloseBrowserAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(
            BrowserLogsProtocol.BrowserCloseMethod,
            sessionId: null,
            writeParameters: null,
            BrowserLogsProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var commandId = Interlocked.Increment(ref _nextCommandId);
        var pendingCommand = new PendingCommand<TResult>(parseResponse);
        _pendingCommands[commandId] = pendingCommand;

        try
        {
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            sendCts.CancelAfter(s_commandTimeout);

            using var registration = sendCts.Token.Register(static state =>
            {
                ((IPendingCommand)state!).SetCanceled();
            }, pendingCommand);

            var payload = BrowserLogsProtocol.CreateCommandFrame(commandId, method, sessionId, writeParameters);
            _logger.LogTrace("Tracked browser protocol -> {Frame}", BrowserLogsProtocol.DescribeFrame(payload));

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

                messageBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = messageBuffer.ToArray();
                messageBuffer.SetLength(0);

                _logger.LogTrace("Tracked browser protocol <- {Frame}", BrowserLogsProtocol.DescribeFrame(frame));

                try
                {
                    await HandleFrameAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    terminalException = new InvalidOperationException(
                        $"Tracked browser protocol receive loop failed while processing frame {BrowserLogsProtocol.DescribeFrame(frame)}.",
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
        var header = BrowserLogsProtocol.ParseMessageHeader(frame);
        if (header.Id is long commandId)
        {
            if (_pendingCommands.TryGetValue(commandId, out var pendingCommand))
            {
                pendingCommand.SetResult(frame);
            }

            return;
        }

        if (header.Method is not null && BrowserLogsProtocol.ParseEvent(header, frame) is { } protocolEvent)
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

internal interface IClientWebSocketConnector : IDisposable
{
    void SetKeepAliveInterval(TimeSpan interval);

    Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken);

    ClientWebSocket DetachConnectedWebSocket();
}

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

    public ClientWebSocket DetachConnectedWebSocket()
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
