// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Represents any type of attribute value following the OTLP protojson format.
/// Only one value property should be set at a time (oneof semantics).
/// </summary>
internal sealed class OtlpAnyValueJson
{
    /// <summary>
    /// String value.
    /// </summary>
    [JsonPropertyName("stringValue")]
    public string? StringValue { get; set; }

    /// <summary>
    /// Boolean value.
    /// </summary>
    [JsonPropertyName("boolValue")]
    public bool? BoolValue { get; set; }

    /// <summary>
    /// Integer value. Serialized as string per protojson spec for int64.
    /// </summary>
    [JsonPropertyName("intValue")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long? IntValue { get; set; }

    /// <summary>
    /// Double value.
    /// </summary>
    [JsonPropertyName("doubleValue")]
    public double? DoubleValue { get; set; }

    /// <summary>
    /// Array value.
    /// </summary>
    [JsonPropertyName("arrayValue")]
    public OtlpArrayValueJson? ArrayValue { get; set; }

    /// <summary>
    /// Key-value list value.
    /// </summary>
    [JsonPropertyName("kvlistValue")]
    public OtlpKeyValueListJson? KvlistValue { get; set; }

    /// <summary>
    /// Bytes value. Serialized as base64 per protojson spec.
    /// </summary>
    [JsonPropertyName("bytesValue")]
    public string? BytesValue { get; set; }
}

/// <summary>
/// Represents an array of AnyValue messages.
/// </summary>
internal sealed class OtlpArrayValueJson
{
    /// <summary>
    /// Array of values.
    /// </summary>
    [JsonPropertyName("values")]
    public OtlpAnyValueJson[]? Values { get; set; }
}

/// <summary>
/// Represents a list of KeyValue messages.
/// </summary>
internal sealed class OtlpKeyValueListJson
{
    /// <summary>
    /// Collection of key/value pairs.
    /// </summary>
    [JsonPropertyName("values")]
    public OtlpKeyValueJson[]? Values { get; set; }
}

/// <summary>
/// Represents a key-value pair used to store attributes.
/// </summary>
internal sealed class OtlpKeyValueJson
{
    /// <summary>
    /// The key name of the pair.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>
    /// The value of the pair.
    /// </summary>
    [JsonPropertyName("value")]
    public OtlpAnyValueJson? Value { get; set; }
}

/// <summary>
/// Represents the combined telemetry data in OTLP JSON format.
/// This type can contain logs, traces, and/or metrics data.
/// </summary>
internal sealed class OtlpTelemetryDataJson
{
    /// <summary>
    /// An array of ResourceSpans.
    /// </summary>
    [JsonPropertyName("resourceSpans")]
    public OtlpResourceSpansJson[]? ResourceSpans { get; set; }

    /// <summary>
    /// An array of ResourceLogs.
    /// </summary>
    [JsonPropertyName("resourceLogs")]
    public OtlpResourceLogsJson[]? ResourceLogs { get; set; }

    /// <summary>
    /// An array of ResourceMetrics.
    /// </summary>
    [JsonPropertyName("resourceMetrics")]
    public OtlpResourceMetricsJson[]? ResourceMetrics { get; set; }
}

/// <summary>
/// Represents instrumentation scope information.
/// </summary>
internal sealed class OtlpInstrumentationScopeJson
{
    /// <summary>
    /// A name denoting the instrumentation scope.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// The version of the instrumentation scope.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Additional attributes that describe the scope.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// The number of attributes that were discarded.
    /// </summary>
    [JsonPropertyName("droppedAttributesCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedAttributesCount { get; set; }
}

/// <summary>
/// Represents a reference to an entity.
/// </summary>
internal sealed class OtlpEntityRefJson
{
    /// <summary>
    /// The Schema URL, if known.
    /// </summary>
    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }

    /// <summary>
    /// Defines the type of the entity.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Attribute keys that identify the entity.
    /// </summary>
    [JsonPropertyName("idKeys")]
    public string[]? IdKeys { get; set; }

    /// <summary>
    /// Descriptive (non-identifying) attribute keys of the entity.
    /// </summary>
    [JsonPropertyName("descriptionKeys")]
    public string[]? DescriptionKeys { get; set; }
}
