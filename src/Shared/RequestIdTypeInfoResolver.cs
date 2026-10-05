// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StreamJsonRpc;

namespace Aspire.Shared;

/// <summary>
/// Provides reflection-free JSON metadata for StreamJsonRpc request identifiers.
/// </summary>
// RequestId's internal converter prevents source generation (SYSLIB1220).
// Remove this workaround when https://github.com/microsoft/vs-streamjsonrpc/issues/1407 is fixed.
// Uses the resolver/converter approach from https://github.com/github/copilot-sdk/pull/783.
internal sealed class RequestIdTypeInfoResolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        return type == typeof(RequestId)
            ? JsonMetadataServices.CreateValueInfo<RequestId>(options, new RequestIdJsonConverter())
            : null;
    }

    private sealed class RequestIdJsonConverter : JsonConverter<RequestId>
    {
        public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // IDs are numbers (42), strings ("request-1"), or null. As in the SDK workaround,
            // preserve numbers outside Int64 (9223372036854775808 or 1.5) as strings without rounding.
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.TryGetInt64(out var value)
                    ? new RequestId(value)
                    : new RequestId(reader.HasValueSequence
                        ? Encoding.UTF8.GetString(reader.ValueSequence)
                        : Encoding.UTF8.GetString(reader.ValueSpan)),
                JsonTokenType.String => new RequestId(reader.GetString()),
                JsonTokenType.Null => RequestId.Null,
                _ => throw new JsonException($"Unexpected token type for RequestId: {reader.TokenType}")
            };
        }

        public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
        {
            if (value.Number is long number)
            {
                writer.WriteNumberValue(number);
            }
            else if (value.String is string text)
            {
                writer.WriteStringValue(text);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
