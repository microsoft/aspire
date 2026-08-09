// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Configuration;

/// <summary>
/// A JSON converter for <see cref="bool"/> that accepts both actual boolean values
/// (<c>true</c>/<c>false</c>) and string representations (<c>"true"</c>/<c>"false"</c>).
/// This provides backward compatibility for settings files that may have string values
/// written by older CLI versions.
/// </summary>
internal sealed class FlexibleBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => ParseString(reader.GetString()),
            _ => throw new JsonException($"Unexpected token parsing boolean. Token: {reader.TokenType}")
        };
    }

    private static bool ParseString(string? value)
    {
        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        throw new JsonException($"Invalid boolean value: '{value}'. Expected 'true' or 'false'.");
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}
