// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

internal sealed class KubernetesMicroTimeJsonConverter : JsonConverter<DateTime?>
{
    private const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";
    private const string OffsetFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffffzzz";

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"Expected a string token for Kubernetes MicroTime but found {reader.TokenType}.");
        }

        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            throw new JsonException("Expected a non-empty Kubernetes MicroTime value.");
        }

        return value.EndsWith('Z')
            ? DateTime.ParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            : DateTimeOffset.ParseExact(value, OffsetFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // DCP models these fields as Kubernetes metav1.MicroTime, whose JSON shape is fixed-width:
        //   "2026-07-15T18:46:06.123000Z"
        // See https://github.com/kubernetes/apimachinery/blob/v0.36.0/pkg/apis/meta/v1/micro_time.go.
        // System.Text.Json trims fractional seconds for DateTime by default, which DCP rejects when
        // the value is submitted to the API server.
        var timestamp = value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };

        writer.WriteStringValue(timestamp.ToString(UtcFormat, CultureInfo.InvariantCulture));
    }
}
