// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class BrowserLogsCdpConnectionTests
{
    [Fact]
    public async Task ConnectAsync_DisposesConnectorWhenConnectFails()
    {
        var connectException = new WebSocketException("Connection refused");
        var connector = new ThrowingClientWebSocketConnector(connectException);

        var exception = await Assert.ThrowsAsync<WebSocketException>(() => BrowserLogsCdpConnection.ConnectAsync(
            new Uri("ws://127.0.0.1:12345/devtools/browser/test"),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            CancellationToken.None,
            () => connector));

        Assert.Same(connectException, exception);
        Assert.True(connector.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(15), connector.KeepAliveInterval);
    }

    [Fact]
    public async Task ConnectAsync_CorrelatesOutOfOrderResponsesAndRoutesEventsWhileCommandIsPending()
    {
        await using var pair = InMemoryWebSocketPair.Create();
        var connector = new ConnectedClientWebSocketConnector(pair.ClientSocket);
        var routedEventSource = new TaskCompletionSource<BrowserLogsCdpProtocolEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = await BrowserLogsCdpConnection.ConnectAsync(
            new Uri("ws://127.0.0.1/devtools/browser/test"),
            protocolEvent =>
            {
                routedEventSource.TrySetResult(protocolEvent);
                return ValueTask.CompletedTask;
            },
            NullLogger<BrowserLogsSessionManager>.Instance,
            CancellationToken.None,
            () => connector);

        var createTargetTask = connection.CreateTargetAsync(CancellationToken.None);
        var attachToTargetTask = connection.AttachToTargetAsync("target-1", CancellationToken.None);

        var firstCommand = await ReceiveCommandAsync(pair.ServerSocket).DefaultTimeout();
        var secondCommand = await ReceiveCommandAsync(pair.ServerSocket).DefaultTimeout();
        var createTargetCommand = Assert.Single(new[] { firstCommand, secondCommand }, static command => command.Method == BrowserLogsCdpProtocol.TargetCreateTargetMethod);
        var attachToTargetCommand = Assert.Single(new[] { firstCommand, secondCommand }, static command => command.Method == BrowserLogsCdpProtocol.TargetAttachToTargetMethod);
        Assert.Null(createTargetCommand.SessionId);
        Assert.Equal("about:blank", createTargetCommand.Url);
        Assert.Null(attachToTargetCommand.SessionId);
        Assert.Equal("target-1", attachToTargetCommand.TargetId);

        await SendTextAsync(
            pair.ServerSocket,
            """
            {
              "method": "Runtime.consoleAPICalled",
              "sessionId": "target-session-1",
              "params": {
                "type": "log",
                "args": []
              }
            }
            """).DefaultTimeout();

        var routedEvent = Assert.IsType<BrowserLogsConsoleApiCalledEvent>(await routedEventSource.Task.DefaultTimeout());
        Assert.Equal("target-session-1", routedEvent.SessionId);
        Assert.Equal("log", routedEvent.Parameters.Type);

        await SendTextAsync(
            pair.ServerSocket,
            $$"""
            {
              "id": {{attachToTargetCommand.Id}},
              "result": {
                "sessionId": "attached-session"
              }
            }
            """).DefaultTimeout();
        await SendTextAsync(
            pair.ServerSocket,
            $$"""
            {
              "id": {{createTargetCommand.Id}},
              "result": {
                "targetId": "created-target"
              }
            }
            """).DefaultTimeout();

        var createTargetResult = await createTargetTask.DefaultTimeout();
        var attachToTargetResult = await attachToTargetTask.DefaultTimeout();
        Assert.Equal("created-target", createTargetResult.TargetId);
        Assert.Equal("attached-session", attachToTargetResult.SessionId);
        Assert.True(connector.Disposed);

        await pair.ServerSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task CaptureScreenshotAsync_SendsPageCaptureScreenshotForTargetSession()
    {
        await using var pair = InMemoryWebSocketPair.Create();
        var connector = new ConnectedClientWebSocketConnector(pair.ClientSocket);
        await using var connection = await BrowserLogsCdpConnection.ConnectAsync(
            new Uri("ws://127.0.0.1/devtools/browser/test"),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            CancellationToken.None,
            () => connector);

        var captureTask = connection.CaptureScreenshotAsync("target-session-1", CancellationToken.None);

        var command = await ReceiveCommandAsync(pair.ServerSocket).DefaultTimeout();
        Assert.Equal(BrowserLogsCdpProtocol.PageCaptureScreenshotMethod, command.Method);
        Assert.Equal("target-session-1", command.SessionId);
        Assert.Equal("png", command.Format);
        Assert.Equal(true, command.FromSurface);

        await SendTextAsync(
            pair.ServerSocket,
            $$"""
            {
              "id": {{command.Id}},
              "result": {
                "data": "aW1hZ2UtZGF0YQ=="
              }
            }
            """).DefaultTimeout();

        var result = await captureTask.DefaultTimeout();
        Assert.Equal("aW1hZ2UtZGF0YQ==", result.Data);

        await pair.ServerSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task EvaluateAsync_SendsRuntimeEvaluateForTargetSession()
    {
        await using var pair = InMemoryWebSocketPair.Create();
        var connector = new ConnectedClientWebSocketConnector(pair.ClientSocket);
        await using var connection = await BrowserLogsCdpConnection.ConnectAsync(
            new Uri("ws://127.0.0.1/devtools/browser/test"),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            CancellationToken.None,
            () => connector);

        var evaluateTask = connection.EvaluateAsync("target-session-1", "1 + 1", timeout: null, CancellationToken.None);

        var command = await ReceiveCommandAsync(pair.ServerSocket).DefaultTimeout();
        Assert.Equal(BrowserLogsCdpProtocol.RuntimeEvaluateMethod, command.Method);
        Assert.Equal("target-session-1", command.SessionId);
        Assert.Equal("1 + 1", command.Expression);
        Assert.True(command.AwaitPromise);
        Assert.True(command.ReturnByValue);
        Assert.True(command.UserGesture);

        await SendTextAsync(
            pair.ServerSocket,
            $$"""
            {
              "id": {{command.Id}},
              "result": {
                "result": {
                  "type": "string",
                  "value": "{\"value\":2}"
                }
              }
            }
            """).DefaultTimeout();

        var result = await evaluateTask.DefaultTimeout();
        var value = Assert.IsType<BrowserLogsCdpProtocolStringValue>(result.Result?.Value);
        Assert.Equal("""{"value":2}""", value.Value);

        await pair.ServerSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task CreateWithPipeTransport_UsesNullDelimitedFrames()
    {
        var appToBrowser = new Pipe();
        var browserToApp = new Pipe();
        await using var browserRead = appToBrowser.Reader.AsStream();
        await using var browserWrite = browserToApp.Writer.AsStream();
        var routedEventSource = new TaskCompletionSource<BrowserLogsCdpProtocolEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = BrowserLogsCdpConnection.Create(
            new BrowserLogsPipeCdpTransport(browserToApp.Reader.AsStream(), appToBrowser.Writer.AsStream()),
            protocolEvent =>
            {
                routedEventSource.TrySetResult(protocolEvent);
                return ValueTask.CompletedTask;
            },
            NullLogger<BrowserLogsSessionManager>.Instance);

        var createTargetTask = connection.CreateTargetAsync(CancellationToken.None);
        var command = ParseReceivedCommand(await ReceiveNullTerminatedFrameAsync(browserRead).DefaultTimeout());
        Assert.Equal(BrowserLogsCdpProtocol.TargetCreateTargetMethod, command.Method);
        Assert.Equal("about:blank", command.Url);

        await SendNullTerminatedFramesAsync(
            browserWrite,
            """
            {
              "method": "Runtime.consoleAPICalled",
              "sessionId": "target-session-1",
              "params": {
                "type": "log",
                "args": []
              }
            }
            """,
            $$"""
            {
              "id": {{command.Id}},
              "result": {
                "targetId": "created-target"
              }
            }
            """).DefaultTimeout();

        var routedEvent = Assert.IsType<BrowserLogsConsoleApiCalledEvent>(await routedEventSource.Task.DefaultTimeout());
        Assert.Equal("target-session-1", routedEvent.SessionId);
        Assert.Equal("log", routedEvent.Parameters.Type);

        var result = await createTargetTask.DefaultTimeout();
        Assert.Equal("created-target", result.TargetId);
    }

    [Fact]
    public async Task MultiplexerLeasesShareCommandsAndBroadcastEvents()
    {
        FakeSharedCdpConnection? innerConnection = null;
        await using var multiplexer = new BrowserLogsCdpConnectionMultiplexer(
            eventHandler =>
            {
                innerConnection = new FakeSharedCdpConnection(eventHandler);
                return innerConnection;
            },
            NullLogger<BrowserLogsSessionManager>.Instance);

        var firstEvents = new List<BrowserLogsCdpProtocolEvent>();
        var secondEvents = new List<BrowserLogsCdpProtocolEvent>();
        await using var firstConnection = multiplexer.CreateConnection(protocolEvent =>
        {
            firstEvents.Add(protocolEvent);
            return ValueTask.CompletedTask;
        });
        await using var secondConnection = multiplexer.CreateConnection(protocolEvent =>
        {
            secondEvents.Add(protocolEvent);
            return ValueTask.CompletedTask;
        });

        var result = await firstConnection.CreateTargetAsync(CancellationToken.None);
        Assert.Equal("created-target", result.TargetId);
        Assert.Equal(1, innerConnection!.CreateTargetCount);

        var firstEvent = CreateConsoleEvent("target-session-1");
        await innerConnection.RaiseEventAsync(firstEvent);
        Assert.Same(firstEvent, Assert.Single(firstEvents));
        Assert.Same(firstEvent, Assert.Single(secondEvents));

        await firstConnection.DisposeAsync();
        await firstConnection.Completion.DefaultTimeout();
        Assert.False(innerConnection.Disposed);

        var secondEvent = CreateConsoleEvent("target-session-2");
        await innerConnection.RaiseEventAsync(secondEvent);
        Assert.Single(firstEvents);
        Assert.Equal(2, secondEvents.Count);
        Assert.Same(secondEvent, secondEvents[1]);
        Assert.False(secondConnection.Completion.IsCompleted);
    }

    [Fact]
    public async Task MultiplexerFaultsOnlyFailingSubscriberWhenEventHandlerThrows()
    {
        FakeSharedCdpConnection? innerConnection = null;
        await using var multiplexer = new BrowserLogsCdpConnectionMultiplexer(
            eventHandler =>
            {
                innerConnection = new FakeSharedCdpConnection(eventHandler);
                return innerConnection;
            },
            NullLogger<BrowserLogsSessionManager>.Instance);

        await using var failingConnection = multiplexer.CreateConnection(_ => throw new InvalidOperationException("boom"));
        var survivingEvents = new List<BrowserLogsCdpProtocolEvent>();
        await using var survivingConnection = multiplexer.CreateConnection(protocolEvent =>
        {
            survivingEvents.Add(protocolEvent);
            return ValueTask.CompletedTask;
        });

        var protocolEvent = CreateConsoleEvent("target-session-1");
        await innerConnection!.RaiseEventAsync(protocolEvent);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failingConnection.Completion.DefaultTimeout());
        Assert.Equal("Tracked browser CDP event handler failed.", exception.Message);
        Assert.Same(protocolEvent, Assert.Single(survivingEvents));
        Assert.False(survivingConnection.Completion.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingConnection.CreateTargetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MultiplexerRejectsNewLeasesAfterInnerConnectionCompletes()
    {
        FakeSharedCdpConnection? innerConnection = null;
        await using var multiplexer = new BrowserLogsCdpConnectionMultiplexer(
            eventHandler =>
            {
                innerConnection = new FakeSharedCdpConnection(eventHandler);
                return innerConnection;
            },
            NullLogger<BrowserLogsSessionManager>.Instance);

        innerConnection!.Complete();
        await multiplexer.Completion.DefaultTimeout();

        var exception = Assert.Throws<InvalidOperationException>(() => multiplexer.CreateConnection(static _ => ValueTask.CompletedTask));
        Assert.Equal("Tracked browser CDP pipe is no longer active.", exception.Message);
    }

    private static async Task<ReceivedCommand> ReceiveCommandAsync(WebSocket socket)
    {
        using var document = await ReceiveJsonDocumentAsync(socket).DefaultTimeout();
        return ParseReceivedCommand(document.RootElement);
    }

    private static ReceivedCommand ParseReceivedCommand(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseReceivedCommand(document.RootElement);
    }

    private static ReceivedCommand ParseReceivedCommand(JsonElement root)
    {
        var id = root.GetProperty("id").GetInt64();
        var method = root.GetProperty("method").GetString()!;
        var sessionId = root.TryGetProperty("sessionId", out var sessionIdElement)
            ? sessionIdElement.GetString()
            : null;
        JsonElement? parameters = root.TryGetProperty("params", out var parametersElement)
            ? parametersElement
            : null;
        var targetId = parameters?.TryGetProperty("targetId", out var targetIdElement) == true
            ? targetIdElement.GetString()
            : null;
        var url = parameters?.TryGetProperty("url", out var urlElement) == true
            ? urlElement.GetString()
            : null;
        var format = parameters?.TryGetProperty("format", out var formatElement) == true
            ? formatElement.GetString()
            : null;
        var fromSurface = parameters?.TryGetProperty("fromSurface", out var fromSurfaceElement) == true
            ? fromSurfaceElement.GetBoolean()
            : (bool?)null;
        var expression = parameters?.TryGetProperty("expression", out var expressionElement) == true
            ? expressionElement.GetString()
            : null;
        var awaitPromise = parameters?.TryGetProperty("awaitPromise", out var awaitPromiseElement) == true
            ? awaitPromiseElement.GetBoolean()
            : (bool?)null;
        var returnByValue = parameters?.TryGetProperty("returnByValue", out var returnByValueElement) == true
            ? returnByValueElement.GetBoolean()
            : (bool?)null;
        var userGesture = parameters?.TryGetProperty("userGesture", out var userGestureElement) == true
            ? userGestureElement.GetBoolean()
            : (bool?)null;

        return new ReceivedCommand(id, method, sessionId, targetId, url, format, fromSurface, expression, awaitPromise, returnByValue, userGesture);
    }

    private static BrowserLogsConsoleApiCalledEvent CreateConsoleEvent(string sessionId)
    {
        return new BrowserLogsConsoleApiCalledEvent(
            sessionId,
            new BrowserLogsRuntimeConsoleApiCalledParameters
            {
                Type = "log",
                Args = []
            });
    }

    private static async Task<JsonDocument> ReceiveJsonDocumentAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        using var messageBuffer = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None).DefaultTimeout();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The in-memory websocket closed before a JSON message was received.");
            }

            messageBuffer.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(messageBuffer.ToArray());
            }
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text)
    {
        return socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<byte[]> ReceiveNullTerminatedFrameAsync(Stream stream)
    {
        using var frame = new MemoryStream();
        var oneByte = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(oneByte);
            if (read == 0)
            {
                throw new EndOfStreamException("The stream closed before a null-terminated frame was received.");
            }

            if (oneByte[0] == 0)
            {
                return frame.ToArray();
            }

            frame.WriteByte(oneByte[0]);
        }
    }

    private static async Task SendNullTerminatedFramesAsync(Stream stream, params string[] frames)
    {
        foreach (var frame in frames)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(frame));
            await stream.WriteAsync(new byte[] { 0 });
        }

        await stream.FlushAsync();
    }

    private sealed record ReceivedCommand(
        long Id,
        string Method,
        string? SessionId,
        string? TargetId,
        string? Url,
        string? Format,
        bool? FromSurface,
        string? Expression,
        bool? AwaitPromise,
        bool? ReturnByValue,
        bool? UserGesture);

    private sealed class FakeSharedCdpConnection(Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler) : IBrowserLogsCdpConnection
    {
        private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateTargetCount { get; private set; }

        public bool Disposed { get; private set; }

        public Task Completion => _completionSource.Task;

        public ValueTask RaiseEventAsync(BrowserLogsCdpProtocolEvent protocolEvent)
        {
            return eventHandler(protocolEvent);
        }

        public void Complete()
        {
            _completionSource.TrySetResult();
        }

        public Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
        {
            CreateTargetCount++;
            return Task.FromResult(new BrowserLogsCreateTargetResult { TargetId = "created-target" });
        }

        public Task<BrowserLogsGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new BrowserLogsGetTargetsResult { TargetInfos = [] });
        }

        public Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new BrowserLogsAttachToTargetResult { SessionId = "attached-session" });
        }

        public Task<BrowserLogsCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            return Task.FromResult(BrowserLogsCommandAck.Instance);
        }

        public Task<BrowserLogsCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(BrowserLogsCommandAck.Instance);
        }

        public Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new BrowserLogsCaptureScreenshotResult { Data = "image-data" });
        }

        public Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
        {
            return Task.FromResult(BrowserLogsCommandAck.Instance);
        }

        public Task<BrowserLogsRuntimeEvaluateResult> EvaluateAsync(string sessionId, string expression, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            return Task.FromResult(new BrowserLogsRuntimeEvaluateResult
            {
                Result = new BrowserLogsCdpProtocolRemoteObject
                {
                    Value = new BrowserLogsCdpProtocolStringValue("""{"action":"evaluate"}""")
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completionSource.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConnectedClientWebSocketConnector(WebSocket webSocket) : IClientWebSocketConnector
    {
        private readonly WebSocket _webSocket = webSocket;

        public bool Disposed { get; private set; }

        public TimeSpan? KeepAliveInterval { get; private set; }

        public void SetKeepAliveInterval(TimeSpan interval)
        {
            KeepAliveInterval = interval;
        }

        public Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public WebSocket DetachConnectedWebSocket()
        {
            return _webSocket;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class ThrowingClientWebSocketConnector(Exception connectException) : IClientWebSocketConnector
    {
        public bool Disposed { get; private set; }

        public TimeSpan? KeepAliveInterval { get; private set; }

        public void SetKeepAliveInterval(TimeSpan interval)
        {
            KeepAliveInterval = interval;
        }

        public Task ConnectAsync(Uri webSocketUri, CancellationToken cancellationToken)
        {
            return Task.FromException(connectException);
        }

        public WebSocket DetachConnectedWebSocket()
        {
            throw new InvalidOperationException("A failed connect should not detach a websocket.");
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class InMemoryWebSocketPair : IAsyncDisposable
    {
        private readonly DuplexPipeStream _clientStream;
        private readonly DuplexPipeStream _serverStream;

        private InMemoryWebSocketPair(DuplexPipeStream clientStream, DuplexPipeStream serverStream)
        {
            _clientStream = clientStream;
            _serverStream = serverStream;
            ClientSocket = WebSocket.CreateFromStream(clientStream, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
            ServerSocket = WebSocket.CreateFromStream(serverStream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
        }

        public WebSocket ClientSocket { get; }

        public WebSocket ServerSocket { get; }

        public static InMemoryWebSocketPair Create()
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            return new InMemoryWebSocketPair(
                new DuplexPipeStream(serverToClient.Reader, clientToServer.Writer),
                new DuplexPipeStream(clientToServer.Reader, serverToClient.Writer));
        }

        public async ValueTask DisposeAsync()
        {
            ClientSocket.Dispose();
            ServerSocket.Dispose();
            await _clientStream.DisposeAsync();
            await _serverStream.DisposeAsync();
        }
    }

    private sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await writer.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken);
                var readableBuffer = result.Buffer;
                if (readableBuffer.Length > 0)
                {
                    var count = (int)Math.Min(readableBuffer.Length, buffer.Length);
                    var consumed = readableBuffer.GetPosition(count);
                    readableBuffer.Slice(0, count).CopyTo(buffer.Span);
                    reader.AdvanceTo(consumed);
                    return count;
                }

                reader.AdvanceTo(readableBuffer.Start, readableBuffer.End);
                if (result.IsCompleted)
                {
                    return 0;
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writer.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                reader.Complete();
                writer.Complete();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await reader.CompleteAsync();
                await writer.CompleteAsync();
            }

            await base.DisposeAsync();
        }
    }
}
