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
internal static class BrowserLogsProtocol
{
    private static readonly JsonWriterOptions s_commandFrameWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal const string BrowserCloseMethod = "Browser.close";
    internal const string LogEnableMethod = "Log.enable";
    internal const string LogEntryAddedMethod = "Log.entryAdded";
    internal const string NetworkEnableMethod = "Network.enable";
    internal const string NetworkLoadingFailedMethod = "Network.loadingFailed";
    internal const string NetworkLoadingFinishedMethod = "Network.loadingFinished";
    internal const string NetworkRequestWillBeSentMethod = "Network.requestWillBeSent";
    internal const string NetworkResponseReceivedMethod = "Network.responseReceived";
    internal const string PageEnableMethod = "Page.enable";
    internal const string PageNavigateMethod = "Page.navigate";
    internal const string RuntimeConsoleApiCalledMethod = "Runtime.consoleAPICalled";
    internal const string RuntimeEnableMethod = "Runtime.enable";
    internal const string RuntimeExceptionThrownMethod = "Runtime.exceptionThrown";
    internal const string TargetAttachToTargetMethod = "Target.attachToTarget";
    internal const string TargetCreateTargetMethod = "Target.createTarget";
    internal const string TargetGetTargetsMethod = "Target.getTargets";

    internal static BrowserLogsProtocolMessageHeader ParseMessageHeader(ReadOnlySpan<byte> framePayload)
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

        return new BrowserLogsProtocolMessageHeader(id, method, sessionId);
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

    internal static BrowserLogsProtocolEvent? ParseEvent(BrowserLogsProtocolMessageHeader header, ReadOnlySpan<byte> framePayload) => header.Method switch
    {
        RuntimeConsoleApiCalledMethod => CreateConsoleApiCalledEvent(framePayload),
        RuntimeExceptionThrownMethod => CreateExceptionThrownEvent(framePayload),
        LogEntryAddedMethod => CreateLogEntryAddedEvent(framePayload),
        NetworkRequestWillBeSentMethod => CreateRequestWillBeSentEvent(framePayload),
        NetworkResponseReceivedMethod => CreateResponseReceivedEvent(framePayload),
        NetworkLoadingFinishedMethod => CreateLoadingFinishedEvent(framePayload),
        NetworkLoadingFailedMethod => CreateLoadingFailedEvent(framePayload),
        _ => null
    };

    internal static BrowserLogsCreateTargetResult ParseCreateTargetResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsCreateTargetResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target creation did not return a result payload.");
    }

    internal static BrowserLogsAttachToTargetResult ParseAttachToTargetResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsAttachToTargetResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target attachment did not return a result payload.");
    }

    internal static BrowserLogsGetTargetsResult ParseGetTargetsResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsGetTargetsResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return envelope.Result ?? throw new InvalidOperationException("Tracked browser target discovery did not return a result payload.");
    }

    internal static BrowserLogsCommandAck ParseCommandAckResponse(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsCommandAckResponseEnvelope);
        ThrowIfProtocolError(envelope.Error);

        return BrowserLogsCommandAck.Instance;
    }

    internal static string DescribeFrame(ReadOnlySpan<byte> framePayload, int maxLength = 512)
    {
        var text = Encoding.UTF8.GetString(framePayload);
        return text.Length <= maxLength
            ? text
            : $"{text[..maxLength]}...";
    }

    private static BrowserLogsConsoleApiCalledEvent? CreateConsoleApiCalledEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsConsoleApiCalledEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsConsoleApiCalledEvent(envelope.SessionId, envelope.Params);
    }

    private static BrowserLogsExceptionThrownEvent? CreateExceptionThrownEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsExceptionThrownEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsExceptionThrownEvent(envelope.SessionId, envelope.Params);
    }

    private static BrowserLogsLogEntryAddedEvent? CreateLogEntryAddedEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsLogEntryAddedEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsLogEntryAddedEvent(envelope.SessionId, envelope.Params);
    }

    private static BrowserLogsRequestWillBeSentEvent? CreateRequestWillBeSentEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsRequestWillBeSentEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsRequestWillBeSentEvent(envelope.SessionId, envelope.Params);
    }

    private static BrowserLogsResponseReceivedEvent? CreateResponseReceivedEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsResponseReceivedEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsResponseReceivedEvent(envelope.SessionId, envelope.Params);
    }

    private static BrowserLogsLoadingFinishedEvent? CreateLoadingFinishedEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsLoadingFinishedEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsLoadingFinishedEvent(envelope.SessionId, envelope.Params);
    }

    private static BrowserLogsLoadingFailedEvent? CreateLoadingFailedEvent(ReadOnlySpan<byte> framePayload)
    {
        var envelope = DeserializeFrame(framePayload, BrowserLogsProtocolJsonContext.Default.BrowserLogsLoadingFailedEnvelope);
        return envelope.Params is null
            ? null
            : new BrowserLogsLoadingFailedEvent(envelope.SessionId, envelope.Params);
    }

    private static T DeserializeFrame<T>(ReadOnlySpan<byte> framePayload, JsonTypeInfo<T> jsonTypeInfo)
        where T : class
    {
        return JsonSerializer.Deserialize(framePayload, jsonTypeInfo)
            ?? throw new InvalidOperationException("Tracked browser protocol frame was empty.");
    }

    private static void ThrowIfProtocolError(BrowserLogsProtocolError? error)
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
}

internal readonly record struct BrowserLogsProtocolMessageHeader(long? Id, string? Method, string? SessionId);

internal abstract record BrowserLogsProtocolEvent(string Method, string? SessionId);

internal sealed record BrowserLogsConsoleApiCalledEvent(string? SessionId, BrowserLogsRuntimeConsoleApiCalledParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.RuntimeConsoleApiCalledMethod, SessionId);

internal sealed record BrowserLogsExceptionThrownEvent(string? SessionId, BrowserLogsExceptionThrownParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.RuntimeExceptionThrownMethod, SessionId);

internal sealed record BrowserLogsLoadingFailedEvent(string? SessionId, BrowserLogsLoadingFailedParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.NetworkLoadingFailedMethod, SessionId);

internal sealed record BrowserLogsLoadingFinishedEvent(string? SessionId, BrowserLogsLoadingFinishedParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.NetworkLoadingFinishedMethod, SessionId);

internal sealed record BrowserLogsLogEntryAddedEvent(string? SessionId, BrowserLogsLogEntryAddedParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.LogEntryAddedMethod, SessionId);

internal sealed record BrowserLogsRequestWillBeSentEvent(string? SessionId, BrowserLogsRequestWillBeSentParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.NetworkRequestWillBeSentMethod, SessionId);

internal sealed record BrowserLogsResponseReceivedEvent(string? SessionId, BrowserLogsResponseReceivedParameters Parameters)
    : BrowserLogsProtocolEvent(BrowserLogsProtocol.NetworkResponseReceivedMethod, SessionId);

internal sealed class BrowserLogsAttachToTargetResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsProtocolError? Error { get; init; }

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
    public BrowserLogsProtocolError? Error { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }
}

internal sealed class BrowserLogsConsoleApiCalledEnvelope
{
    [JsonPropertyName("params")]
    public BrowserLogsRuntimeConsoleApiCalledParameters? Params { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class BrowserLogsCreateTargetResponseEnvelope
{
    [JsonPropertyName("error")]
    public BrowserLogsProtocolError? Error { get; init; }

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
    public BrowserLogsProtocolError? Error { get; init; }

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

internal sealed class BrowserLogsExceptionThrownEnvelope
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

internal sealed class BrowserLogsLoadingFailedEnvelope
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

internal sealed class BrowserLogsLoadingFinishedEnvelope
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

internal sealed class BrowserLogsLogEntryAddedEnvelope
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

internal sealed class BrowserLogsProtocolError
{
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

internal sealed class BrowserLogsProtocolRemoteObject
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("unserializableValue")]
    public string? UnserializableValue { get; init; }

    [JsonPropertyName("value")]
    public BrowserLogsProtocolValue? Value { get; init; }
}

internal sealed class BrowserLogsRequest
{
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class BrowserLogsRequestWillBeSentEnvelope
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

internal sealed class BrowserLogsResponseReceivedEnvelope
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
    public BrowserLogsProtocolRemoteObject[]? Args { get; init; }

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

[JsonConverter(typeof(BrowserLogsProtocolValueJsonConverter))]
internal abstract record BrowserLogsProtocolValue;

internal sealed record BrowserLogsProtocolArrayValue(IReadOnlyList<BrowserLogsProtocolValue> Items) : BrowserLogsProtocolValue;

internal sealed record BrowserLogsProtocolBooleanValue(bool Value) : BrowserLogsProtocolValue;

internal sealed record BrowserLogsProtocolNullValue : BrowserLogsProtocolValue
{
    public static BrowserLogsProtocolNullValue Instance { get; } = new();

    private BrowserLogsProtocolNullValue()
    {
    }
}

internal sealed record BrowserLogsProtocolNumberValue(string RawValue) : BrowserLogsProtocolValue;

internal sealed record BrowserLogsProtocolObjectValue(IReadOnlyDictionary<string, BrowserLogsProtocolValue> Properties) : BrowserLogsProtocolValue;

internal sealed record BrowserLogsProtocolStringValue(string Value) : BrowserLogsProtocolValue;

internal sealed class BrowserLogsProtocolValueJsonConverter : JsonConverter<BrowserLogsProtocolValue>
{
    public override BrowserLogsProtocolValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.StartArray => ReadArray(ref reader, options),
        JsonTokenType.StartObject => ReadObject(ref reader, options),
        JsonTokenType.String => new BrowserLogsProtocolStringValue(reader.GetString() ?? string.Empty),
        JsonTokenType.True => new BrowserLogsProtocolBooleanValue(true),
        JsonTokenType.False => new BrowserLogsProtocolBooleanValue(false),
        JsonTokenType.Null => BrowserLogsProtocolNullValue.Instance,
        JsonTokenType.Number => new BrowserLogsProtocolNumberValue(GetRawNumber(ref reader)),
        _ => throw new JsonException($"Unsupported JSON token '{reader.TokenType}' for tracked browser protocol value.")
    };

    public override void Write(Utf8JsonWriter writer, BrowserLogsProtocolValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case BrowserLogsProtocolArrayValue arrayValue:
                writer.WriteStartArray();
                foreach (var item in arrayValue.Items)
                {
                    Write(writer, item, options);
                }

                writer.WriteEndArray();
                break;
            case BrowserLogsProtocolBooleanValue booleanValue:
                writer.WriteBooleanValue(booleanValue.Value);
                break;
            case BrowserLogsProtocolNullValue:
                writer.WriteNullValue();
                break;
            case BrowserLogsProtocolNumberValue numberValue:
                writer.WriteRawValue(numberValue.RawValue, skipInputValidation: true);
                break;
            case BrowserLogsProtocolObjectValue objectValue:
                writer.WriteStartObject();
                foreach (var (propertyName, propertyValue) in objectValue.Properties)
                {
                    writer.WritePropertyName(propertyName);
                    Write(writer, propertyValue, options);
                }

                writer.WriteEndObject();
                break;
            case BrowserLogsProtocolStringValue stringValue:
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

    private static BrowserLogsProtocolArrayValue ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var items = new List<BrowserLogsProtocolValue>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            items.Add(ReadValue(ref reader, options));
        }

        return new BrowserLogsProtocolArrayValue(items);
    }

    private static BrowserLogsProtocolObjectValue ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var properties = new Dictionary<string, BrowserLogsProtocolValue>(StringComparer.Ordinal);

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

        return new BrowserLogsProtocolObjectValue(properties);
    }

    private static BrowserLogsProtocolValue ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var converter = (BrowserLogsProtocolValueJsonConverter)options.GetConverter(typeof(BrowserLogsProtocolValue));
        return converter.Read(ref reader, typeof(BrowserLogsProtocolValue), options);
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserLogsAttachToTargetResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsCommandAckResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsConsoleApiCalledEnvelope))]
[JsonSerializable(typeof(BrowserLogsCreateTargetResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsGetTargetsResponseEnvelope))]
[JsonSerializable(typeof(BrowserLogsExceptionThrownEnvelope))]
[JsonSerializable(typeof(BrowserLogsLoadingFailedEnvelope))]
[JsonSerializable(typeof(BrowserLogsLoadingFinishedEnvelope))]
[JsonSerializable(typeof(BrowserLogsLogEntryAddedEnvelope))]
[JsonSerializable(typeof(BrowserLogsRequestWillBeSentEnvelope))]
[JsonSerializable(typeof(BrowserLogsResponseReceivedEnvelope))]
internal sealed partial class BrowserLogsProtocolJsonContext : JsonSerializerContext;
