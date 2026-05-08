// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Aspire.Hosting;

// Chrome DevTools Protocol (CDP) references:
// - Message envelope and domain index: https://chromedevtools.github.io/devtools-protocol/
// - Target domain: https://chromedevtools.github.io/devtools-protocol/tot/Target/
// - Runtime domain: https://chromedevtools.github.io/devtools-protocol/tot/Runtime/
// - Log domain: https://chromedevtools.github.io/devtools-protocol/tot/Log/
// - Page domain: https://chromedevtools.github.io/devtools-protocol/tot/Page/
// - Network domain: https://chromedevtools.github.io/devtools-protocol/tot/Network/
//
// Browser websocket frames are JSON objects shaped like:
// - command request:  { "id": 1, "method": "...", "params": { ... }, "sessionId": "..."? }
// - command response: { "id": 1, "result": { ... } } or { "id": 1, "error": { ... } }
// - event:            { "method": "...", "params": { ... }, "sessionId": "..."? }
//
// Keep this file focused on protocol serialization and parsing so browser networking and session orchestration can be
// tested independently from CDP frame handling.
internal static class BrowserCdpProtocol
{
    private static readonly JsonWriterOptions s_commandFrameWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal const string LogEnableMethod = "Log.enable";
    internal const string LogEntryAddedMethod = "Log.entryAdded";
    internal const string NetworkEnableMethod = "Network.enable";
    internal const string NetworkLoadingFailedMethod = "Network.loadingFailed";
    internal const string NetworkLoadingFinishedMethod = "Network.loadingFinished";
    internal const string NetworkRequestWillBeSentMethod = "Network.requestWillBeSent";
    internal const string NetworkResponseReceivedMethod = "Network.responseReceived";
    internal const string PageEnableMethod = "Page.enable";
    internal const string PageCaptureScreenshotMethod = "Page.captureScreenshot";
    internal const string PageNavigateMethod = "Page.navigate";
    internal const string RuntimeConsoleApiCalledMethod = "Runtime.consoleAPICalled";
    internal const string RuntimeEnableMethod = "Runtime.enable";
    internal const string RuntimeEvaluateMethod = "Runtime.evaluate";
    internal const string RuntimeExceptionThrownMethod = "Runtime.exceptionThrown";
    internal const string TargetAttachToTargetMethod = "Target.attachToTarget";
    internal const string TargetCloseTargetMethod = "Target.closeTarget";
    internal const string TargetCreateTargetMethod = "Target.createTarget";
    internal const string TargetDetachedFromTargetMethod = "Target.detachedFromTarget";
    internal const string TargetGetTargetsMethod = "Target.getTargets";
    // Turns on browser-level target discovery. In CDP a "target" is a debuggable entity such as a page/tab, worker,
    // or iframe. We use this subscription for page target lifecycle events; without it, closing or crashing the
    // tracked tab can look like an unexplained connection loss.
    internal const string TargetSetDiscoverTargetsMethod = "Target.setDiscoverTargets";
    internal const string TargetTargetCrashedMethod = "Target.targetCrashed";
    internal const string TargetTargetDestroyedMethod = "Target.targetDestroyed";
    internal const string InspectorDetachedMethod = "Inspector.detached";

    /// <summary>
    /// Reads the shared CDP frame header fields before routing the full payload to a response or event parser.
    /// </summary>
    /// <remarks>
    /// CDP responses and events use the same websocket stream. This lightweight pass extracts only the fields needed to
    /// route the frame and skips the larger payload:
    /// <code>
    /// { "id": 42, "result": { "result": { "type": "string", "value": "https://localhost:5001/" } } }
    /// { "method": "Runtime.consoleAPICalled", "sessionId": "A1B2C3", "params": { "type": "log" } }
    /// </code>
    /// The first frame is a command response because it has an <c>id</c>; the second is an event because it has a
    /// <c>method</c> and no response <c>id</c>.
    /// </remarks>
    internal static BrowserCdpProtocolMessageHeader ParseMessageHeader(ReadOnlySpan<byte> framePayload)
    {
        var reader = new Utf8JsonReader(framePayload, isFinalBlock: true, state: default);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("Tracked browser protocol frame was not a JSON object.");
        }

        long? id = null;
        string? method = null;
        string? sessionId = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidOperationException("Tracked browser protocol frame was malformed.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Tracked browser protocol frame ended unexpectedly.");
            }

            switch (propertyName)
            {
                case "id":
                    if (!reader.TryGetInt64(out var parsedId))
                    {
                        throw new InvalidOperationException("Tracked browser protocol response id was not an integer.");
                    }

                    id = parsedId;
                    break;
                case "method":
                    method = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : throw new InvalidOperationException("Tracked browser protocol event method was not a string.");
                    break;
                case "sessionId":
                    sessionId = reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : null;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new BrowserCdpProtocolMessageHeader(id, method, sessionId);
    }

    /// <summary>
    /// Creates the JSON command frame sent over the browser websocket.
    /// </summary>
    /// <remarks>
    /// CDP command frames are a single JSON object. The <c>sessionId</c> is present for commands routed to an attached
    /// page target, and <c>params</c> is omitted when the command has no parameters.
    /// <code>
    /// {
    ///   "id": 42,
    ///   "method": "Runtime.evaluate",
    ///   "sessionId": "A1B2C3",
    ///   "params": {
    ///     "expression": "location.href",
    ///     "returnByValue": true
    ///   }
    /// }
    /// </code>
    /// </remarks>
    internal static byte[] CreateCommandFrame(long id, string method, string? sessionId, Action<Utf8JsonWriter>? writeParameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, s_commandFrameWriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("id", id);
        writer.WriteString("method", method);

        if (sessionId is not null)
        {
            writer.WriteString("sessionId", sessionId);
        }

        if (writeParameters is not null)
        {
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writeParameters(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    internal static BrowserCdpProtocolEvent? ParseEvent(BrowserCdpProtocolMessageHeader header, ReadOnlySpan<byte> framePayload) => header.Method switch
    {
        RuntimeConsoleApiCalledMethod => CreateConsoleApiCalledEvent(framePayload),
        RuntimeExceptionThrownMethod => CreateExceptionThrownEvent(framePayload),
        LogEntryAddedMethod => CreateLogEntryAddedEvent(framePayload),
        NetworkRequestWillBeSentMethod => CreateRequestWillBeSentEvent(framePayload),
        NetworkResponseReceivedMethod => CreateResponseReceivedEvent(framePayload),
        NetworkLoadingFinishedMethod => CreateLoadingFinishedEvent(framePayload),
        NetworkLoadingFailedMethod => CreateLoadingFailedEvent(framePayload),
        TargetTargetDestroyedMethod => CreateTargetDestroyedEvent(framePayload),
        TargetTargetCrashedMethod => CreateTargetCrashedEvent(framePayload),
        TargetDetachedFromTargetMethod => CreateDetachedFromTargetEvent(framePayload),
        InspectorDetachedMethod => CreateInspectorDetachedEvent(framePayload),
        _ => null
    };

    internal static BrowserCreateTargetResult ParseCreateTargetResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserCreateTargetResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target creation did not return a result payload.");
    }

    internal static BrowserAttachToTargetResult ParseAttachToTargetResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserAttachToTargetResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target attachment did not return a result payload.");
    }

    internal static BrowserGetTargetsResult ParseGetTargetsResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserGetTargetsResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target discovery did not return a result payload.");
    }

    internal static BrowserCommandAck ParseCommandAckResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserCommandAckResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return BrowserCommandAck.Instance;
    }

    internal static BrowserNavigateResult ParseNavigateResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserNavigateResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        var result = envelope.Result ?? throw new InvalidOperationException("Tracked browser navigation did not return a result payload.");
        if (!string.IsNullOrWhiteSpace(result.ErrorText))
        {
            throw new InvalidOperationException($"Tracked browser navigation failed: {result.ErrorText}");
        }

        return result;
    }

    internal static BrowserCaptureScreenshotResult ParseCaptureScreenshotResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserCaptureScreenshotResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        var result = envelope.Result ?? throw new InvalidOperationException("Tracked browser screenshot capture did not return a result payload.");
        if (string.IsNullOrWhiteSpace(result.Data))
        {
            throw new InvalidOperationException("Tracked browser screenshot capture did not return image data.");
        }

        return result;
    }

    internal static BrowserRuntimeEvaluateResult ParseRuntimeEvaluateResponse(ReadOnlySpan<byte> framePayload)
    {
        // Expected Runtime.evaluate response shape:
        // { "id": 1, "result": { "result": { "type": "string", "value": "{...}" }, "exceptionDetails": { ... } } }
        var envelope = DeserializeFrame(framePayload, BrowserCdpProtocolJsonContext.Default.BrowserRuntimeEvaluateResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        var result = envelope.Result ?? throw new InvalidOperationException("Tracked browser script evaluation did not return a result payload.");
        if (result.ExceptionDetails is { } exceptionDetails)
        {
            throw new InvalidOperationException(FormatRuntimeException(exceptionDetails));
        }

        return result;
    }

    internal static string ParseRawCommandResponse(ReadOnlySpan<byte> framePayload)
    {
        // Expected generic CDP response shape:
        // { "id": 1, "result": { ... } } or { "id": 1, "error": { "code": -32601, "message": "..." } }
        using var document = JsonDocument.Parse(framePayload.ToArray());
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var errorElement))
        {
            ThrowIfProtocolError(new BrowserCdpProtocolError
            {
                Code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var code) ? code : null,
                Message = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : null
            });
        }

        return root.TryGetProperty("result", out var resultElement)
            ? JsonSerializer.Serialize(resultElement)
            : "{}";
    }

    internal static void WriteRawCommandParameters(Utf8JsonWriter writer, string parametersJson)
    {
        using var document = JsonDocument.Parse(parametersJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("CDP command parameters must be a JSON object.");
        }

        foreach (var property in root.EnumerateObject())
        {
            property.WriteTo(writer);
        }
    }

    internal static string DescribeFrame(ReadOnlySpan<byte> framePayload, int maxLength = 512)
    {
        var text = Encoding.UTF8.GetString(framePayload);
        return text.Length <= maxLength
            ? text
            : $"{text[..maxLength]}...";
    }

    private static BrowserConsoleApiCalledEvent? CreateConsoleApiCalledEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserConsoleApiCalledEnvelope,
            static (string? sessionId, BrowserRuntimeConsoleApiCalledParameters parameters) => new BrowserConsoleApiCalledEvent(sessionId, parameters));

    private static BrowserExceptionThrownEvent? CreateExceptionThrownEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserExceptionThrownEnvelope,
            static (string? sessionId, BrowserExceptionThrownParameters parameters) => new BrowserExceptionThrownEvent(sessionId, parameters));

    private static BrowserLogEntryAddedEvent? CreateLogEntryAddedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserLogEntryAddedEnvelope,
            static (string? sessionId, BrowserLogEntryAddedParameters parameters) => new BrowserLogEntryAddedEvent(sessionId, parameters));

    private static BrowserRequestWillBeSentEvent? CreateRequestWillBeSentEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserRequestWillBeSentEnvelope,
            static (string? sessionId, BrowserRequestWillBeSentParameters parameters) => new BrowserRequestWillBeSentEvent(sessionId, parameters));

    private static BrowserResponseReceivedEvent? CreateResponseReceivedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserResponseReceivedEnvelope,
            static (string? sessionId, BrowserResponseReceivedParameters parameters) => new BrowserResponseReceivedEvent(sessionId, parameters));

    private static BrowserLoadingFinishedEvent? CreateLoadingFinishedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserLoadingFinishedEnvelope,
            static (string? sessionId, BrowserLoadingFinishedParameters parameters) => new BrowserLoadingFinishedEvent(sessionId, parameters));

    private static BrowserLoadingFailedEvent? CreateLoadingFailedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserLoadingFailedEnvelope,
            static (string? sessionId, BrowserLoadingFailedParameters parameters) => new BrowserLoadingFailedEvent(sessionId, parameters));

    private static BrowserTargetDestroyedEvent? CreateTargetDestroyedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserTargetDestroyedEnvelope,
            static (string? sessionId, BrowserTargetDestroyedParameters parameters) => new BrowserTargetDestroyedEvent(sessionId, parameters));

    private static BrowserTargetCrashedEvent? CreateTargetCrashedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserTargetCrashedEnvelope,
            static (string? sessionId, BrowserTargetCrashedParameters parameters) => new BrowserTargetCrashedEvent(sessionId, parameters));

    private static BrowserDetachedFromTargetEvent? CreateDetachedFromTargetEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserDetachedFromTargetEnvelope,
            static (string? sessionId, BrowserDetachedFromTargetParameters parameters) => new BrowserDetachedFromTargetEvent(sessionId, parameters));

    private static BrowserInspectorDetachedEvent? CreateInspectorDetachedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserCdpProtocolJsonContext.Default.BrowserInspectorDetachedEnvelope,
            static (string? sessionId, BrowserInspectorDetachedParameters parameters) => new BrowserInspectorDetachedEvent(sessionId, parameters));

    private static TEvent? CreateEvent<TEnvelope, TParameters, TEvent>(
        ReadOnlySpan<byte> framePayload,
        JsonTypeInfo<TEnvelope> jsonTypeInfo,
        Func<string?, TParameters, TEvent> createEvent)
        where TEnvelope : class, IBrowserEventEnvelope<TParameters>
        where TParameters : class
        where TEvent : class
    {
        var envelope = DeserializeFrame(framePayload, jsonTypeInfo);
        return envelope.Params is null
            ? null
            : createEvent(envelope.SessionId, envelope.Params);
    }

    private static T DeserializeFrame<T>(ReadOnlySpan<byte> framePayload, JsonTypeInfo<T> jsonTypeInfo)
        where T : class
    {
        return JsonSerializer.Deserialize(framePayload, jsonTypeInfo)
            ?? throw new InvalidOperationException("Tracked browser protocol frame was empty.");
    }

    private static void ThrowIfProtocolError(BrowserCdpProtocolError? error)
    {
        if (error is null)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(error.Message)
            ? "Unknown browser protocol error."
            : error.Message;

        if (error.Code is int code)
        {
            throw new InvalidOperationException($"{message} (CDP error {code}).");
        }

        throw new InvalidOperationException(message);
    }

    private static string FormatRuntimeException(BrowserExceptionDetails exceptionDetails)
    {
        var message = !string.IsNullOrWhiteSpace(exceptionDetails.Exception?.Description)
            ? exceptionDetails.Exception.Description
            : !string.IsNullOrWhiteSpace(exceptionDetails.Text)
                ? exceptionDetails.Text
                : "Tracked browser script evaluation failed.";

        if (exceptionDetails.Url is { Length: > 0 } url)
        {
            return $"{message} ({url}:{exceptionDetails.LineNumber ?? 0}:{exceptionDetails.ColumnNumber ?? 0}).";
        }

        return message;
    }
}

internal readonly record struct BrowserCdpProtocolMessageHeader(long? Id, string? Method, string? SessionId);

internal abstract record BrowserCdpProtocolEvent(string Method, string? SessionId);

internal sealed record BrowserConsoleApiCalledEvent(string? SessionId, BrowserRuntimeConsoleApiCalledParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.RuntimeConsoleApiCalledMethod, SessionId);

internal sealed record BrowserExceptionThrownEvent(string? SessionId, BrowserExceptionThrownParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.RuntimeExceptionThrownMethod, SessionId);

internal sealed record BrowserLoadingFailedEvent(string? SessionId, BrowserLoadingFailedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.NetworkLoadingFailedMethod, SessionId);

internal sealed record BrowserLoadingFinishedEvent(string? SessionId, BrowserLoadingFinishedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.NetworkLoadingFinishedMethod, SessionId);

internal sealed record BrowserLogEntryAddedEvent(string? SessionId, BrowserLogEntryAddedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.LogEntryAddedMethod, SessionId);

internal sealed record BrowserRequestWillBeSentEvent(string? SessionId, BrowserRequestWillBeSentParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.NetworkRequestWillBeSentMethod, SessionId);

internal sealed record BrowserResponseReceivedEvent(string? SessionId, BrowserResponseReceivedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.NetworkResponseReceivedMethod, SessionId);

// Target lifecycle events differ from page-domain events in routing semantics:
// - For Page/Runtime/Network/Log events, BrowserCdpProtocolEvent.SessionId is the attached page session and
//   the dispatcher routes by matching it against the tracked target's session id.
// - For Target.targetDestroyed/targetCrashed and Inspector.detached, the envelope-level sessionId is typically
//   absent (these are fired on the browser CDP channel, not on a target session). The SUBJECT of the event is
//   carried in the parameters: targetId for target events, the parent attached sessionId for the implicit
//   detach. Routing logic must not rely on BrowserCdpProtocolEvent.SessionId for these.
// - For Target.detachedFromTarget specifically, params.sessionId identifies the session that detached, which is
//   the value that should be matched against the tracked target's session id.
internal sealed record BrowserTargetDestroyedEvent(string? SessionId, BrowserTargetDestroyedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.TargetTargetDestroyedMethod, SessionId)
{
    public string? TargetId => Parameters.TargetId;
}

internal sealed record BrowserTargetCrashedEvent(string? SessionId, BrowserTargetCrashedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.TargetTargetCrashedMethod, SessionId)
{
    public string? TargetId => Parameters.TargetId;
}

internal sealed record BrowserDetachedFromTargetEvent(string? SessionId, BrowserDetachedFromTargetParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.TargetDetachedFromTargetMethod, SessionId)
{
    public string? DetachedSessionId => Parameters.SessionId;

    public string? TargetId => Parameters.TargetId;
}

internal sealed record BrowserInspectorDetachedEvent(string? SessionId, BrowserInspectorDetachedParameters Parameters)
    : BrowserCdpProtocolEvent(BrowserCdpProtocol.InspectorDetachedMethod, SessionId)
{
    public string? Reason => Parameters.Reason;
}

internal sealed class BrowserAttachToTargetResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserAttachToTargetResult? Result { get; init; }
}

internal sealed class BrowserAttachToTargetResult
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserCommandAck
{
    public static BrowserCommandAck Instance { get; } = new();

    private BrowserCommandAck()
    {
    }
}

internal sealed class BrowserCommandAckResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }
}

internal sealed class BrowserNavigateResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserNavigateResult? Result { get; init; }
}

internal sealed class BrowserNavigateResult
{
    [JsonPropertyName("frameId")]
    public string? FrameId { get; init; }

    [JsonPropertyName("loaderId")]
    public string? LoaderId { get; init; }

    [JsonPropertyName("errorText")]
    public string? ErrorText { get; init; }

    [JsonPropertyName("isDownload")]
    public bool? IsDownload { get; init; }
}

internal sealed class BrowserCaptureScreenshotResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserCaptureScreenshotResult? Result { get; init; }
}

internal sealed class BrowserCaptureScreenshotResult
{
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

internal sealed class BrowserRuntimeEvaluateResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserRuntimeEvaluateResult? Result { get; init; }
}

internal sealed class BrowserRuntimeEvaluateResult
{
    [JsonPropertyName("exceptionDetails")]
    public BrowserExceptionDetails? ExceptionDetails { get; init; }

    [JsonPropertyName("result")]
    public BrowserCdpProtocolRemoteObject? Result { get; init; }
}

internal interface IBrowserEventEnvelope<out TParameters>
    where TParameters : class
{
    TParameters? Params { get; }

    string? SessionId { get; }
}

internal sealed class BrowserConsoleApiCalledEnvelope : IBrowserEventEnvelope<BrowserRuntimeConsoleApiCalledParameters>
{
    [JsonPropertyName("params")]
    public BrowserRuntimeConsoleApiCalledParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserCreateTargetResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserCreateTargetResult? Result { get; init; }
}

internal sealed class BrowserCreateTargetResult
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserGetTargetsResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserGetTargetsResult? Result { get; init; }
}

internal sealed class BrowserGetTargetsResult
{
    [JsonPropertyName("targetInfos")]
    public BrowserTargetInfo[]? TargetInfos { get; init; }
}

internal sealed class BrowserTargetInfo
{
    [JsonPropertyName("attached")]
    public bool? Attached { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserExceptionDetails : BrowserSourceLocation
{
    [JsonPropertyName("exception")]
    public BrowserExceptionObject? Exception { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

internal sealed class BrowserExceptionObject
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

internal sealed class BrowserExceptionThrownEnvelope : IBrowserEventEnvelope<BrowserExceptionThrownParameters>
{
    [JsonPropertyName("params")]
    public BrowserExceptionThrownParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserExceptionThrownParameters
{
    [JsonPropertyName("exceptionDetails")]
    public BrowserExceptionDetails? ExceptionDetails { get; init; }
}

internal sealed class BrowserLoadingFailedEnvelope : IBrowserEventEnvelope<BrowserLoadingFailedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLoadingFailedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLoadingFailedParameters
{
    [JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; init; }

    [JsonPropertyName("canceled")]
    public bool? Canceled { get; init; }

    [JsonPropertyName("errorText")]
    public string? ErrorText { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }
}

internal sealed class BrowserLoadingFinishedEnvelope : IBrowserEventEnvelope<BrowserLoadingFinishedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLoadingFinishedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLoadingFinishedParameters
{
    [JsonPropertyName("encodedDataLength")]
    public double? EncodedDataLength { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }
}

internal sealed class BrowserLogEntry : BrowserSourceLocation
{
    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

internal sealed class BrowserLogEntryAddedEnvelope : IBrowserEventEnvelope<BrowserLogEntryAddedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogEntryAddedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogEntryAddedParameters
{
    [JsonPropertyName("entry")]
    public BrowserLogEntry? Entry { get; init; }
}

internal sealed class BrowserCdpProtocolError
{
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class BrowserCdpProtocolRemoteObject
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("unserializableValue")]
    public string? UnserializableValue { get; init; }

    [JsonPropertyName("value")]
    public BrowserCdpProtocolValue? Value { get; init; }
}

internal sealed class BrowserRequest
{
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserRequestWillBeSentEnvelope : IBrowserEventEnvelope<BrowserRequestWillBeSentParameters>
{
    [JsonPropertyName("params")]
    public BrowserRequestWillBeSentParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserRequestWillBeSentParameters
{
    [JsonPropertyName("redirectResponse")]
    public BrowserResponse? RedirectResponse { get; init; }

    [JsonPropertyName("request")]
    public BrowserRequest? Request { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed class BrowserResponse
{
    [JsonPropertyName("fromDiskCache")]
    public bool? FromDiskCache { get; init; }

    [JsonPropertyName("fromServiceWorker")]
    public bool? FromServiceWorker { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("statusText")]
    public string? StatusText { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserResponseReceivedEnvelope : IBrowserEventEnvelope<BrowserResponseReceivedParameters>
{
    [JsonPropertyName("params")]
    public BrowserResponseReceivedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserResponseReceivedParameters
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("response")]
    public BrowserResponse? Response { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed class BrowserRuntimeConsoleApiCalledParameters
{
    [JsonPropertyName("args")]
    public BrowserCdpProtocolRemoteObject[]? Args { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal class BrowserSourceLocation
{
    [JsonPropertyName("columnNumber")]
    public int? ColumnNumber { get; init; }

    [JsonPropertyName("lineNumber")]
    public int? LineNumber { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserTargetDestroyedEnvelope : IBrowserEventEnvelope<BrowserTargetDestroyedParameters>
{
    [JsonPropertyName("params")]
    public BrowserTargetDestroyedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserTargetDestroyedParameters
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserTargetCrashedEnvelope : IBrowserEventEnvelope<BrowserTargetCrashedParameters>
{
    [JsonPropertyName("params")]
    public BrowserTargetCrashedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserTargetCrashedParameters
{
    [JsonPropertyName("errorCode")]
    public int? ErrorCode { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserDetachedFromTargetEnvelope : IBrowserEventEnvelope<BrowserDetachedFromTargetParameters>
{
    [JsonPropertyName("params")]
    public BrowserDetachedFromTargetParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserDetachedFromTargetParameters
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserInspectorDetachedEnvelope : IBrowserEventEnvelope<BrowserInspectorDetachedParameters>
{
    [JsonPropertyName("params")]
    public BrowserInspectorDetachedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserInspectorDetachedParameters
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

[JsonConverter(typeof(BrowserCdpProtocolValueJsonConverter))]
internal abstract record BrowserCdpProtocolValue;

internal sealed record BrowserCdpProtocolArrayValue(IReadOnlyList<BrowserCdpProtocolValue> Items) : BrowserCdpProtocolValue;

internal sealed record BrowserCdpProtocolBooleanValue(bool Value) : BrowserCdpProtocolValue;

internal sealed record BrowserCdpProtocolNullValue : BrowserCdpProtocolValue
{
    public static BrowserCdpProtocolNullValue Instance { get; } = new();

    private BrowserCdpProtocolNullValue()
    {
    }
}

internal sealed record BrowserCdpProtocolNumberValue(string RawValue) : BrowserCdpProtocolValue;

internal sealed record BrowserCdpProtocolObjectValue(IReadOnlyDictionary<string, BrowserCdpProtocolValue> Properties) : BrowserCdpProtocolValue;

internal sealed record BrowserCdpProtocolStringValue(string Value) : BrowserCdpProtocolValue;

internal sealed class BrowserCdpProtocolValueJsonConverter : JsonConverter<BrowserCdpProtocolValue>
{
    // Runtime RemoteObject.value is the right-hand side of protocol frames such as:
    //
    // { "type": "number", "value": 1e+21 }
    // { "type": "object", "value": { "count": 2, "items": [true, null, "text"] } }
    //
    // Numbers are preserved as their raw JSON token so console logging doesn't round, reformat, or overflow values that
    // JavaScript produced. Non-JSON JavaScript values such as NaN and Infinity arrive through RemoteObject.unserializableValue
    // and are handled before this converter is used.
    public override BrowserCdpProtocolValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.StartArray => ReadArray(ref reader, options),
        JsonTokenType.StartObject => ReadObject(ref reader, options),
        JsonTokenType.String => new BrowserCdpProtocolStringValue(reader.GetString() ?? string.Empty),
        JsonTokenType.True => new BrowserCdpProtocolBooleanValue(true),
        JsonTokenType.False => new BrowserCdpProtocolBooleanValue(false),
        JsonTokenType.Null => BrowserCdpProtocolNullValue.Instance,
        JsonTokenType.Number => new BrowserCdpProtocolNumberValue(GetRawNumber(ref reader)),
        _ => throw new JsonException($"Unsupported JSON token '{reader.TokenType}' for tracked browser protocol value.")
    };

    public override void Write(Utf8JsonWriter writer, BrowserCdpProtocolValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case BrowserCdpProtocolArrayValue arrayValue:
                writer.WriteStartArray();
                foreach (var item in arrayValue.Items)
                {
                    Write(writer, item, options);
                }

                writer.WriteEndArray();
                break;
            case BrowserCdpProtocolBooleanValue booleanValue:
                writer.WriteBooleanValue(booleanValue.Value);
                break;
            case BrowserCdpProtocolNullValue:
                writer.WriteNullValue();
                break;
            case BrowserCdpProtocolNumberValue numberValue:
                writer.WriteRawValue(numberValue.RawValue, skipInputValidation: true);
                break;
            case BrowserCdpProtocolObjectValue objectValue:
                writer.WriteStartObject();
                foreach (var (propertyName, propertyValue) in objectValue.Properties)
                {
                    writer.WritePropertyName(propertyName);
                    Write(writer, propertyValue, options);
                }

                writer.WriteEndObject();
                break;
            case BrowserCdpProtocolStringValue stringValue:
                writer.WriteStringValue(stringValue.Value);
                break;
            default:
                throw new JsonException($"Unsupported tracked browser protocol value type '{value.GetType()}'.");
        }
    }

    private static string GetRawNumber(ref Utf8JsonReader reader)
    {
        return reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
    }

    private static BrowserCdpProtocolArrayValue ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var items = new List<BrowserCdpProtocolValue>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            items.Add(ReadValue(ref reader, options));
        }

        return new BrowserCdpProtocolArrayValue(items);
    }

    private static BrowserCdpProtocolObjectValue ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var properties = new Dictionary<string, BrowserCdpProtocolValue>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Tracked browser protocol object value was malformed.");
            }

            var propertyName = reader.GetString()
                ?? throw new JsonException("Tracked browser protocol object property name was null.");

            if (!reader.Read())
            {
                throw new JsonException("Tracked browser protocol object value ended unexpectedly.");
            }

            properties[propertyName] = ReadValue(ref reader, options);
        }

        return new BrowserCdpProtocolObjectValue(properties);
    }

    private static BrowserCdpProtocolValue ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var converter = (BrowserCdpProtocolValueJsonConverter)options.GetConverter(typeof(BrowserCdpProtocolValue));
        return converter.Read(ref reader, typeof(BrowserCdpProtocolValue), options);
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserAttachToTargetResponseEnvelope))]
[JsonSerializable(typeof(BrowserCaptureScreenshotResponseEnvelope))]
[JsonSerializable(typeof(BrowserCommandAckResponseEnvelope))]
[JsonSerializable(typeof(BrowserConsoleApiCalledEnvelope))]
[JsonSerializable(typeof(BrowserCreateTargetResponseEnvelope))]
[JsonSerializable(typeof(BrowserDetachedFromTargetEnvelope))]
[JsonSerializable(typeof(BrowserGetTargetsResponseEnvelope))]
[JsonSerializable(typeof(BrowserNavigateResponseEnvelope))]
[JsonSerializable(typeof(BrowserExceptionThrownEnvelope))]
[JsonSerializable(typeof(BrowserInspectorDetachedEnvelope))]
[JsonSerializable(typeof(BrowserLoadingFailedEnvelope))]
[JsonSerializable(typeof(BrowserLoadingFinishedEnvelope))]
[JsonSerializable(typeof(BrowserLogEntryAddedEnvelope))]
[JsonSerializable(typeof(BrowserRequestWillBeSentEnvelope))]
[JsonSerializable(typeof(BrowserResponseReceivedEnvelope))]
[JsonSerializable(typeof(BrowserRuntimeEvaluateResponseEnvelope))]
[JsonSerializable(typeof(BrowserTargetCrashedEnvelope))]
[JsonSerializable(typeof(BrowserTargetDestroyedEnvelope))]
internal sealed partial class BrowserCdpProtocolJsonContext : JsonSerializerContext;
