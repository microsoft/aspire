// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serializes durations as ATS millisecond numbers while accepting standard TimeSpan strings.
/// </summary>
internal sealed class TimeSpanMillisecondsJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // ATS durations use numeric milliseconds (1500); also accept CLR duration strings
        // ("00:00:01.5000000") for compatibility with existing serialized values.
        if (reader.TokenType == JsonTokenType.Number)
        {
            var milliseconds = reader.GetDouble();
            if (!double.IsFinite(milliseconds))
            {
                throw new JsonException("TimeSpan milliseconds must be a finite number.");
            }

            try
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }
            catch (OverflowException ex)
            {
                throw new JsonException("TimeSpan milliseconds are outside the supported range.", ex);
            }
        }

        if (reader.TokenType == JsonTokenType.String &&
            TimeSpan.TryParse(reader.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new JsonException("TimeSpan values must be milliseconds or a standard TimeSpan string.");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.TotalMilliseconds);
    }
}
