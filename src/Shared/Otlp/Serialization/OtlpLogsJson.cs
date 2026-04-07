// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Represents a collection of ScopeLogs from a Resource.
/// </summary>
internal sealed class OtlpResourceLogsJson
{
    /// <summary>
    /// A list of ScopeLogs that originate from a resource.
    /// </summary>
    [JsonPropertyName("scopeLogs")]
    public OtlpScopeLogsJson[]? ScopeLogs { get; set; }

    /// <summary>
    /// The resource for the logs in this message.
    /// </summary>
    [JsonPropertyName("resource")]
    public OtlpResourceJson? Resource { get; set; }

    /// <summary>
    /// The Schema URL, if known.
    /// </summary>
    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }
}

/// <summary>
/// Represents a collection of Logs produced by a Scope.
/// </summary>
internal sealed class OtlpScopeLogsJson
{
    /// <summary>
    /// The instrumentation scope information for the logs in this message.
    /// </summary>
    [JsonPropertyName("scope")]
    public OtlpInstrumentationScopeJson? Scope { get; set; }

    /// <summary>
    /// A list of log records.
    /// </summary>
    [JsonPropertyName("logRecords")]
    public OtlpLogRecordJson[]? LogRecords { get; set; }

    /// <summary>
    /// The Schema URL, if known.
    /// </summary>
    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }
}

/// <summary>
/// Represents a log record according to OpenTelemetry Log Data Model.
/// </summary>
internal sealed class OtlpLogRecordJson
{
    /// <summary>
    /// Time when the event occurred. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// Time when the event was observed. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("observedTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? ObservedTimeUnixNano { get; set; }

    /// <summary>
    /// Numerical value of the severity. Serialized as integer per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("severityNumber")]
    public int? SeverityNumber { get; set; }

    /// <summary>
    /// The severity text (also known as log level).
    /// </summary>
    [JsonPropertyName("severityText")]
    public string? SeverityText { get; set; }

    /// <summary>
    /// A value containing the body of the log record.
    /// </summary>
    [JsonPropertyName("body")]
    public OtlpAnyValueJson? Body { get; set; }

    /// <summary>
    /// Additional attributes that describe the specific event occurrence.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// The number of dropped attributes.
    /// </summary>
    [JsonPropertyName("droppedAttributesCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedAttributesCount { get; set; }

    /// <summary>
    /// Flags, a bit field (fixed32 per protobuf).
    /// </summary>
    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Flags { get; set; }

    /// <summary>
    /// A unique identifier for a trace. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    /// <summary>
    /// A unique identifier for a span within a trace. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    /// <summary>
    /// A unique identifier of event category/type.
    /// </summary>
    [JsonPropertyName("eventName")]
    public string? EventName { get; set; }
}

/// <summary>
/// Represents the export logs service request.
/// </summary>
internal sealed class OtlpExportLogsServiceRequestJson
{
    /// <summary>
    /// An array of ResourceLogs.
    /// </summary>
    [JsonPropertyName("resourceLogs")]
    public OtlpResourceLogsJson[]? ResourceLogs { get; set; }
}
