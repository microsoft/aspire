// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// WebDriver BiDi protocol references:
// - Protocol model and message envelopes: https://w3c.github.io/webdriver-bidi/#protocol
// - Session subscription command: https://w3c.github.io/webdriver-bidi/#command-session-subscribe
// - Browsing context commands/events: https://w3c.github.io/webdriver-bidi/#module-browsingContext
// - Log events: https://w3c.github.io/webdriver-bidi/#module-log
// - Network events: https://w3c.github.io/webdriver-bidi/#module-network
//
// WebDriver BiDi frames are JSON websocket text messages shaped like:
// - command request:  { "id": 1, "method": "...", "params": { ... } }
// - command success:  { "type": "success", "id": 1, "result": { ... } }
// - command error:    { "type": "error", "id": 1, "error": "...", "message": "..." }
// - event:            { "type": "event", "method": "...", "params": { ... } }
//
// Keep this layer focused on BiDi frame serialization/parsing. Safari page orchestration and event normalization live
// in SafariBidiPageSession so the protocol parser can be tested without starting Safari or safaridriver.
internal interface IBrowserLogsBidiConnection : IAsyncDisposable
{
    Task Completion { get; }

    Task<BrowserLogsBidiCreateContextResult> CreateBrowsingContextAsync(CancellationToken cancellationToken);

    Task SubscribeAsync(string context, CancellationToken cancellationToken);

    Task<BrowserLogsBidiCommandAck> NavigateAsync(string context, Uri url, CancellationToken cancellationToken);

    Task<BrowserLogsBidiCaptureScreenshotResult> CaptureScreenshotAsync(string context, CancellationToken cancellationToken);

    Task<BrowserLogsBidiCommandAck> CloseBrowsingContextAsync(string context, CancellationToken cancellationToken);
}

internal sealed class BrowserLogsBidiConnection : IBrowserLogsBidiConnection
{
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_screenshotCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_keepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Func<BrowserLogsBidiProtocolEvent, ValueTask> _eventHandler;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly ConcurrentDictionary<long, IPendingCommand> _pendingCommands = new();
    private readonly Task _receiveLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly BrowserLogsWebSocketCdpTransport _transport;
    private long _nextCommandId;

    private BrowserLogsBidiConnection(BrowserLogsWebSocketCdpTransport transport, Func<BrowserLogsBidiProtocolEvent, ValueTask> eventHandler, ILogger<BrowserLogsSessionManager> logger)
    {
        _eventHandler = eventHandler;
        _logger = logger;
        _transport = transport;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public Task Completion => _receiveLoop;

    public static async Task<BrowserLogsBidiConnection> ConnectAsync(
        Uri webSocketUri,
        Func<BrowserLogsBidiProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken)
    {
        using var connector = new ClientWebSocketConnector();
        connector.SetKeepAliveInterval(s_keepAliveInterval);
        await connector.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
        return new BrowserLogsBidiConnection(
            new BrowserLogsWebSocketCdpTransport(connector.DetachConnectedWebSocket(), s_closeTimeout),
            eventHandler,
            logger);
    }

    internal static BrowserLogsBidiConnection Create(
        WebSocket webSocket,
        Func<BrowserLogsBidiProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger)
    {
        return new BrowserLogsBidiConnection(
            new BrowserLogsWebSocketCdpTransport(webSocket, s_closeTimeout),
            eventHandler,
            logger);
    }

    public Task<BrowserLogsBidiCreateContextResult> CreateBrowsingContextAsync(CancellationToken cancellationToken)
    {
        // browsingContext.create request:
        // {
        //   "id": 1,
        //   "method": "browsingContext.create",
        //   "params": { "type": "tab" }
        // }
        return SendCommandAsync(
            BrowserLogsBidiProtocol.BrowsingContextCreateMethod,
            static writer =>
            {
                writer.WriteString("type", "tab");
            },
            BrowserLogsBidiProtocol.ParseCreateContextResponse,
            cancellationToken);
    }

    public Task SubscribeAsync(string context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        // session.subscribe request:
        // {
        //   "id": 2,
        //   "method": "session.subscribe",
        //   "params": {
        //     "events": [
        //       "log.entryAdded",
        //       "network.beforeRequestSent",
        //       "network.responseStarted",
        //       "network.responseCompleted",
        //       "network.fetchError",
        //       "browsingContext.contextDestroyed"
        //     ],
        //     "contexts": [ "context-id" ]
        //   }
        // }
        //
        // The context filter keeps BrowserLogs output scoped to the page opened for this resource instead of every
        // automated Safari context in the same WebDriver session.
        return SendCommandAsync(
            BrowserLogsBidiProtocol.SessionSubscribeMethod,
            writer =>
            {
                writer.WritePropertyName("events");
                writer.WriteStartArray();
                writer.WriteStringValue(BrowserLogsBidiProtocol.LogEntryAddedMethod);
                writer.WriteStringValue(BrowserLogsBidiProtocol.NetworkBeforeRequestSentMethod);
                writer.WriteStringValue(BrowserLogsBidiProtocol.NetworkResponseStartedMethod);
                writer.WriteStringValue(BrowserLogsBidiProtocol.NetworkResponseCompletedMethod);
                writer.WriteStringValue(BrowserLogsBidiProtocol.NetworkFetchErrorMethod);
                writer.WriteStringValue(BrowserLogsBidiProtocol.BrowsingContextContextDestroyedMethod);
                writer.WriteEndArray();

                writer.WritePropertyName("contexts");
                writer.WriteStartArray();
                writer.WriteStringValue(context);
                writer.WriteEndArray();
            },
            BrowserLogsBidiProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public Task<BrowserLogsBidiCommandAck> NavigateAsync(string context, Uri url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(url);

        // browsingContext.navigate request:
        // {
        //   "id": 3,
        //   "method": "browsingContext.navigate",
        //   "params": {
        //     "context": "context-id",
        //     "url": "https://localhost:5001/",
        //     "wait": "none"
        //   }
        // }
        return SendCommandAsync(
            BrowserLogsBidiProtocol.BrowsingContextNavigateMethod,
            writer =>
            {
                writer.WriteString("context", context);
                writer.WriteString("url", url.ToString());
                writer.WriteString("wait", "none");
            },
            BrowserLogsBidiProtocol.ParseCommandAckResponse,
            cancellationToken);
    }

    public Task<BrowserLogsBidiCaptureScreenshotResult> CaptureScreenshotAsync(string context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        // browsingContext.captureScreenshot request:
        // {
        //   "id": 4,
        //   "method": "browsingContext.captureScreenshot",
        //   "params": { "context": "context-id" }
        // }
        //
        // Success returns { "result": { "data": "<base64 image bytes>" } }. The BiDi spec permits format/clip
        // options, but the default PNG viewport capture matches the current BrowserLogs screenshot artifact behavior.
        return SendCommandAsync(
            BrowserLogsBidiProtocol.BrowsingContextCaptureScreenshotMethod,
            writer =>
            {
                writer.WriteString("context", context);
            },
            BrowserLogsBidiProtocol.ParseCaptureScreenshotResponse,
            cancellationToken,
            s_screenshotCommandTimeout);
    }

    public Task<BrowserLogsBidiCommandAck> CloseBrowsingContextAsync(string context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        // browsingContext.close request:
        // {
        //   "id": 5,
        //   "method": "browsingContext.close",
        //   "params": { "context": "context-id" }
        // }
        return SendCommandAsync(
            BrowserLogsBidiProtocol.BrowsingContextCloseMethod,
            writer =>
            {
                writer.WriteString("context", context);
            },
            BrowserLogsBidiProtocol.ParseCommandAckResponse,
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
        Action<Utf8JsonWriter> writeParameters,
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

            var payload = BrowserLogsBidiProtocol.CreateCommandFrame(commandId, method, writeParameters);
            _logger.LogTrace("Tracked browser BiDi -> {Frame}", BrowserLogsBidiProtocol.DescribeFrame(payload));

            await _sendLock.WaitAsync(sendCts.Token).ConfigureAwait(false);
            try
            {
                await _transport.SendAsync(payload, sendCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await pendingCommand.Task.ConfigureAwait(false);
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
                _logger.LogTrace("Tracked browser BiDi <- {Frame}", BrowserLogsBidiProtocol.DescribeFrame(frame));
                await HandleFrameAsync(frame).ConfigureAwait(false);
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
            terminalException ??= new InvalidOperationException("Browser BiDi connection closed.");
            foreach (var pendingCommand in _pendingCommands.Values)
            {
                pendingCommand.SetException(terminalException);
            }
        }

        if (!_disposeCts.IsCancellationRequested)
        {
            throw terminalException ?? new InvalidOperationException("Browser BiDi connection closed.");
        }
    }

    private async Task HandleFrameAsync(byte[] frame)
    {
        var header = BrowserLogsBidiProtocol.ParseMessageHeader(frame);
        if (header.Id is long commandId)
        {
            if (_pendingCommands.TryGetValue(commandId, out var pendingCommand))
            {
                pendingCommand.SetResult(frame);
            }

            return;
        }

        if (header.Method is not null && BrowserLogsBidiProtocol.ParseEvent(header, frame) is { } protocolEvent)
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

internal static class BrowserLogsBidiProtocol
{
    private static readonly JsonWriterOptions s_commandFrameWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal const string BrowsingContextCaptureScreenshotMethod = "browsingContext.captureScreenshot";
    internal const string BrowsingContextCloseMethod = "browsingContext.close";
    internal const string BrowsingContextContextDestroyedMethod = "browsingContext.contextDestroyed";
    internal const string BrowsingContextCreateMethod = "browsingContext.create";
    internal const string BrowsingContextNavigateMethod = "browsingContext.navigate";
    internal const string LogEntryAddedMethod = "log.entryAdded";
    internal const string NetworkBeforeRequestSentMethod = "network.beforeRequestSent";
    internal const string NetworkFetchErrorMethod = "network.fetchError";
    internal const string NetworkResponseCompletedMethod = "network.responseCompleted";
    internal const string NetworkResponseStartedMethod = "network.responseStarted";
    internal const string SessionSubscribeMethod = "session.subscribe";

    internal static BrowserLogsBidiProtocolMessageHeader ParseMessageHeader(ReadOnlySpan<byte> framePayload)
    {
        // Header-only parse is enough to route a frame:
        // - responses have "id" and unblock one pending command;
        // - events have "method" and are then parsed into the matching typed event envelope.
        var reader = new Utf8JsonReader(framePayload, isFinalBlock: true, state: default);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("Tracked browser BiDi frame was not a JSON object.");
        }

        long? id = null;
        string? method = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidOperationException("Tracked browser BiDi frame was malformed.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Tracked browser BiDi frame ended unexpectedly.");
            }

            switch (propertyName)
            {
                case "id":
                    if (!reader.TryGetInt64(out var parsedId))
                    {
                        throw new InvalidOperationException("Tracked browser BiDi response id was not an integer.");
                    }

                    id = parsedId;
                    break;
                case "method":
                    method = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new InvalidOperationException("Tracked browser BiDi event method was not a string.");
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new BrowserLogsBidiProtocolMessageHeader(id, method);
    }

    internal static byte[] CreateCommandFrame(long id, string method, Action<Utf8JsonWriter> writeParameters)
    {
        // Command request frame shape:
        // {
        //   "id": 1,
        //   "method": "module.command",
        //   "params": { ... command-specific properties ... }
        // }
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, s_commandFrameWriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("id", id);
        writer.WriteString("method", method);
        writer.WritePropertyName("params");
        writer.WriteStartObject();
        writeParameters(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    internal static BrowserLogsBidiProtocolEvent? ParseEvent(BrowserLogsBidiProtocolMessageHeader header, ReadOnlySpan<byte> framePayload) => header.Method switch
    {
        // Event frame shape:
        // {
        //   "type": "event",
        //   "method": "log.entryAdded",
        //   "params": { ... event-specific properties ... }
        // }
        LogEntryAddedMethod => CreateEvent<BrowserLogsBidiLogEntryAddedEnvelope, BrowserLogsBidiLogEntryAddedParameters, BrowserLogsBidiLogEntryAddedEvent>(
            framePayload,
            BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiLogEntryAddedEnvelope,
            static parameters => new BrowserLogsBidiLogEntryAddedEvent(parameters)),
        NetworkBeforeRequestSentMethod => CreateEvent<BrowserLogsBidiNetworkBeforeRequestSentEnvelope, BrowserLogsBidiNetworkEventParameters, BrowserLogsBidiNetworkBeforeRequestSentEvent>(
            framePayload,
            BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiNetworkBeforeRequestSentEnvelope,
            static parameters => new BrowserLogsBidiNetworkBeforeRequestSentEvent(parameters)),
        NetworkResponseStartedMethod => CreateEvent<BrowserLogsBidiNetworkResponseStartedEnvelope, BrowserLogsBidiNetworkEventParameters, BrowserLogsBidiNetworkResponseStartedEvent>(
            framePayload,
            BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiNetworkResponseStartedEnvelope,
            static parameters => new BrowserLogsBidiNetworkResponseStartedEvent(parameters)),
        NetworkResponseCompletedMethod => CreateEvent<BrowserLogsBidiNetworkResponseCompletedEnvelope, BrowserLogsBidiNetworkEventParameters, BrowserLogsBidiNetworkResponseCompletedEvent>(
            framePayload,
            BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiNetworkResponseCompletedEnvelope,
            static parameters => new BrowserLogsBidiNetworkResponseCompletedEvent(parameters)),
        NetworkFetchErrorMethod => CreateEvent<BrowserLogsBidiNetworkFetchErrorEnvelope, BrowserLogsBidiNetworkEventParameters, BrowserLogsBidiNetworkFetchErrorEvent>(
            framePayload,
            BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiNetworkFetchErrorEnvelope,
            static parameters => new BrowserLogsBidiNetworkFetchErrorEvent(parameters)),
        BrowsingContextContextDestroyedMethod => CreateEvent<BrowserLogsBidiBrowsingContextDestroyedEnvelope, BrowserLogsBidiBrowsingContextDestroyedParameters, BrowserLogsBidiBrowsingContextDestroyedEvent>(
            framePayload,
            BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiBrowsingContextDestroyedEnvelope,
            static parameters => new BrowserLogsBidiBrowsingContextDestroyedEvent(parameters)),
        _ => null
    };

    internal static BrowserLogsBidiCreateContextResult ParseCreateContextResponse(ReadOnlySpan<byte> framePayload)
    {
        // browsingContext.create success response:
        // {
        //   "type": "success",
        //   "id": 1,
        //   "result": { "context": "context-id" }
        // }
        var envelope = DeserializeFrame(framePayload, BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiCreateContextResponseEnvelope);
        ThrowIfProtocolError(envelope.Error, envelope.Message);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser BiDi context creation did not return a result payload.");
    }

    internal static BrowserLogsBidiCaptureScreenshotResult ParseCaptureScreenshotResponse(ReadOnlySpan<byte> framePayload)
    {
        // browsingContext.captureScreenshot success response:
        // {
        //   "type": "success",
        //   "id": 4,
        //   "result": { "data": "base64-encoded-image" }
        // }
        var envelope = DeserializeFrame(framePayload, BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiCaptureScreenshotResponseEnvelope);
        ThrowIfProtocolError(envelope.Error, envelope.Message);

        var result = envelope.Result ?? throw new InvalidOperationException("Tracked browser BiDi screenshot capture did not return a result payload.");
        if (string.IsNullOrWhiteSpace(result.Data))
        {
            throw new InvalidOperationException("Tracked browser BiDi screenshot capture did not return image data.");
        }

        return result;
    }

    internal static BrowserLogsBidiCommandAck ParseCommandAckResponse(ReadOnlySpan<byte> framePayload)
    {
        // Commands without useful result data still return the standard BiDi success/error envelope:
        // { "type": "success", "id": 2, "result": {} }
        // { "type": "error", "id": 2, "error": "invalid argument", "message": "..." }
        var envelope = DeserializeFrame(framePayload, BrowserLogsBidiProtocolJsonContext.Default.BrowserLogsBidiCommandAckResponseEnvelope);
        ThrowIfProtocolError(envelope.Error, envelope.Message);

        return BrowserLogsBidiCommandAck.Instance;
    }

    internal static string DescribeFrame(ReadOnlySpan<byte> framePayload, int maxLength = 512)
    {
        var text = Encoding.UTF8.GetString(framePayload);
        return text.Length <= maxLength
            ? text
            : $"{text[..maxLength]}...";
    }

    private static TEvent? CreateEvent<TEnvelope, TParameters, TEvent>(
        ReadOnlySpan<byte> framePayload,
        JsonTypeInfo<TEnvelope> jsonTypeInfo,
        Func<TParameters, TEvent> createEvent)
        where TEnvelope : class, IBrowserLogsBidiEventEnvelope<TParameters>
        where TParameters : class
        where TEvent : class
    {
        // All BiDi events we consume use the same envelope shape with event-specific "params". Deserialize only the
        // params payload we need for BrowserLogs and ignore unrelated protocol fields such as "type".
        var envelope = DeserializeFrame(framePayload, jsonTypeInfo);
        return envelope.Params is null
            ? null
            : createEvent(envelope.Params);
    }

    private static T DeserializeFrame<T>(ReadOnlySpan<byte> framePayload, JsonTypeInfo<T> jsonTypeInfo)
        where T : class
    {
        return JsonSerializer.Deserialize(framePayload, jsonTypeInfo)
            ?? throw new InvalidOperationException("Tracked browser BiDi frame was empty.");
    }

    private static void ThrowIfProtocolError(string? error, string? message)
    {
        // BiDi error response shape is top-level, not nested under an "error" object:
        // {
        //   "type": "error",
        //   "id": 7,
        //   "error": "invalid argument",
        //   "message": "Context is missing"
        // }
        if (string.IsNullOrWhiteSpace(error))
        {
            return;
        }

        var errorMessage = string.IsNullOrWhiteSpace(message)
            ? "Unknown browser BiDi protocol error."
            : message;

        throw new InvalidOperationException($"{errorMessage} (BiDi error {error}).");
    }
}

internal readonly record struct BrowserLogsBidiProtocolMessageHeader(long? Id, string? Method);

internal abstract record BrowserLogsBidiProtocolEvent(string Method, string? Context);

internal sealed record BrowserLogsBidiLogEntryAddedEvent(BrowserLogsBidiLogEntryAddedParameters Parameters)
    : BrowserLogsBidiProtocolEvent(BrowserLogsBidiProtocol.LogEntryAddedMethod, Parameters.Source?.Context);

internal sealed record BrowserLogsBidiNetworkBeforeRequestSentEvent(BrowserLogsBidiNetworkEventParameters Parameters)
    : BrowserLogsBidiProtocolEvent(BrowserLogsBidiProtocol.NetworkBeforeRequestSentMethod, Parameters.Context);

internal sealed record BrowserLogsBidiNetworkResponseStartedEvent(BrowserLogsBidiNetworkEventParameters Parameters)
    : BrowserLogsBidiProtocolEvent(BrowserLogsBidiProtocol.NetworkResponseStartedMethod, Parameters.Context);

internal sealed record BrowserLogsBidiNetworkResponseCompletedEvent(BrowserLogsBidiNetworkEventParameters Parameters)
    : BrowserLogsBidiProtocolEvent(BrowserLogsBidiProtocol.NetworkResponseCompletedMethod, Parameters.Context);

internal sealed record BrowserLogsBidiNetworkFetchErrorEvent(BrowserLogsBidiNetworkEventParameters Parameters)
    : BrowserLogsBidiProtocolEvent(BrowserLogsBidiProtocol.NetworkFetchErrorMethod, Parameters.Context);

internal sealed record BrowserLogsBidiBrowsingContextDestroyedEvent(BrowserLogsBidiBrowsingContextDestroyedParameters Parameters)
    : BrowserLogsBidiProtocolEvent(BrowserLogsBidiProtocol.BrowsingContextContextDestroyedMethod, Parameters.Context);

internal sealed class BrowserLogsBidiCommandAck
{
    public static BrowserLogsBidiCommandAck Instance { get; } = new();

    private BrowserLogsBidiCommandAck()
    {
    }
}

internal sealed class BrowserLogsBidiCommandAckResponseEnvelope
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class BrowserLogsBidiCaptureScreenshotResponseEnvelope
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsBidiCaptureScreenshotResult? Result { get; init; }
}

internal sealed class BrowserLogsBidiCaptureScreenshotResult
{
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

internal sealed class BrowserLogsBidiCreateContextResponseEnvelope
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsBidiCreateContextResult? Result { get; init; }
}

internal sealed class BrowserLogsBidiCreateContextResult
{
    [JsonPropertyName("context")]
    public string? Context { get; init; }
}

internal interface IBrowserLogsBidiEventEnvelope<out TParameters>
    where TParameters : class
{
    TParameters? Params { get; }
}

internal sealed class BrowserLogsBidiLogEntryAddedEnvelope : IBrowserLogsBidiEventEnvelope<BrowserLogsBidiLogEntryAddedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsBidiLogEntryAddedParameters? Params { get; init; }
}

internal sealed class BrowserLogsBidiLogEntryAddedParameters
{
    // log.entryAdded params shape:
    // {
    //   "type": "console" | "javascript",
    //   "method": "log" | "warn" | "error"?,   // present for console entries
    //   "level": "info" | "warn" | "error" | ...,
    //   "text": "rendered message",
    //   "args": [ { "type": "string", "value": "..." } ],
    //   "source": { "context": "context-id" },
    //   "stackTrace": { "callFrames": [ { "url": "...", "lineNumber": 0, "columnNumber": 0 } ] }
    // }
    [JsonPropertyName("args")]
    public BrowserLogsBidiRemoteValue[]? Args { get; init; }

    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("source")]
    public BrowserLogsBidiSource? Source { get; init; }

    [JsonPropertyName("stackTrace")]
    public BrowserLogsBidiStackTrace? StackTrace { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed class BrowserLogsBidiRemoteValue
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }
}

internal sealed class BrowserLogsBidiSource
{
    [JsonPropertyName("context")]
    public string? Context { get; init; }
}

internal sealed class BrowserLogsBidiStackTrace
{
    [JsonPropertyName("callFrames")]
    public BrowserLogsBidiStackFrame[]? CallFrames { get; init; }
}

internal sealed class BrowserLogsBidiStackFrame
{
    [JsonPropertyName("columnNumber")]
    public int? ColumnNumber { get; init; }

    [JsonPropertyName("lineNumber")]
    public int? LineNumber { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserLogsBidiNetworkBeforeRequestSentEnvelope : IBrowserLogsBidiEventEnvelope<BrowserLogsBidiNetworkEventParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsBidiNetworkEventParameters? Params { get; init; }
}

internal sealed class BrowserLogsBidiNetworkResponseStartedEnvelope : IBrowserLogsBidiEventEnvelope<BrowserLogsBidiNetworkEventParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsBidiNetworkEventParameters? Params { get; init; }
}

internal sealed class BrowserLogsBidiNetworkResponseCompletedEnvelope : IBrowserLogsBidiEventEnvelope<BrowserLogsBidiNetworkEventParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsBidiNetworkEventParameters? Params { get; init; }
}

internal sealed class BrowserLogsBidiNetworkFetchErrorEnvelope : IBrowserLogsBidiEventEnvelope<BrowserLogsBidiNetworkEventParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsBidiNetworkEventParameters? Params { get; init; }
}

internal sealed class BrowserLogsBidiNetworkEventParameters
{
    // Network event params shape for beforeRequestSent/responseStarted/responseCompleted/fetchError:
    // {
    //   "context": "context-id",
    //   "timestamp": 1714520000000,
    //   "request": { "request": "request-id", "method": "GET", "url": "https://..." },
    //   "response": { "url": "https://...", "status": 200, "statusText": "OK", "fromCache": false },
    //   "errorText": "..." // only on network.fetchError
    // }
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    [JsonPropertyName("errorText")]
    public string? ErrorText { get; init; }

    [JsonPropertyName("request")]
    public BrowserLogsBidiNetworkRequest? Request { get; init; }

    [JsonPropertyName("response")]
    public BrowserLogsBidiNetworkResponse? Response { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }
}

internal sealed class BrowserLogsBidiNetworkRequest
{
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("request")]
    public string? Request { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserLogsBidiNetworkResponse
{
    [JsonPropertyName("fromCache")]
    public bool? FromCache { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("statusText")]
    public string? StatusText { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserLogsBidiBrowsingContextDestroyedEnvelope : IBrowserLogsBidiEventEnvelope<BrowserLogsBidiBrowsingContextDestroyedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsBidiBrowsingContextDestroyedParameters? Params { get; init; }
}

internal sealed class BrowserLogsBidiBrowsingContextDestroyedParameters
{
    [JsonPropertyName("context")]
    public string? Context { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserLogsBidiBrowsingContextDestroyedEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiCaptureScreenshotResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiCommandAckResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiCreateContextResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiLogEntryAddedEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiNetworkBeforeRequestSentEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiNetworkFetchErrorEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiNetworkResponseCompletedEnvelope))]
[JsonSerializable(typeof(BrowserLogsBidiNetworkResponseStartedEnvelope))]
internal sealed partial class BrowserLogsBidiProtocolJsonContext : JsonSerializerContext;
