// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Represents a collection of ScopeSpans from a Resource.
/// </summary>
internal sealed class OtlpResourceSpansJson
{
    /// <summary>
    /// A list of ScopeSpans that originate from a resource.
    /// </summary>
    [JsonPropertyName("scopeSpans")]
    public OtlpScopeSpansJson[]? ScopeSpans { get; set; }

    /// <summary>
    /// The resource for the spans in this message.
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
/// Represents a collection of Spans produced by an InstrumentationScope.
/// </summary>
internal sealed class OtlpScopeSpansJson
{
    /// <summary>
    /// The instrumentation scope information for the spans in this message.
    /// </summary>
    [JsonPropertyName("scope")]
    public OtlpInstrumentationScopeJson? Scope { get; set; }

    /// <summary>
    /// A list of Spans that originate from an instrumentation scope.
    /// </summary>
    [JsonPropertyName("spans")]
    public OtlpSpanJson[]? Spans { get; set; }

    /// <summary>
    /// The Schema URL, if known.
    /// </summary>
    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }
}

/// <summary>
/// Represents a single operation performed by a single component of the system.
/// </summary>
internal sealed class OtlpSpanJson
{
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
    /// Trace state in w3c-trace-context format.
    /// </summary>
    [JsonPropertyName("traceState")]
    public string? TraceState { get; set; }

    /// <summary>
    /// The span_id of this span's parent span. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("parentSpanId")]
    public string? ParentSpanId { get; set; }

    /// <summary>
    /// Flags, a bit field (fixed32 per protobuf).
    /// </summary>
    [JsonPropertyName("flags")]
    public uint? Flags { get; set; }

    /// <summary>
    /// A description of the span's operation.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Distinguishes between spans generated in a particular context.
    /// Serialized as integer per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("kind")]
    public int? Kind { get; set; }

    /// <summary>
    /// The start time of the span. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("startTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? StartTimeUnixNano { get; set; }

    /// <summary>
    /// The end time of the span. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("endTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? EndTimeUnixNano { get; set; }

    /// <summary>
    /// A collection of key/value pairs.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// The number of attributes that were discarded.
    /// </summary>
    [JsonPropertyName("droppedAttributesCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedAttributesCount { get; set; }

    /// <summary>
    /// A collection of Event items.
    /// </summary>
    [JsonPropertyName("events")]
    public OtlpSpanEventJson[]? Events { get; set; }

    /// <summary>
    /// The number of dropped events.
    /// </summary>
    [JsonPropertyName("droppedEventsCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedEventsCount { get; set; }

    /// <summary>
    /// A collection of Links.
    /// </summary>
    [JsonPropertyName("links")]
    public OtlpSpanLinkJson[]? Links { get; set; }

    /// <summary>
    /// The number of dropped links.
    /// </summary>
    [JsonPropertyName("droppedLinksCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedLinksCount { get; set; }

    /// <summary>
    /// An optional final status for this span.
    /// </summary>
    [JsonPropertyName("status")]
    public OtlpSpanStatusJson? Status { get; set; }
}

/// <summary>
/// Represents a time-stamped annotation of the span.
/// </summary>
internal sealed class OtlpSpanEventJson
{
    /// <summary>
    /// The time the event occurred. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// The name of the event.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// A collection of attribute key/value pairs on the event.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// The number of dropped attributes.
    /// </summary>
    [JsonPropertyName("droppedAttributesCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedAttributesCount { get; set; }
}

/// <summary>
/// Represents a pointer from the current span to another span.
/// </summary>
internal sealed class OtlpSpanLinkJson
{
    /// <summary>
    /// A unique identifier of a trace. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }

    /// <summary>
    /// A unique identifier for the linked span. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    /// <summary>
    /// The trace_state associated with the link.
    /// </summary>
    [JsonPropertyName("traceState")]
    public string? TraceState { get; set; }

    /// <summary>
    /// A collection of attribute key/value pairs on the link.
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
    public uint? Flags { get; set; }
}

/// <summary>
/// Represents the status of a span.
/// </summary>
internal sealed class OtlpSpanStatusJson
{
    /// <summary>
    /// A developer-facing human readable error message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// The status code. Serialized as integer per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("code")]
    public int? Code { get; set; }
}

/// <summary>
/// Represents the export trace service request.
/// </summary>
internal sealed class OtlpExportTraceServiceRequestJson
{
    /// <summary>
    /// An array of ResourceSpans.
    /// </summary>
    [JsonPropertyName("resourceSpans")]
    public OtlpResourceSpansJson[]? ResourceSpans { get; set; }
}
