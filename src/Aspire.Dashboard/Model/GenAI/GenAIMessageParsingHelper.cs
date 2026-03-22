// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Dashboard.Model.GenAI;

/// <summary>
/// Provides truncation-tolerant JSON array deserialization for GenAI message parsing.
/// </summary>
internal static class GenAIMessageParsingHelper
{
    internal delegate T? ReadElement<T>(ref Utf8JsonReader reader);

    internal static (List<T> items, bool truncated) DeserializeArrayIncrementally<T>(string json, ReadElement<T> readElement)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var readerOptions = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var reader = new Utf8JsonReader(bytes, readerOptions);
        return DeserializeArrayIncrementally(ref reader, readElement);
    }

    internal static (List<T> items, bool truncated) DeserializeArrayIncrementally<T>(ref Utf8JsonReader reader, ReadElement<T> readElement)
    {
        var items = new List<T>();

        // Read start of array. Let exceptions propagate for truly invalid JSON.
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a JSON array.");
        }

        while (true)
        {
            bool readSuccess;
            try
            {
                readSuccess = reader.Read();
            }
            catch (JsonException)
            {
                return (items, true);
            }

            if (!readSuccess)
            {
                return (items, true);
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            try
            {
                var item = readElement(ref reader);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (JsonException)
            {
                return (items, true);
            }
            catch (InvalidOperationException)
            {
                return (items, true);
            }
        }

        return (items, false);
    }

    internal static MessagePart? ReadMessagePart(ref Utf8JsonReader reader)
    {
        return JsonSerializer.Deserialize(ref reader, GenAIMessagesContext.Default.MessagePart);
    }

    internal static (string role, List<MessagePart> parts, bool partsTruncated) ReadChatMessage(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of chat message object.");
        }

        string? role = null;
        List<MessagePart>? parts = null;
        var partsTruncated = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name.");
            }

            var propertyName = reader.GetString();

            switch (propertyName)
            {
                case "role":
                    if (!reader.Read())
                    {
                        throw new JsonException("Unexpected end of JSON while reading role value.");
                    }
                    role = reader.GetString();
                    break;
                case "parts":
                    // DeserializeArrayIncrementally reads the StartArray token itself.
                    (parts, partsTruncated) = DeserializeArrayIncrementally<MessagePart>(ref reader, ReadMessagePart);
                    break;
                default:
                    if (!reader.Read())
                    {
                        throw new JsonException("Unexpected end of JSON while reading property value.");
                    }

                    if (!reader.TrySkip())
                    {
                        throw new JsonException("Unexpected end of JSON while skipping property value.");
                    }
                    break;
            }
        }

        return (role ?? string.Empty, parts ?? [], partsTruncated);
    }

    /// <summary>
    /// If the node is a JSON string that parses to a JSON object or array, return the parsed node instead.
    /// </summary>
    internal static JsonNode? TryParseStringJsonNode(JsonNode? node)
    {
        if (node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { } json)
        {
            try
            {
                var parsed = JsonNode.Parse(json);
                if (parsed?.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Not valid JSON. Keep the original string value.
            }
        }

        return node;
    }
}
