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
internal static class BrowserLogsCdpProtocol
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

    internal static BrowserLogsCdpProtocolMessageHeader ParseMessageHeader(ReadOnlySpan<byte> framePayload)
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

        return new BrowserLogsCdpProtocolMessageHeader(id, method, sessionId);
    }

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

    internal static BrowserLogsCdpProtocolEvent? ParseEvent(BrowserLogsCdpProtocolMessageHeader header, ReadOnlySpan<byte> framePayload) => header.Method switch
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

    internal static BrowserLogsCreateTargetResult ParseCreateTargetResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsCreateTargetResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target creation did not return a result payload.");
    }

    internal static BrowserLogsAttachToTargetResult ParseAttachToTargetResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsAttachToTargetResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target attachment did not return a result payload.");
    }

    internal static BrowserLogsGetTargetsResult ParseGetTargetsResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsGetTargetsResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target discovery did not return a result payload.");
    }

    internal static BrowserLogsCommandAck ParseCommandAckResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsCommandAckResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return BrowserLogsCommandAck.Instance;
    }

    internal static BrowserLogsCaptureScreenshotResult ParseCaptureScreenshotResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsCaptureScreenshotResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        var result = envelope.Result ?? throw new InvalidOperationException("Tracked browser screenshot capture did not return a result payload.");
        if (string.IsNullOrWhiteSpace(result.Data))
        {
            throw new InvalidOperationException("Tracked browser screenshot capture did not return image data.");
        }

        return result;
    }

    internal static BrowserLogsRuntimeEvaluateResult ParseRuntimeEvaluateResponse(ReadOnlySpan<byte> framePayload)
    {
        // Expected Runtime.evaluate response shape:
        // { "id": 1, "result": { "result": { "type": "string", "value": "{...}" }, "exceptionDetails": { ... } } }
        var envelope = DeserializeFrame(framePayload, BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsRuntimeEvaluateResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        var result = envelope.Result ?? throw new InvalidOperationException("Tracked browser script evaluation did not return a result payload.");
        if (result.ExceptionDetails is { } exceptionDetails)
        {
            throw new InvalidOperationException(FormatRuntimeException(exceptionDetails));
        }

        return result;
    }

    internal static string DescribeFrame(ReadOnlySpan<byte> framePayload, int maxLength = 512)
    {
        var text = Encoding.UTF8.GetString(framePayload);
        return text.Length <= maxLength
            ? text
            : $"{text[..maxLength]}...";
    }

    private static BrowserLogsConsoleApiCalledEvent? CreateConsoleApiCalledEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsConsoleApiCalledEnvelope,
            static (string? sessionId, BrowserLogsRuntimeConsoleApiCalledParameters parameters) => new BrowserLogsConsoleApiCalledEvent(sessionId, parameters));

    private static BrowserLogsExceptionThrownEvent? CreateExceptionThrownEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsExceptionThrownEnvelope,
            static (string? sessionId, BrowserLogsExceptionThrownParameters parameters) => new BrowserLogsExceptionThrownEvent(sessionId, parameters));

    private static BrowserLogsLogEntryAddedEvent? CreateLogEntryAddedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsLogEntryAddedEnvelope,
            static (string? sessionId, BrowserLogsLogEntryAddedParameters parameters) => new BrowserLogsLogEntryAddedEvent(sessionId, parameters));

    private static BrowserLogsRequestWillBeSentEvent? CreateRequestWillBeSentEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsRequestWillBeSentEnvelope,
            static (string? sessionId, BrowserLogsRequestWillBeSentParameters parameters) => new BrowserLogsRequestWillBeSentEvent(sessionId, parameters));

    private static BrowserLogsResponseReceivedEvent? CreateResponseReceivedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsResponseReceivedEnvelope,
            static (string? sessionId, BrowserLogsResponseReceivedParameters parameters) => new BrowserLogsResponseReceivedEvent(sessionId, parameters));

    private static BrowserLogsLoadingFinishedEvent? CreateLoadingFinishedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsLoadingFinishedEnvelope,
            static (string? sessionId, BrowserLogsLoadingFinishedParameters parameters) => new BrowserLogsLoadingFinishedEvent(sessionId, parameters));

    private static BrowserLogsLoadingFailedEvent? CreateLoadingFailedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsLoadingFailedEnvelope,
            static (string? sessionId, BrowserLogsLoadingFailedParameters parameters) => new BrowserLogsLoadingFailedEvent(sessionId, parameters));

    private static BrowserLogsTargetDestroyedEvent? CreateTargetDestroyedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsTargetDestroyedEnvelope,
            static (string? sessionId, BrowserLogsTargetDestroyedParameters parameters) => new BrowserLogsTargetDestroyedEvent(sessionId, parameters));

    private static BrowserLogsTargetCrashedEvent? CreateTargetCrashedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsTargetCrashedEnvelope,
            static (string? sessionId, BrowserLogsTargetCrashedParameters parameters) => new BrowserLogsTargetCrashedEvent(sessionId, parameters));

    private static BrowserLogsDetachedFromTargetEvent? CreateDetachedFromTargetEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsDetachedFromTargetEnvelope,
            static (string? sessionId, BrowserLogsDetachedFromTargetParameters parameters) => new BrowserLogsDetachedFromTargetEvent(sessionId, parameters));

    private static BrowserLogsInspectorDetachedEvent? CreateInspectorDetachedEvent(ReadOnlySpan<byte> framePayload)
        => CreateEvent(
            framePayload,
            BrowserLogsCdpProtocolJsonContext.Default.BrowserLogsInspectorDetachedEnvelope,
            static (string? sessionId, BrowserLogsInspectorDetachedParameters parameters) => new BrowserLogsInspectorDetachedEvent(sessionId, parameters));

    private static TEvent? CreateEvent<TEnvelope, TParameters, TEvent>(
        ReadOnlySpan<byte> framePayload,
        JsonTypeInfo<TEnvelope> jsonTypeInfo,
        Func<string?, TParameters, TEvent> createEvent)
        where TEnvelope : class, IBrowserLogsEventEnvelope<TParameters>
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

    private static void ThrowIfProtocolError(BrowserLogsCdpProtocolError? error)
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

    private static string FormatRuntimeException(BrowserLogsExceptionDetails exceptionDetails)
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

internal readonly record struct BrowserLogsCdpProtocolMessageHeader(long? Id, string? Method, string? SessionId);

internal abstract record BrowserLogsCdpProtocolEvent(string Method, string? SessionId);

internal sealed record BrowserLogsConsoleApiCalledEvent(string? SessionId, BrowserLogsRuntimeConsoleApiCalledParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.RuntimeConsoleApiCalledMethod, SessionId);

internal sealed record BrowserLogsExceptionThrownEvent(string? SessionId, BrowserLogsExceptionThrownParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.RuntimeExceptionThrownMethod, SessionId);

internal sealed record BrowserLogsLoadingFailedEvent(string? SessionId, BrowserLogsLoadingFailedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.NetworkLoadingFailedMethod, SessionId);

internal sealed record BrowserLogsLoadingFinishedEvent(string? SessionId, BrowserLogsLoadingFinishedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.NetworkLoadingFinishedMethod, SessionId);

internal sealed record BrowserLogsLogEntryAddedEvent(string? SessionId, BrowserLogsLogEntryAddedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.LogEntryAddedMethod, SessionId);

internal sealed record BrowserLogsRequestWillBeSentEvent(string? SessionId, BrowserLogsRequestWillBeSentParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.NetworkRequestWillBeSentMethod, SessionId);

internal sealed record BrowserLogsResponseReceivedEvent(string? SessionId, BrowserLogsResponseReceivedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.NetworkResponseReceivedMethod, SessionId);

// Target lifecycle events differ from page-domain events in routing semantics:
// - For Page/Runtime/Network/Log events, BrowserLogsCdpProtocolEvent.SessionId is the attached page session and
//   the dispatcher routes by matching it against the tracked target's session id.
// - For Target.targetDestroyed/targetCrashed and Inspector.detached, the envelope-level sessionId is typically
//   absent (these are fired on the browser CDP channel, not on a target session). The SUBJECT of the event is
//   carried in the parameters: targetId for target events, the parent attached sessionId for the implicit
//   detach. Routing logic must not rely on BrowserLogsCdpProtocolEvent.SessionId for these.
// - For Target.detachedFromTarget specifically, params.sessionId identifies the session that detached, which is
//   the value that should be matched against the tracked target's session id.
internal sealed record BrowserLogsTargetDestroyedEvent(string? SessionId, BrowserLogsTargetDestroyedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.TargetTargetDestroyedMethod, SessionId)
{
    public string? TargetId => Parameters.TargetId;
}

internal sealed record BrowserLogsTargetCrashedEvent(string? SessionId, BrowserLogsTargetCrashedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.TargetTargetCrashedMethod, SessionId)
{
    public string? TargetId => Parameters.TargetId;
}

internal sealed record BrowserLogsDetachedFromTargetEvent(string? SessionId, BrowserLogsDetachedFromTargetParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.TargetDetachedFromTargetMethod, SessionId)
{
    public string? DetachedSessionId => Parameters.SessionId;

    public string? TargetId => Parameters.TargetId;
}

internal sealed record BrowserLogsInspectorDetachedEvent(string? SessionId, BrowserLogsInspectorDetachedParameters Parameters)
    : BrowserLogsCdpProtocolEvent(BrowserLogsCdpProtocol.InspectorDetachedMethod, SessionId)
{
    public string? Reason => Parameters.Reason;
}

internal sealed class BrowserLogsAttachToTargetResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsAttachToTargetResult? Result { get; init; }
}

internal sealed class BrowserLogsAttachToTargetResult
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsCommandAck
{
    public static BrowserLogsCommandAck Instance { get; } = new();

    private BrowserLogsCommandAck()
    {
    }
}

internal sealed class BrowserLogsCommandAckResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }
}

internal sealed class BrowserLogsCaptureScreenshotResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsCaptureScreenshotResult? Result { get; init; }
}

internal sealed class BrowserLogsCaptureScreenshotResult
{
    [JsonPropertyName("data")]
    public string? Data { get; init; }
}

internal sealed class BrowserLogsRuntimeEvaluateResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsRuntimeEvaluateResult? Result { get; init; }
}

internal sealed class BrowserLogsRuntimeEvaluateResult
{
    [JsonPropertyName("exceptionDetails")]
    public BrowserLogsExceptionDetails? ExceptionDetails { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsCdpProtocolRemoteObject? Result { get; init; }
}

internal interface IBrowserLogsEventEnvelope<out TParameters>
    where TParameters : class
{
    TParameters? Params { get; }

    string? SessionId { get; }
}

internal sealed class BrowserLogsConsoleApiCalledEnvelope : IBrowserLogsEventEnvelope<BrowserLogsRuntimeConsoleApiCalledParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsRuntimeConsoleApiCalledParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsCreateTargetResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsCreateTargetResult? Result { get; init; }
}

internal sealed class BrowserLogsCreateTargetResult
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserLogsGetTargetsResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsCdpProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("result")]
    public BrowserLogsGetTargetsResult? Result { get; init; }
}

internal sealed class BrowserLogsGetTargetsResult
{
    [JsonPropertyName("targetInfos")]
    public BrowserLogsTargetInfo[]? TargetInfos { get; init; }
}

internal sealed class BrowserLogsTargetInfo
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

internal sealed class BrowserLogsExceptionDetails : BrowserLogsSourceLocation
{
    [JsonPropertyName("exception")]
    public BrowserLogsExceptionObject? Exception { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

internal sealed class BrowserLogsExceptionObject
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

internal sealed class BrowserLogsExceptionThrownEnvelope : IBrowserLogsEventEnvelope<BrowserLogsExceptionThrownParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsExceptionThrownParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsExceptionThrownParameters
{
    [JsonPropertyName("exceptionDetails")]
    public BrowserLogsExceptionDetails? ExceptionDetails { get; init; }
}

internal sealed class BrowserLogsLoadingFailedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsLoadingFailedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsLoadingFailedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsLoadingFailedParameters
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

internal sealed class BrowserLogsLoadingFinishedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsLoadingFinishedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsLoadingFinishedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsLoadingFinishedParameters
{
    [JsonPropertyName("encodedDataLength")]
    public double? EncodedDataLength { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }
}

internal sealed class BrowserLogsLogEntry : BrowserLogsSourceLocation
{
    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

internal sealed class BrowserLogsLogEntryAddedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsLogEntryAddedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsLogEntryAddedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsLogEntryAddedParameters
{
    [JsonPropertyName("entry")]
    public BrowserLogsLogEntry? Entry { get; init; }
}

internal sealed class BrowserLogsCdpProtocolError
{
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class BrowserLogsCdpProtocolRemoteObject
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("unserializableValue")]
    public string? UnserializableValue { get; init; }

    [JsonPropertyName("value")]
    public BrowserLogsCdpProtocolValue? Value { get; init; }
}

internal sealed class BrowserLogsRequest
{
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserLogsRequestWillBeSentEnvelope : IBrowserLogsEventEnvelope<BrowserLogsRequestWillBeSentParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsRequestWillBeSentParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsRequestWillBeSentParameters
{
    [JsonPropertyName("redirectResponse")]
    public BrowserLogsResponse? RedirectResponse { get; init; }

    [JsonPropertyName("request")]
    public BrowserLogsRequest? Request { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("timestamp")]
    public double? Timestamp { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed class BrowserLogsResponse
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

internal sealed class BrowserLogsResponseReceivedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsResponseReceivedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsResponseReceivedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsResponseReceivedParameters
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("response")]
    public BrowserLogsResponse? Response { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal sealed class BrowserLogsRuntimeConsoleApiCalledParameters
{
    [JsonPropertyName("args")]
    public BrowserLogsCdpProtocolRemoteObject[]? Args { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

internal class BrowserLogsSourceLocation
{
    [JsonPropertyName("columnNumber")]
    public int? ColumnNumber { get; init; }

    [JsonPropertyName("lineNumber")]
    public int? LineNumber { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserLogsTargetDestroyedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsTargetDestroyedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsTargetDestroyedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsTargetDestroyedParameters
{
    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserLogsTargetCrashedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsTargetCrashedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsTargetCrashedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsTargetCrashedParameters
{
    [JsonPropertyName("errorCode")]
    public int? ErrorCode { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserLogsDetachedFromTargetEnvelope : IBrowserLogsEventEnvelope<BrowserLogsDetachedFromTargetParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsDetachedFromTargetParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsDetachedFromTargetParameters
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("targetId")]
    public string? TargetId { get; init; }
}

internal sealed class BrowserLogsInspectorDetachedEnvelope : IBrowserLogsEventEnvelope<BrowserLogsInspectorDetachedParameters>
{
    [JsonPropertyName("params")]
    public BrowserLogsInspectorDetachedParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsInspectorDetachedParameters
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

[JsonConverter(typeof(BrowserLogsCdpProtocolValueJsonConverter))]
internal abstract record BrowserLogsCdpProtocolValue;

internal sealed record BrowserLogsCdpProtocolArrayValue(IReadOnlyList<BrowserLogsCdpProtocolValue> Items) : BrowserLogsCdpProtocolValue;

internal sealed record BrowserLogsCdpProtocolBooleanValue(bool Value) : BrowserLogsCdpProtocolValue;

internal sealed record BrowserLogsCdpProtocolNullValue : BrowserLogsCdpProtocolValue
{
    public static BrowserLogsCdpProtocolNullValue Instance { get; } = new();

    private BrowserLogsCdpProtocolNullValue()
    {
    }
}

internal sealed record BrowserLogsCdpProtocolNumberValue(string RawValue) : BrowserLogsCdpProtocolValue;

internal sealed record BrowserLogsCdpProtocolObjectValue(IReadOnlyDictionary<string, BrowserLogsCdpProtocolValue> Properties) : BrowserLogsCdpProtocolValue;

internal sealed record BrowserLogsCdpProtocolStringValue(string Value) : BrowserLogsCdpProtocolValue;

internal sealed class BrowserLogsCdpProtocolValueJsonConverter : JsonConverter<BrowserLogsCdpProtocolValue>
{
    public override BrowserLogsCdpProtocolValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.StartArray => ReadArray(ref reader, options),
        JsonTokenType.StartObject => ReadObject(ref reader, options),
        JsonTokenType.String => new BrowserLogsCdpProtocolStringValue(reader.GetString() ?? string.Empty),
        JsonTokenType.True => new BrowserLogsCdpProtocolBooleanValue(true),
        JsonTokenType.False => new BrowserLogsCdpProtocolBooleanValue(false),
        JsonTokenType.Null => BrowserLogsCdpProtocolNullValue.Instance,
        JsonTokenType.Number => new BrowserLogsCdpProtocolNumberValue(GetRawNumber(ref reader)),
        _ => throw new JsonException($"Unsupported JSON token '{reader.TokenType}' for tracked browser protocol value.")
    };

    public override void Write(Utf8JsonWriter writer, BrowserLogsCdpProtocolValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case BrowserLogsCdpProtocolArrayValue arrayValue:
                writer.WriteStartArray();
                foreach (var item in arrayValue.Items)
                {
                    Write(writer, item, options);
                }

                writer.WriteEndArray();
                break;
            case BrowserLogsCdpProtocolBooleanValue booleanValue:
                writer.WriteBooleanValue(booleanValue.Value);
                break;
            case BrowserLogsCdpProtocolNullValue:
                writer.WriteNullValue();
                break;
            case BrowserLogsCdpProtocolNumberValue numberValue:
                writer.WriteRawValue(numberValue.RawValue, skipInputValidation: true);
                break;
            case BrowserLogsCdpProtocolObjectValue objectValue:
                writer.WriteStartObject();
                foreach (var (propertyName, propertyValue) in objectValue.Properties)
                {
                    writer.WritePropertyName(propertyName);
                    Write(writer, propertyValue, options);
                }

                writer.WriteEndObject();
                break;
            case BrowserLogsCdpProtocolStringValue stringValue:
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

    private static BrowserLogsCdpProtocolArrayValue ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var items = new List<BrowserLogsCdpProtocolValue>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            items.Add(ReadValue(ref reader, options));
        }

        return new BrowserLogsCdpProtocolArrayValue(items);
    }

    private static BrowserLogsCdpProtocolObjectValue ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var properties = new Dictionary<string, BrowserLogsCdpProtocolValue>(StringComparer.Ordinal);

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

        return new BrowserLogsCdpProtocolObjectValue(properties);
    }

    private static BrowserLogsCdpProtocolValue ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var converter = (BrowserLogsCdpProtocolValueJsonConverter)options.GetConverter(typeof(BrowserLogsCdpProtocolValue));
        return converter.Read(ref reader, typeof(BrowserLogsCdpProtocolValue), options);
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserLogsAttachToTargetResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsCaptureScreenshotResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsCommandAckResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsConsoleApiCalledEnvelope))]
[JsonSerializable(typeof(BrowserLogsCreateTargetResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsDetachedFromTargetEnvelope))]
[JsonSerializable(typeof(BrowserLogsGetTargetsResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsExceptionThrownEnvelope))]
[JsonSerializable(typeof(BrowserLogsInspectorDetachedEnvelope))]
[JsonSerializable(typeof(BrowserLogsLoadingFailedEnvelope))]
[JsonSerializable(typeof(BrowserLogsLoadingFinishedEnvelope))]
[JsonSerializable(typeof(BrowserLogsLogEntryAddedEnvelope))]
[JsonSerializable(typeof(BrowserLogsRequestWillBeSentEnvelope))]
[JsonSerializable(typeof(BrowserLogsResponseReceivedEnvelope))]
[JsonSerializable(typeof(BrowserLogsRuntimeEvaluateResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsTargetCrashedEnvelope))]
[JsonSerializable(typeof(BrowserLogsTargetDestroyedEnvelope))]
internal sealed partial class BrowserLogsCdpProtocolJsonContext : JsonSerializerContext;
