// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Represents a collection of ScopeMetrics from a Resource.
/// </summary>
internal sealed class OtlpResourceMetricsJson
{
    /// <summary>
    /// A list of metrics that originate from a resource.
    /// </summary>
    [JsonPropertyName("scopeMetrics")]
    public OtlpScopeMetricsJson[]? ScopeMetrics { get; set; }

    /// <summary>
    /// The resource for the metrics in this message.
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
/// Represents a collection of Metrics produced by a Scope.
/// </summary>
internal sealed class OtlpScopeMetricsJson
{
    /// <summary>
    /// The instrumentation scope information for the metrics in this message.
    /// </summary>
    [JsonPropertyName("scope")]
    public OtlpInstrumentationScopeJson? Scope { get; set; }

    /// <summary>
    /// A list of metrics that originate from an instrumentation library.
    /// </summary>
    [JsonPropertyName("metrics")]
    public OtlpMetricJson[]? Metrics { get; set; }

    /// <summary>
    /// The Schema URL, if known.
    /// </summary>
    [JsonPropertyName("schemaUrl")]
    public string? SchemaUrl { get; set; }
}

/// <summary>
/// Represents a metric which has one or more timeseries.
/// </summary>
internal sealed class OtlpMetricJson
{
    /// <summary>
    /// The name of the metric.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// A description of the metric.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// The unit in which the metric value is reported.
    /// </summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>
    /// Gauge data.
    /// </summary>
    [JsonPropertyName("gauge")]
    public OtlpGaugeJson? Gauge { get; set; }

    /// <summary>
    /// Sum data.
    /// </summary>
    [JsonPropertyName("sum")]
    public OtlpSumJson? Sum { get; set; }

    /// <summary>
    /// Histogram data.
    /// </summary>
    [JsonPropertyName("histogram")]
    public OtlpHistogramJson? Histogram { get; set; }

    /// <summary>
    /// Exponential histogram data.
    /// </summary>
    [JsonPropertyName("exponentialHistogram")]
    public OtlpExponentialHistogramJson? ExponentialHistogram { get; set; }

    /// <summary>
    /// Summary data.
    /// </summary>
    [JsonPropertyName("summary")]
    public OtlpSummaryJson? Summary { get; set; }

    /// <summary>
    /// Additional metadata attributes that describe the metric.
    /// </summary>
    [JsonPropertyName("metadata")]
    public OtlpKeyValueJson[]? Metadata { get; set; }
}

/// <summary>
/// Represents Gauge metric type.
/// </summary>
internal sealed class OtlpGaugeJson
{
    /// <summary>
    /// The time series data points.
    /// </summary>
    [JsonPropertyName("dataPoints")]
    public OtlpNumberDataPointJson[]? DataPoints { get; set; }
}

/// <summary>
/// Represents Sum metric type.
/// </summary>
internal sealed class OtlpSumJson
{
    /// <summary>
    /// The time series data points.
    /// </summary>
    [JsonPropertyName("dataPoints")]
    public OtlpNumberDataPointJson[]? DataPoints { get; set; }

    /// <summary>
    /// Aggregation temporality. Serialized as integer per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("aggregationTemporality")]
    public int? AggregationTemporality { get; set; }

    /// <summary>
    /// Represents whether the sum is monotonic.
    /// </summary>
    [JsonPropertyName("isMonotonic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMonotonic { get; set; }
}

/// <summary>
/// Represents Histogram metric type.
/// </summary>
internal sealed class OtlpHistogramJson
{
    /// <summary>
    /// The time series data points.
    /// </summary>
    [JsonPropertyName("dataPoints")]
    public OtlpHistogramDataPointJson[]? DataPoints { get; set; }

    /// <summary>
    /// Aggregation temporality. Serialized as integer per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("aggregationTemporality")]
    public int? AggregationTemporality { get; set; }
}

/// <summary>
/// Represents ExponentialHistogram metric type.
/// </summary>
internal sealed class OtlpExponentialHistogramJson
{
    /// <summary>
    /// The time series data points.
    /// </summary>
    [JsonPropertyName("dataPoints")]
    public OtlpExponentialHistogramDataPointJson[]? DataPoints { get; set; }

    /// <summary>
    /// Aggregation temporality. Serialized as integer per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("aggregationTemporality")]
    public int? AggregationTemporality { get; set; }
}

/// <summary>
/// Represents Summary metric type.
/// </summary>
internal sealed class OtlpSummaryJson
{
    /// <summary>
    /// The time series data points.
    /// </summary>
    [JsonPropertyName("dataPoints")]
    public OtlpSummaryDataPointJson[]? DataPoints { get; set; }
}

/// <summary>
/// Represents a single data point in a timeseries with scalar value.
/// </summary>
internal sealed class OtlpNumberDataPointJson
{
    /// <summary>
    /// The set of key/value pairs that uniquely identify the timeseries.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// Start time of the aggregation. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("startTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? StartTimeUnixNano { get; set; }

    /// <summary>
    /// Time when the data point was recorded. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// Double value.
    /// </summary>
    [JsonPropertyName("asDouble")]
    public double? AsDouble { get; set; }

    /// <summary>
    /// Integer value. Serialized as string per protojson spec for sfixed64.
    /// </summary>
    [JsonPropertyName("asInt")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long? AsInt { get; set; }

    /// <summary>
    /// List of exemplars collected from measurements.
    /// </summary>
    [JsonPropertyName("exemplars")]
    public OtlpExemplarJson[]? Exemplars { get; set; }

    /// <summary>
    /// Flags that apply to this specific data point.
    /// </summary>
    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Flags { get; set; }
}

/// <summary>
/// Represents a histogram data point.
/// </summary>
internal sealed class OtlpHistogramDataPointJson
{
    /// <summary>
    /// The set of key/value pairs that uniquely identify the timeseries.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// Start time of the aggregation. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("startTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? StartTimeUnixNano { get; set; }

    /// <summary>
    /// Time when the data point was recorded. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// Count is the number of values in the population. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("count")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? Count { get; set; }

    /// <summary>
    /// Sum of the values in the population.
    /// </summary>
    [JsonPropertyName("sum")]
    public double? Sum { get; set; }

    /// <summary>
    /// Bucket counts for each bucket. Serialized as strings per protojson spec for uint64.
    /// </summary>
    [JsonPropertyName("bucketCounts")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong[]? BucketCounts { get; set; }

    /// <summary>
    /// Explicit bucket boundaries.
    /// </summary>
    [JsonPropertyName("explicitBounds")]
    public double[]? ExplicitBounds { get; set; }

    /// <summary>
    /// List of exemplars collected from measurements.
    /// </summary>
    [JsonPropertyName("exemplars")]
    public OtlpExemplarJson[]? Exemplars { get; set; }

    /// <summary>
    /// Flags that apply to this specific data point.
    /// </summary>
    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Flags { get; set; }

    /// <summary>
    /// Minimum value over (start_time, end_time].
    /// </summary>
    [JsonPropertyName("min")]
    public double? Min { get; set; }

    /// <summary>
    /// Maximum value over (start_time, end_time].
    /// </summary>
    [JsonPropertyName("max")]
    public double? Max { get; set; }
}

/// <summary>
/// Represents an exponential histogram data point.
/// </summary>
internal sealed class OtlpExponentialHistogramDataPointJson
{
    /// <summary>
    /// The set of key/value pairs that uniquely identify the timeseries.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// Start time of the aggregation. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("startTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? StartTimeUnixNano { get; set; }

    /// <summary>
    /// Time when the data point was recorded. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// The number of values in the population. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("count")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? Count { get; set; }

    /// <summary>
    /// Sum of the values in the population.
    /// </summary>
    [JsonPropertyName("sum")]
    public double? Sum { get; set; }

    /// <summary>
    /// Scale describes the resolution of the histogram.
    /// </summary>
    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Scale { get; set; }

    /// <summary>
    /// The count of values that are either exactly zero or within the zero region. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("zeroCount")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? ZeroCount { get; set; }

    /// <summary>
    /// Positive carries the positive range of exponential bucket counts.
    /// </summary>
    [JsonPropertyName("positive")]
    public OtlpExponentialHistogramBucketsJson? Positive { get; set; }

    /// <summary>
    /// Negative carries the negative range of exponential bucket counts.
    /// </summary>
    [JsonPropertyName("negative")]
    public OtlpExponentialHistogramBucketsJson? Negative { get; set; }

    /// <summary>
    /// Flags that apply to this specific data point.
    /// </summary>
    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Flags { get; set; }

    /// <summary>
    /// List of exemplars collected from measurements.
    /// </summary>
    [JsonPropertyName("exemplars")]
    public OtlpExemplarJson[]? Exemplars { get; set; }

    /// <summary>
    /// Minimum value over (start_time, end_time].
    /// </summary>
    [JsonPropertyName("min")]
    public double? Min { get; set; }

    /// <summary>
    /// Maximum value over (start_time, end_time].
    /// </summary>
    [JsonPropertyName("max")]
    public double? Max { get; set; }

    /// <summary>
    /// Zero threshold conveys the width of the zero region.
    /// </summary>
    [JsonPropertyName("zeroThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ZeroThreshold { get; set; }
}

/// <summary>
/// Represents exponential histogram buckets.
/// </summary>
internal sealed class OtlpExponentialHistogramBucketsJson
{
    /// <summary>
    /// The bucket index of the first entry in the bucket_counts array.
    /// </summary>
    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Offset { get; set; }

    /// <summary>
    /// An array of count values. Serialized as strings per protojson spec for uint64.
    /// </summary>
    [JsonPropertyName("bucketCounts")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong[]? BucketCounts { get; set; }
}

/// <summary>
/// Represents a summary data point.
/// </summary>
internal sealed class OtlpSummaryDataPointJson
{
    /// <summary>
    /// The set of key/value pairs that uniquely identify the timeseries.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// Start time of the aggregation. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("startTimeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? StartTimeUnixNano { get; set; }

    /// <summary>
    /// Time when the data point was recorded. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// Count is the number of values in the population. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("count")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? Count { get; set; }

    /// <summary>
    /// Sum of the values in the population.
    /// </summary>
    [JsonPropertyName("sum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Sum { get; set; }

    /// <summary>
    /// List of values at different quantiles.
    /// </summary>
    [JsonPropertyName("quantileValues")]
    public OtlpValueAtQuantileJson[]? QuantileValues { get; set; }

    /// <summary>
    /// Flags that apply to this specific data point.
    /// </summary>
    [JsonPropertyName("flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Flags { get; set; }
}

/// <summary>
/// Represents the value at a given quantile of a distribution.
/// </summary>
internal sealed class OtlpValueAtQuantileJson
{
    /// <summary>
    /// The quantile of a distribution.
    /// </summary>
    [JsonPropertyName("quantile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Quantile { get; set; }

    /// <summary>
    /// The value at the given quantile of a distribution.
    /// </summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Value { get; set; }
}

/// <summary>
/// Represents an exemplar, which is a sample input measurement.
/// </summary>
internal sealed class OtlpExemplarJson
{
    /// <summary>
    /// The set of key/value pairs that were filtered out by the aggregator.
    /// </summary>
    [JsonPropertyName("filteredAttributes")]
    public OtlpKeyValueJson[]? FilteredAttributes { get; set; }

    /// <summary>
    /// Time when this exemplar was recorded. Serialized as string per protojson spec for fixed64.
    /// </summary>
    [JsonPropertyName("timeUnixNano")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public ulong? TimeUnixNano { get; set; }

    /// <summary>
    /// Double value.
    /// </summary>
    [JsonPropertyName("asDouble")]
    public double? AsDouble { get; set; }

    /// <summary>
    /// Integer value. Serialized as string per protojson spec for sfixed64.
    /// </summary>
    [JsonPropertyName("asInt")]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long? AsInt { get; set; }

    /// <summary>
    /// Span ID of the exemplar trace. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("spanId")]
    public string? SpanId { get; set; }

    /// <summary>
    /// Trace ID of the exemplar trace. Serialized as lowercase hex per OTLP/JSON spec.
    /// </summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; set; }
}
