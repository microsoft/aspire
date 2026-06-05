// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Aspire.Dashboard.Otlp.Model;

public static class OtlpHelpers
{
    // Reduce size of JSON data by not indenting. Dashboard UI supports formatting JSON values when they're displayed.
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = false
    };

    public const int ShortenedIdLength = 7;

    public static ResourceKey GetResourceKey(this Resource resource)
    {
        string? serviceName = null;
        string? serviceInstanceId = null;
        string? processExecutableName = null;

        for (var i = 0; i < resource.Attributes.Count; i++)
        {
            var attribute = resource.Attributes[i];
            if (attribute.Key == OtlpResource.SERVICE_INSTANCE_ID)
            {
                serviceInstanceId = attribute.Value.GetString();
            }
            if (attribute.Key == OtlpResource.SERVICE_NAME)
            {
                serviceName = attribute.Value.GetString();
            }
            if (attribute.Key == OtlpResource.PROCESS_EXECUTABLE_NAME)
            {
                processExecutableName = attribute.Value.GetString();
            }
        }

        // Fallback to unknown_service if service name isn't specified.
        // https://github.com/open-telemetry/opentelemetry-specification/issues/3210
        if (string.IsNullOrEmpty(serviceName))
        {
            serviceName = "unknown_service";
            if (!string.IsNullOrEmpty(processExecutableName))
            {
                serviceName += ":" + processExecutableName;
            }
        }

        // service.instance.id is recommended but not required.
        return new ResourceKey(serviceName, serviceInstanceId);
    }

    public static string ToShortenedId(string id) => TruncateString(id, maxLength: ShortenedIdLength);

    public static string ToHexString(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        // This produces lowercase hex string from the bytes. It's used instead of Convert.ToHexString()
        // because we want to display lowercase hex string in the UI for values such as traceid and spanid.
        return string.Create(bytes.Length * 2, bytes, static (chars, bytes) =>
        {
            var data = bytes.Span;
            for (var pos = 0; pos < data.Length; pos++)
            {
                ToCharsBuffer(data[pos], chars, pos * 2);
            }
        });
    }

    public static string TruncateString(string value, int maxLength)
    {
        return value.Length > maxLength ? value[..maxLength] : value;
    }

    public static string ToHexString(this ByteString bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return ToHexString(bytes.Memory);
    }

    public static string GetString(this AnyValue? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(CultureInfo.InvariantCulture),
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            AnyValue.ValueOneofCase.BytesValue => value.BytesValue.ToHexString(),
            AnyValue.ValueOneofCase.ArrayValue => ConvertAnyValue(value)!.ToJsonString(s_jsonSerializerOptions),
            AnyValue.ValueOneofCase.KvlistValue => ConvertAnyValue(value)!.ToJsonString(s_jsonSerializerOptions),
            AnyValue.ValueOneofCase.None => string.Empty,
            _ => value.ToString(),
        };
    }

    private static JsonNode? ConvertAnyValue(AnyValue value)
    {
        // Recursively convert AnyValue types to JsonNode types to produce more idiomatic JSON.
        // Recursing over incoming values is safe because Protobuf serializer imposes a safe limit on recursive messages.
        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => JsonValue.Create(value.StringValue),
            AnyValue.ValueOneofCase.IntValue => JsonValue.Create(value.IntValue),
            AnyValue.ValueOneofCase.DoubleValue => JsonValue.Create(value.DoubleValue),
            AnyValue.ValueOneofCase.BoolValue => JsonValue.Create(value.BoolValue),
            AnyValue.ValueOneofCase.BytesValue => JsonValue.Create(value.BytesValue.ToHexString()),
            AnyValue.ValueOneofCase.ArrayValue => ConvertArray(value.ArrayValue),
            AnyValue.ValueOneofCase.KvlistValue => ConvertKeyValues(value.KvlistValue),
            AnyValue.ValueOneofCase.None => null,
            _ => throw new InvalidOperationException($"Unexpected AnyValue type: {value.ValueCase}"),
        };

        static JsonArray ConvertArray(ArrayValue value)
        {
            var a = new JsonArray();
            foreach (var item in value.Values)
            {
                a.Add(ConvertAnyValue(item));
            }
            return a;
        }

        static JsonObject ConvertKeyValues(KeyValueList value)
        {
            var o = new JsonObject();
            foreach (var item in value.Values)
            {
                o[item.Key] = ConvertAnyValue(item.Value);
            }
            return o;
        }
    }

    // From https://github.com/dotnet/runtime/blob/963954a11c1beeea4ad63002084a213d8d742864/src/libraries/Common/src/System/HexConverter.cs#L81-L89
    // Modified slightly to always produce lowercase output.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ToCharsBuffer(byte value, Span<char> buffer, int startingIndex = 0)
    {
        var difference = ((value & 0xF0U) << 4) + (value & 0x0FU) - 0x8989U;
        var packedResult = (((uint)-(int)difference & 0x7070U) >> 4) + difference + 0xB9B9U | 0x2020U;

        buffer[startingIndex + 1] = (char)(packedResult & 0xFF);
        buffer[startingIndex] = (char)(packedResult >> 8);
    }

    public static DateTime UnixNanoSecondsToDateTime(ulong unixTimeNanoseconds)
    {
        var ticks = NanosecondsToTicks(unixTimeNanoseconds);

        return DateTime.UnixEpoch.AddTicks(ticks);
    }

    private static long NanosecondsToTicks(ulong nanoseconds)
    {
        return (long)(nanoseconds / TimeSpan.NanosecondsPerTick);
    }

    public static KeyValuePair<string, string>[] ToKeyValuePairs(this RepeatedField<KeyValue> attributes, OtlpContext context)
    {
        if (attributes.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var values = new KeyValuePair<string, string>[Math.Min(attributes.Count, context.Options.MaxAttributeCount)];
        CopyKeyValues(attributes, values, index: 0, context, out var copyCount);

        if (values.Length == copyCount)
        {
            return values;
        }
        else
        {
            return values[..copyCount];
        }
    }

    public static KeyValuePair<string, string>[] ToKeyValuePairs(this RepeatedField<KeyValue> attributes, OtlpContext context, Func<KeyValue, bool> filter)
    {
        if (attributes.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var readLimit = Math.Min(attributes.Count, context.Options.MaxAttributeCount);
        List<KeyValuePair<string, string>>? values = null;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];

            if (!filter(attribute))
            {
                continue;
            }

            values ??= new List<KeyValuePair<string, string>>(readLimit);

            var value = TruncateString(attribute.Value.GetString(), context.Options.MaxAttributeLength);

            // If there are duplicates then last value wins.
            var existingIndex = GetIndex(values, attribute.Key);
            if (existingIndex >= 0)
            {
                var existingAttribute = values[existingIndex];
                if (existingAttribute.Value != value)
                {
                    context.Logger.LogDebug("Duplicate attribute {Name} with different value. Last value wins.", attribute.Key);
                    values[existingIndex] = new KeyValuePair<string, string>(attribute.Key, value);
                }
            }
            else
            {
                if (values.Count < readLimit)
                {
                    values.Add(new KeyValuePair<string, string>(attribute.Key, value));
                }
            }
        }

        return values?.ToArray() ?? [];

        static int GetIndex(List<KeyValuePair<string, string>> values, string name)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].Key == name)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    public static void CopyKeyValuePairs(RepeatedField<KeyValue> attributes, KeyValuePair<string, string>[] parentAttributes, OtlpContext context, out int copyCount, [NotNull] ref KeyValuePair<string, string>[]? copiedAttributes)
    {
        copyCount = Math.Min(parentAttributes.Length + attributes.Count, context.Options.MaxAttributeCount);

        // Attribute limit already reached.
        if (copyCount == parentAttributes.Length)
        {
            copiedAttributes = parentAttributes;
            return;
        }

        if (copiedAttributes is null || copiedAttributes.Length < copyCount)
        {
            // Existing array isn't big enough. Create new bigger array.
            copiedAttributes = new KeyValuePair<string, string>[copyCount];
        }
        else
        {
            // Clear existing array before reuse.
            Array.Clear(copiedAttributes);
        }

        parentAttributes.AsSpan().CopyTo(copiedAttributes);

        CopyKeyValues(attributes, copiedAttributes, parentAttributes.Length, context, out var newCopyCount);
        copyCount = parentAttributes.Length + newCopyCount;
    }

    private static void CopyKeyValues(RepeatedField<KeyValue> attributes, KeyValuePair<string, string>[] copiedAttributes, int index, OtlpContext context, out int copyCount)
    {
        var desiredCopyCount = Math.Min(attributes.Count + index, context.Options.MaxAttributeCount);
        desiredCopyCount -= index;

        // Don't immediately break out of loop when the limit is reached. The rules for attributes is last value wins.
        // That means we want to loop through all attributes to check for new values to overwrite old.
        copyCount = 0;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];

            var value = TruncateString(attribute.Value.GetString(), context.Options.MaxAttributeLength);

            // If there are duplicates then last value wins.
            var existingIndex = GetIndex(copiedAttributes, attribute.Key);
            if (existingIndex >= 0)
            {
                var existingAttribute = copiedAttributes[existingIndex];
                if (existingAttribute.Value != value)
                {
                    context.Logger.LogDebug("Duplicate attribute {Name} with different value. Last value wins.", attribute.Key);
                    copiedAttributes[existingIndex] = new KeyValuePair<string, string>(attribute.Key, value);
                }
            }
            else
            {
                if (copyCount < desiredCopyCount)
                {
                    copiedAttributes[index + copyCount] = new KeyValuePair<string, string>(attribute.Key, value);
                    copyCount++;
                }
            }
        }
    }

    public static string? GetValue(this KeyValuePair<string, string>[] values, string name)
    {
        var i = values.GetIndex(name);
        if (i >= 0)
        {
            return values[i].Value;
        }

        return null;
    }

    public static string? GetValueWithFallback(this KeyValuePair<string, string>[] values, params ReadOnlySpan<string> names)
    {
        // Attempt each name in order and return the first match.
        foreach (var name in names)
        {
            if (values.GetValue(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    public static int? GetValueAsInteger(this KeyValuePair<string, string>[] values, string name)
    {
        var value = values.GetValue(name);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        int.TryParse(value, CultureInfo.InvariantCulture, out var result);
        return result;
    }

    public static int GetIndex(this KeyValuePair<string, string>[] values, string name)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].Key == name)
            {
                return i;
            }
        }
        return -1;
    }

    public static string? GetPeerAddress(this KeyValuePair<string, string>[] values)
    {
        var address = GetValue(values, OtlpSpan.PeerServiceAttributeKey);
        if (address != null)
        {
            return address;
        }

        // OTEL HTTP 1.7.0 doesn't return peer.service. Fallback to server.address and server.port.
        if (GetValue(values, OtlpSpan.ServerAddressAttributeKey) is { } server)
        {
            if (GetValue(values, OtlpSpan.ServerPortAttributeKey) is { } serverPort)
            {
                server += ":" + serverPort;
            }
            return server;
        }

        // Fallback to older names of net.peer.name and net.peer.port.
        if (GetValue(values, OtlpSpan.NetPeerNameAttributeKey) is { } peer)
        {
            if (GetValue(values, OtlpSpan.NetPeerPortAttributeKey) is { } peerPort)
            {
                peer += ":" + peerPort;
            }
            return peer;
        }

        return null;
    }

    public static bool HasKey(this KeyValuePair<string, string>[] values, string name)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].Key == name)
            {
                return true;
            }
        }
        return false;
    }

    public static string ConcatProperties(this KeyValuePair<string, string>[] properties)
    {
        StringBuilder sb = new();
        var first = true;
        foreach (var kv in properties)
        {
            if (!first)
            {
                sb.Append(", ");
            }
            first = false;
            sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: ");
            sb.Append(string.IsNullOrEmpty(kv.Value) ? "\'\'" : kv.Value);
        }
        return sb.ToString();
    }

    public static PagedResult<T> GetItems<T>(IEnumerable<T> results, int startIndex, int count, bool isFull)
    {
        return GetItems<T, T>(results, startIndex, count, isFull, null);
    }

    public static PagedResult<TResult> GetItems<TSource, TResult>(IEnumerable<TSource> results, int startIndex, int count, bool isFull, Func<TSource, TResult>? select)
    {
        var query = results.Skip(startIndex).Take(count);
        List<TResult> items;
        if (select != null)
        {
            items = query.Select(select).ToList();
        }
        else
        {
            items = query.Cast<TResult>().ToList();
        }
        var totalItemCount = results.Count();

        return new PagedResult<TResult>
        {
            Items = items,
            TotalItemCount = totalItemCount,
            IsFull = isFull
        };
    }

    public static bool MatchTelemetryId(string incomingId, string existingId)
    {
        // This method uses StartsWith to find a match.
        // We only want to use that logic if the traceId is at least the length of a shortened id.
        if (incomingId.Length >= ShortenedIdLength)
        {
            return existingId.StartsWith(incomingId, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return existingId.Equals(incomingId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool TryGetOrAddScope(Dictionary<string, OtlpScope> scopes, InstrumentationScope? scope, OtlpContext context, TelemetryType telemetryType, [NotNullWhen(true)] out OtlpScope? s)
    {
        try
        {
            // The instrumentation scope information for the spans in this message.
            // Semantically when InstrumentationScope isn't set, it is equivalent with
            // an empty instrumentation scope name (unknown).
            var name = scope?.Name ?? string.Empty;
            ref var scopeRef = ref CollectionsMarshal.GetValueRefOrAddDefault(scopes, name, out _);
            // Adds to dictionary if not present.
            if (scopeRef == null)
            {
                scopeRef = (scope != null)
                    ? new OtlpScope(scope.Name, scope.Version, scope.Attributes.ToKeyValuePairs(context))
                    : OtlpScope.Empty;

                context.Logger.LogTrace("Added scope '{ScopeName}' to {TelemetryType}.", scopeRef.Name, telemetryType);
            }

            s = scopeRef;
            return true;
        }
        catch (Exception ex)
        {
            context.Logger.LogInformation(ex, "Error adding scope to {TelemetryType}.", telemetryType);
            s = null;
            return false;
        }
    }

    public static string? GetEventName(OtlpLogEntry logEntry)
    {
        if (!string.IsNullOrEmpty(logEntry.EventName))
        {
            return logEntry.EventName;
        }

        if (GetValue(logEntry.Attributes, "event.name") is { Length: > 0 } eventName)
        {
            return eventName;
        }

        if (GetValue(logEntry.Attributes, "logrecord.event.name") is { Length: > 0 } logRecordEventName)
        {
            return logRecordEventName;
        }

        return null;
    }
}

public enum TelemetryType
{
    Traces,
    Metrics,
    Logs
}
