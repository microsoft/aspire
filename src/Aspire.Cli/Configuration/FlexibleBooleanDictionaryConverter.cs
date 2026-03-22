// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Configuration;

/// <summary>
/// A JSON converter that handles Dictionary&lt;string, bool&gt; with flexible boolean parsing.
/// Accepts both actual boolean values (true/false) and string representations ("true"/"false").
/// This provides backward compatibility for settings files that may have string values.
/// </summary>
internal sealed class FlexibleBooleanDictionaryConverter : JsonConverter<Dictionary<string, bool>>
{
    public override Dictionary<string, bool>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject, got {reader.TokenType}");
        }

        var dictionary = new Dictionary<string, bool>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return dictionary;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName, got {reader.TokenType}");
            }

            var key = reader.GetString() ?? throw new JsonException("Property name cannot be null");

            reader.Read();

            bool value = reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => ParseBooleanString(reader.GetString(), key),
                _ => throw new JsonException($"Cannot convert {reader.TokenType} to boolean for key '{key}'")
            };

            dictionary[key] = value;
        }

        throw new JsonException("Unexpected end of JSON");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, bool> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var kvp in value)
        {
            writer.WriteBoolean(kvp.Key, kvp.Value);
        }

        writer.WriteEndObject();
    }

    private static bool ParseBooleanString(string? value, string key)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new JsonException($"Cannot convert string '{value}' to boolean for key '{key}'. Expected 'true' or 'false'.");
    }
}
