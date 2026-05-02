// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class SafariBrowserLogsTests
{
    [Fact]
    public void BrowserResolver_IdentifiesSafariLogicalNames()
    {
        Assert.Equal(BrowserLogsBrowserFamily.Safari, BrowserLogsBrowserResolver.ResolveFamily("safari"));
        Assert.Equal(BrowserLogsBrowserFamily.Safari, BrowserLogsBrowserResolver.ResolveFamily("safari-technology-preview"));
        Assert.Equal(BrowserLogsBrowserFamily.Chromium, BrowserLogsBrowserResolver.ResolveFamily("chrome"));
    }

    [Fact]
    public void SafariResolver_ResolvesExplicitSafariDriverPath()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var driverPath = Path.Combine(directory.FullName, "safaridriver");
            File.WriteAllText(driverPath, string.Empty);

            var driver = SafariBrowserResolver.TryResolveDriver(driverPath);

            Assert.NotNull(driver);
            Assert.Equal(driverPath, driver.DriverPath);
            Assert.Equal("Safari", driver.DisplayName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BrowserEventLogger_LogsNormalizedNetworkEvents()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceLogger = resourceLoggerService.GetLogger("web-browser-logs");
        var eventLogger = new BrowserEventLogger("session-0001", resourceLogger);
        var logs = await ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, "web-browser-logs", targetLogCount: 1, () =>
        {
            eventLogger.HandleEvent(new BrowserNetworkRequestStartedDiagnosticEvent(
                "request-1",
                "GET",
                "https://example.test/api/todos",
                "fetch",
                Timestamp: 1.5,
                RedirectResponse: null));
            eventLogger.HandleEvent(new BrowserNetworkResponseReceivedDiagnosticEvent(
                "request-1",
                "fetch",
                new BrowserNetworkResponseDetails(
                    "https://example.test/api/todos",
                    Status: 200,
                    StatusText: "OK",
                    FromDiskCache: false,
                    FromServiceWorker: false)));
            eventLogger.HandleEvent(new BrowserNetworkRequestCompletedDiagnosticEvent(
                "request-1",
                Timestamp: 1.75,
                EncodedDataLength: 1024));
        });

        var log = Assert.Single(logs);
        Assert.Equal("2000-12-29T20:59:59.0000000Z [session-0001] [network.fetch] GET https://example.test/api/todos -> 200 OK (250 ms, 1024 B)", log.Content);
    }

    [Fact]
    public async Task SafariWebDriverClient_RequestsWebSocketUrlCapability()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "value": {
                    "sessionId": "webdriver-session-1",
                    "capabilities": {
                      "webSocketUrl": "ws://127.0.0.1:12345/session/webdriver-session-1"
                    }
                  }
                }
                """, Encoding.UTF8, "application/json")
        });
        using var client = new SafariWebDriverClient(new Uri("http://127.0.0.1:9999/"), "/usr/bin/safaridriver", new HttpClient(handler));
        var driverProcessCompletion = new TaskCompletionSource<BrowserLogsProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = await client.CreateSessionAsync(driverProcessCompletion.Task, CancellationToken.None);

        Assert.Equal("webdriver-session-1", session.SessionId);
        Assert.Equal(new Uri("ws://127.0.0.1:12345/session/webdriver-session-1"), session.WebSocketUrl);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(new Uri("http://127.0.0.1:9999/session"), handler.Requests[0].RequestUri);
        using var document = JsonDocument.Parse(handler.RequestBodies[0]);
        var alwaysMatch = document.RootElement.GetProperty("capabilities").GetProperty("alwaysMatch");
        Assert.Equal("safari", alwaysMatch.GetProperty("browserName").GetString());
        Assert.True(alwaysMatch.GetProperty("acceptInsecureCerts").GetBoolean());
        Assert.True(alwaysMatch.GetProperty("webSocketUrl").GetBoolean());
    }

    [Fact]
    public async Task SafariWebDriverClient_ThrowsWhenWebSocketUrlIsMissing()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "value": {
                    "sessionId": "webdriver-session-1",
                    "capabilities": {
                    }
                  }
                }
                """, Encoding.UTF8, "application/json")
        });
        using var client = new SafariWebDriverClient(new Uri("http://127.0.0.1:9999/"), "/usr/bin/safaridriver", new HttpClient(handler));
        var driverProcessCompletion = new TaskCompletionSource<BrowserLogsProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateSessionAsync(driverProcessCompletion.Task, CancellationToken.None));

        Assert.Contains("require WebDriver BiDi support", exception.Message);
    }

    [Fact]
    public async Task SafariWebDriverClient_DeletesWebDriverSession()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":null}""", Encoding.UTF8, "application/json")
        });
        using var client = new SafariWebDriverClient(new Uri("http://127.0.0.1:9999/"), "/usr/bin/safaridriver", new HttpClient(handler));

        await client.DeleteSessionAsync("session 1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal(new Uri("http://127.0.0.1:9999/session/session%201"), handler.Requests[0].RequestUri);
    }

    [Fact]
    public void BrowserLogsBidiProtocol_ParseLogEntryAdded_ReturnsContextAndParameters()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "method": "log.entryAdded",
              "params": {
                "type": "console",
                "method": "warn",
                "level": "warn",
                "text": "hello from safari",
                "source": {
                  "context": "context-1"
                }
              }
            }
            """);

        var @event = Assert.IsType<BrowserLogsBidiLogEntryAddedEvent>(
            BrowserLogsBidiProtocol.ParseEvent(BrowserLogsBidiProtocol.ParseMessageHeader(payload), payload));

        Assert.Equal("context-1", @event.Context);
        Assert.Equal("warn", @event.Parameters.Method);
        Assert.Equal("hello from safari", @event.Parameters.Text);
    }

    [Fact]
    public void BrowserLogsBidiProtocol_CommandParserIncludesProtocolErrorDetails()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "type": "error",
              "id": 7,
              "error": "invalid argument",
              "message": "Context is missing"
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserLogsBidiProtocol.ParseCommandAckResponse(payload));

        Assert.Contains("Context is missing", exception.Message);
        Assert.Contains("invalid argument", exception.Message);
    }

    [Fact]
    public async Task BrowserLogsBidiConnection_SendsCommandsAndRoutesEvents()
    {
        await using var pair = InMemoryWebSocketPair.Create();
        var routedEventSource = new TaskCompletionSource<BrowserLogsBidiProtocolEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = BrowserLogsBidiConnection.Create(
            pair.ClientSocket,
            protocolEvent =>
            {
                routedEventSource.TrySetResult(protocolEvent);
                return ValueTask.CompletedTask;
            },
            NullLogger<BrowserLogsSessionManager>.Instance);

        var createContextTask = connection.CreateBrowsingContextAsync(CancellationToken.None);
        var command = await ReceiveCommandAsync(pair.ServerSocket).DefaultTimeout();
        Assert.Equal(BrowserLogsBidiProtocol.BrowsingContextCreateMethod, command.Method);
        Assert.Equal("tab", command.Parameters.GetProperty("type").GetString());

        await SendTextAsync(
            pair.ServerSocket,
            $$"""
            {
              "id": {{command.Id}},
              "result": {
                "context": "context-1"
              }
            }
            """).DefaultTimeout();

        var result = await createContextTask.DefaultTimeout();
        Assert.Equal("context-1", result.Context);

        await SendTextAsync(
            pair.ServerSocket,
            """
            {
              "method": "network.beforeRequestSent",
              "params": {
                "context": "context-1",
                "timestamp": 1714520000000,
                "request": {
                  "request": "request-1",
                  "method": "GET",
                  "url": "https://example.test/"
                }
              }
            }
            """).DefaultTimeout();

        var routedEvent = Assert.IsType<BrowserLogsBidiNetworkBeforeRequestSentEvent>(await routedEventSource.Task.DefaultTimeout());
        Assert.Equal("context-1", routedEvent.Context);
        Assert.Equal("request-1", routedEvent.Parameters.Request?.Request);

        await pair.ServerSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None).DefaultTimeout();
    }

    private static async Task<ReceivedBidiCommand> ReceiveCommandAsync(WebSocket socket)
    {
        using var document = await ReceiveJsonDocumentAsync(socket).DefaultTimeout();
        var root = document.RootElement;
        return new ReceivedBidiCommand(
            root.GetProperty("id").GetInt64(),
            root.GetProperty("method").GetString()!,
            root.GetProperty("params").Clone());
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

    private sealed record ReceivedBidiCommand(long Id, string Method, JsonElement Parameters);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> getResponse) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return getResponse(request);
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

        public override int Read(Span<byte> buffer)
        {
            return ReadAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();
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
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await writer.CompleteAsync();
            await reader.CompleteAsync();
            GC.SuppressFinalize(this);
        }
    }
}
