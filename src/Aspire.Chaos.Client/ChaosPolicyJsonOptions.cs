// <copyright file="ChaosPolicyJsonOptions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Aspire.Chaos.Client;

/// <summary>
/// JSON serialization options for <see cref="ChaosPolicy"/> shipped via env var.
/// CamelCase to match the runtime <c>POST /chaos/policies</c> JSON binding defaults
/// (ASP.NET Core 8 minimal API uses <see cref="JsonNamingPolicy.CamelCase"/>), so the
/// container can deserialize via its existing <c>InstallPolicyRequest</c> DTO shape.
/// </summary>
public static class ChaosPolicyJsonOptions
{
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new TimeSpanToMillisecondsConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
        },
    };

    /// <summary>
    /// Serializes <see cref="TimeSpan"/> as integer milliseconds, deserializes from
    /// the same. The container's DTOs use raw <c>int</c> for min/max latency (matching
    /// the runtime API contract), so we need to project TimeSpan to ms during transit.
    /// </summary>
    private sealed class TimeSpanToMillisecondsConverter : System.Text.Json.Serialization.JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return TimeSpan.FromMilliseconds(reader.GetInt64());
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((long)value.TotalMilliseconds);
        }
    }
}
