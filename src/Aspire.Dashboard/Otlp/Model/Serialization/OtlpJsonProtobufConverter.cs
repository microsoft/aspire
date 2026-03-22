// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Otlp.Serialization;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Model.Serialization;

/// <summary>
/// Converts OTLP JSON types to protobuf types.
/// </summary>
internal static class OtlpJsonToProtobufConverter
{
    /// <summary>
    /// Converts an export trace service request from JSON to protobuf.
    /// </summary>
    public static ExportTraceServiceRequest ToProtobuf(OtlpExportTraceServiceRequestJson json)
    {
        var request = new ExportTraceServiceRequest();
        if (json.ResourceSpans is not null)
        {
            foreach (var rs in json.ResourceSpans)
            {
                request.ResourceSpans.Add(ToProtobuf(rs));
            }
        }
        return request;
    }

    /// <summary>
    /// Converts an export logs service request from JSON to protobuf.
    /// </summary>
    public static ExportLogsServiceRequest ToProtobuf(OtlpExportLogsServiceRequestJson json)
    {
        var request = new ExportLogsServiceRequest();
        if (json.ResourceLogs is not null)
        {
            foreach (var rl in json.ResourceLogs)
            {
                request.ResourceLogs.Add(ToProtobuf(rl));
            }
        }
        return request;
    }

    /// <summary>
    /// Converts an export metrics service request from JSON to protobuf.
    /// </summary>
    public static ExportMetricsServiceRequest ToProtobuf(OtlpExportMetricsServiceRequestJson json)
    {
        var request = new ExportMetricsServiceRequest();
        if (json.ResourceMetrics is not null)
        {
            foreach (var rm in json.ResourceMetrics)
            {
                request.ResourceMetrics.Add(ToProtobuf(rm));
            }
        }
        return request;
    }

    private static ResourceSpans ToProtobuf(OtlpResourceSpansJson json)
    {
        var resourceSpans = new ResourceSpans();
        if (json.Resource is not null)
        {
            resourceSpans.Resource = ToProtobuf(json.Resource);
        }
        if (json.ScopeSpans is not null)
        {
            foreach (var ss in json.ScopeSpans)
            {
                resourceSpans.ScopeSpans.Add(ToProtobuf(ss));
            }
        }
        if (json.SchemaUrl is not null)
        {
            resourceSpans.SchemaUrl = json.SchemaUrl;
        }
        return resourceSpans;
    }

    private static ScopeSpans ToProtobuf(OtlpScopeSpansJson json)
    {
        var scopeSpans = new ScopeSpans();
        if (json.Scope is not null)
        {
            scopeSpans.Scope = ToProtobuf(json.Scope);
        }
        if (json.Spans is not null)
        {
            foreach (var s in json.Spans)
            {
                scopeSpans.Spans.Add(ToProtobuf(s));
            }
        }
        if (json.SchemaUrl is not null)
        {
            scopeSpans.SchemaUrl = json.SchemaUrl;
        }
        return scopeSpans;
    }

    private static Span ToProtobuf(OtlpSpanJson json)
    {
        var span = new Span();
        if (json.TraceId is not null)
        {
            span.TraceId = HexToByteString(json.TraceId);
        }
        if (json.SpanId is not null)
        {
            span.SpanId = HexToByteString(json.SpanId);
        }
        if (json.TraceState is not null)
        {
            span.TraceState = json.TraceState;
        }
        if (json.ParentSpanId is not null)
        {
            span.ParentSpanId = HexToByteString(json.ParentSpanId);
        }
        if (json.Flags.HasValue)
        {
            span.Flags = json.Flags.Value;
        }
        if (json.Name is not null)
        {
            span.Name = json.Name;
        }
        if (json.Kind.HasValue)
        {
            span.Kind = (Span.Types.SpanKind)json.Kind.Value;
        }
        if (json.StartTimeUnixNano.HasValue)
        {
            span.StartTimeUnixNano = json.StartTimeUnixNano.Value;
        }
        if (json.EndTimeUnixNano.HasValue)
        {
            span.EndTimeUnixNano = json.EndTimeUnixNano.Value;
        }
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                span.Attributes.Add(ToProtobuf(attr));
            }
        }
        span.DroppedAttributesCount = json.DroppedAttributesCount;
        if (json.Events is not null)
        {
            foreach (var evt in json.Events)
            {
                span.Events.Add(ToProtobuf(evt));
            }
        }
        span.DroppedEventsCount = json.DroppedEventsCount;
        if (json.Links is not null)
        {
            foreach (var link in json.Links)
            {
                span.Links.Add(ToProtobuf(link));
            }
        }
        span.DroppedLinksCount = json.DroppedLinksCount;
        if (json.Status is not null)
        {
            span.Status = ToProtobuf(json.Status);
        }
        return span;
    }

    private static Span.Types.Event ToProtobuf(OtlpSpanEventJson json)
    {
        var evt = new Span.Types.Event();
        if (json.TimeUnixNano.HasValue)
        {
            evt.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.Name is not null)
        {
            evt.Name = json.Name;
        }
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                evt.Attributes.Add(ToProtobuf(attr));
            }
        }
        evt.DroppedAttributesCount = json.DroppedAttributesCount;
        return evt;
    }

    private static Span.Types.Link ToProtobuf(OtlpSpanLinkJson json)
    {
        var link = new Span.Types.Link();
        if (json.TraceId is not null)
        {
            link.TraceId = HexToByteString(json.TraceId);
        }
        if (json.SpanId is not null)
        {
            link.SpanId = HexToByteString(json.SpanId);
        }
        if (json.TraceState is not null)
        {
            link.TraceState = json.TraceState;
        }
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                link.Attributes.Add(ToProtobuf(attr));
            }
        }
        link.DroppedAttributesCount = json.DroppedAttributesCount;
        if (json.Flags.HasValue)
        {
            link.Flags = json.Flags.Value;
        }
        return link;
    }

    private static Status ToProtobuf(OtlpSpanStatusJson json)
    {
        var status = new Status();
        if (json.Message is not null)
        {
            status.Message = json.Message;
        }
        if (json.Code.HasValue)
        {
            status.Code = (Status.Types.StatusCode)json.Code.Value;
        }
        return status;
    }

    private static ResourceLogs ToProtobuf(OtlpResourceLogsJson json)
    {
        var resourceLogs = new ResourceLogs();
        if (json.Resource is not null)
        {
            resourceLogs.Resource = ToProtobuf(json.Resource);
        }
        if (json.ScopeLogs is not null)
        {
            foreach (var sl in json.ScopeLogs)
            {
                resourceLogs.ScopeLogs.Add(ToProtobuf(sl));
            }
        }
        if (json.SchemaUrl is not null)
        {
            resourceLogs.SchemaUrl = json.SchemaUrl;
        }
        return resourceLogs;
    }

    private static ScopeLogs ToProtobuf(OtlpScopeLogsJson json)
    {
        var scopeLogs = new ScopeLogs();
        if (json.Scope is not null)
        {
            scopeLogs.Scope = ToProtobuf(json.Scope);
        }
        if (json.LogRecords is not null)
        {
            foreach (var lr in json.LogRecords)
            {
                scopeLogs.LogRecords.Add(ToProtobuf(lr));
            }
        }
        if (json.SchemaUrl is not null)
        {
            scopeLogs.SchemaUrl = json.SchemaUrl;
        }
        return scopeLogs;
    }

    private static LogRecord ToProtobuf(OtlpLogRecordJson json)
    {
        var logRecord = new LogRecord();
        if (json.TimeUnixNano.HasValue)
        {
            logRecord.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.ObservedTimeUnixNano.HasValue)
        {
            logRecord.ObservedTimeUnixNano = json.ObservedTimeUnixNano.Value;
        }
        if (json.SeverityNumber.HasValue)
        {
            logRecord.SeverityNumber = (SeverityNumber)json.SeverityNumber.Value;
        }
        if (json.SeverityText is not null)
        {
            logRecord.SeverityText = json.SeverityText;
        }
        if (json.Body is not null)
        {
            logRecord.Body = ToProtobuf(json.Body);
        }
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                logRecord.Attributes.Add(ToProtobuf(attr));
            }
        }
        logRecord.DroppedAttributesCount = json.DroppedAttributesCount;
        logRecord.Flags = json.Flags;
        if (json.TraceId is not null)
        {
            logRecord.TraceId = HexToByteString(json.TraceId);
        }
        if (json.SpanId is not null)
        {
            logRecord.SpanId = HexToByteString(json.SpanId);
        }
        if (json.EventName is not null)
        {
            logRecord.EventName = json.EventName;
        }
        return logRecord;
    }

    private static ResourceMetrics ToProtobuf(OtlpResourceMetricsJson json)
    {
        var resourceMetrics = new ResourceMetrics();
        if (json.Resource is not null)
        {
            resourceMetrics.Resource = ToProtobuf(json.Resource);
        }
        if (json.ScopeMetrics is not null)
        {
            foreach (var sm in json.ScopeMetrics)
            {
                resourceMetrics.ScopeMetrics.Add(ToProtobuf(sm));
            }
        }
        if (json.SchemaUrl is not null)
        {
            resourceMetrics.SchemaUrl = json.SchemaUrl;
        }
        return resourceMetrics;
    }

    private static ScopeMetrics ToProtobuf(OtlpScopeMetricsJson json)
    {
        var scopeMetrics = new ScopeMetrics();
        if (json.Scope is not null)
        {
            scopeMetrics.Scope = ToProtobuf(json.Scope);
        }
        if (json.Metrics is not null)
        {
            foreach (var m in json.Metrics)
            {
                scopeMetrics.Metrics.Add(ToProtobuf(m));
            }
        }
        if (json.SchemaUrl is not null)
        {
            scopeMetrics.SchemaUrl = json.SchemaUrl;
        }
        return scopeMetrics;
    }

    private static Metric ToProtobuf(OtlpMetricJson json)
    {
        var metric = new Metric();
        if (json.Name is not null)
        {
            metric.Name = json.Name;
        }
        if (json.Description is not null)
        {
            metric.Description = json.Description;
        }
        if (json.Unit is not null)
        {
            metric.Unit = json.Unit;
        }
        if (json.Gauge is not null)
        {
            metric.Gauge = ToProtobuf(json.Gauge);
        }
        else if (json.Sum is not null)
        {
            metric.Sum = ToProtobuf(json.Sum);
        }
        else if (json.Histogram is not null)
        {
            metric.Histogram = ToProtobuf(json.Histogram);
        }
        else if (json.ExponentialHistogram is not null)
        {
            metric.ExponentialHistogram = ToProtobuf(json.ExponentialHistogram);
        }
        else if (json.Summary is not null)
        {
            metric.Summary = ToProtobuf(json.Summary);
        }
        if (json.Metadata is not null)
        {
            foreach (var kv in json.Metadata)
            {
                metric.Metadata.Add(ToProtobuf(kv));
            }
        }
        return metric;
    }

    private static Gauge ToProtobuf(OtlpGaugeJson json)
    {
        var gauge = new Gauge();
        if (json.DataPoints is not null)
        {
            foreach (var dp in json.DataPoints)
            {
                gauge.DataPoints.Add(ToProtobuf(dp));
            }
        }
        return gauge;
    }

    private static Sum ToProtobuf(OtlpSumJson json)
    {
        var sum = new Sum();
        if (json.DataPoints is not null)
        {
            foreach (var dp in json.DataPoints)
            {
                sum.DataPoints.Add(ToProtobuf(dp));
            }
        }
        if (json.AggregationTemporality.HasValue)
        {
            sum.AggregationTemporality = (AggregationTemporality)json.AggregationTemporality.Value;
        }
        sum.IsMonotonic = json.IsMonotonic;
        return sum;
    }

    private static Histogram ToProtobuf(OtlpHistogramJson json)
    {
        var histogram = new Histogram();
        if (json.DataPoints is not null)
        {
            foreach (var dp in json.DataPoints)
            {
                histogram.DataPoints.Add(ToProtobuf(dp));
            }
        }
        if (json.AggregationTemporality.HasValue)
        {
            histogram.AggregationTemporality = (AggregationTemporality)json.AggregationTemporality.Value;
        }
        return histogram;
    }

    private static ExponentialHistogram ToProtobuf(OtlpExponentialHistogramJson json)
    {
        var histogram = new ExponentialHistogram();
        if (json.DataPoints is not null)
        {
            foreach (var dp in json.DataPoints)
            {
                histogram.DataPoints.Add(ToProtobuf(dp));
            }
        }
        if (json.AggregationTemporality.HasValue)
        {
            histogram.AggregationTemporality = (AggregationTemporality)json.AggregationTemporality.Value;
        }
        return histogram;
    }

    private static Summary ToProtobuf(OtlpSummaryJson json)
    {
        var summary = new Summary();
        if (json.DataPoints is not null)
        {
            foreach (var dp in json.DataPoints)
            {
                summary.DataPoints.Add(ToProtobuf(dp));
            }
        }
        return summary;
    }

    private static NumberDataPoint ToProtobuf(OtlpNumberDataPointJson json)
    {
        var dataPoint = new NumberDataPoint();
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                dataPoint.Attributes.Add(ToProtobuf(attr));
            }
        }
        if (json.StartTimeUnixNano.HasValue)
        {
            dataPoint.StartTimeUnixNano = json.StartTimeUnixNano.Value;
        }
        if (json.TimeUnixNano.HasValue)
        {
            dataPoint.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.AsDouble.HasValue)
        {
            dataPoint.AsDouble = json.AsDouble.Value;
        }
        else if (json.AsInt.HasValue)
        {
            dataPoint.AsInt = json.AsInt.Value;
        }
        if (json.Exemplars is not null)
        {
            foreach (var ex in json.Exemplars)
            {
                dataPoint.Exemplars.Add(ToProtobuf(ex));
            }
        }
        dataPoint.Flags = json.Flags;
        return dataPoint;
    }

    private static HistogramDataPoint ToProtobuf(OtlpHistogramDataPointJson json)
    {
        var dataPoint = new HistogramDataPoint();
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                dataPoint.Attributes.Add(ToProtobuf(attr));
            }
        }
        if (json.StartTimeUnixNano.HasValue)
        {
            dataPoint.StartTimeUnixNano = json.StartTimeUnixNano.Value;
        }
        if (json.TimeUnixNano.HasValue)
        {
            dataPoint.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.Count.HasValue)
        {
            dataPoint.Count = json.Count.Value;
        }
        if (json.Sum.HasValue)
        {
            dataPoint.Sum = json.Sum.Value;
        }
        if (json.BucketCounts is not null)
        {
            foreach (var bc in json.BucketCounts)
            {
                dataPoint.BucketCounts.Add(ulong.Parse(bc, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (json.ExplicitBounds is not null)
        {
            foreach (var eb in json.ExplicitBounds)
            {
                dataPoint.ExplicitBounds.Add(eb);
            }
        }
        if (json.Exemplars is not null)
        {
            foreach (var ex in json.Exemplars)
            {
                dataPoint.Exemplars.Add(ToProtobuf(ex));
            }
        }
        dataPoint.Flags = json.Flags;
        if (json.Min.HasValue)
        {
            dataPoint.Min = json.Min.Value;
        }
        if (json.Max.HasValue)
        {
            dataPoint.Max = json.Max.Value;
        }
        return dataPoint;
    }

    private static ExponentialHistogramDataPoint ToProtobuf(OtlpExponentialHistogramDataPointJson json)
    {
        var dataPoint = new ExponentialHistogramDataPoint();
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                dataPoint.Attributes.Add(ToProtobuf(attr));
            }
        }
        if (json.StartTimeUnixNano.HasValue)
        {
            dataPoint.StartTimeUnixNano = json.StartTimeUnixNano.Value;
        }
        if (json.TimeUnixNano.HasValue)
        {
            dataPoint.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.Count.HasValue)
        {
            dataPoint.Count = json.Count.Value;
        }
        if (json.Sum.HasValue)
        {
            dataPoint.Sum = json.Sum.Value;
        }
        dataPoint.Scale = json.Scale;
        if (json.ZeroCount.HasValue)
        {
            dataPoint.ZeroCount = json.ZeroCount.Value;
        }
        if (json.Positive is not null)
        {
            dataPoint.Positive = ToProtobuf(json.Positive);
        }
        if (json.Negative is not null)
        {
            dataPoint.Negative = ToProtobuf(json.Negative);
        }
        dataPoint.Flags = json.Flags;
        if (json.Exemplars is not null)
        {
            foreach (var ex in json.Exemplars)
            {
                dataPoint.Exemplars.Add(ToProtobuf(ex));
            }
        }
        if (json.Min.HasValue)
        {
            dataPoint.Min = json.Min.Value;
        }
        if (json.Max.HasValue)
        {
            dataPoint.Max = json.Max.Value;
        }
        dataPoint.ZeroThreshold = json.ZeroThreshold;
        return dataPoint;
    }

    private static ExponentialHistogramDataPoint.Types.Buckets ToProtobuf(OtlpExponentialHistogramBucketsJson json)
    {
        var buckets = new ExponentialHistogramDataPoint.Types.Buckets();
        buckets.Offset = json.Offset;
        if (json.BucketCounts is not null)
        {
            foreach (var bc in json.BucketCounts)
            {
                buckets.BucketCounts.Add(ulong.Parse(bc, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        return buckets;
    }

    private static SummaryDataPoint ToProtobuf(OtlpSummaryDataPointJson json)
    {
        var dataPoint = new SummaryDataPoint();
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                dataPoint.Attributes.Add(ToProtobuf(attr));
            }
        }
        if (json.StartTimeUnixNano.HasValue)
        {
            dataPoint.StartTimeUnixNano = json.StartTimeUnixNano.Value;
        }
        if (json.TimeUnixNano.HasValue)
        {
            dataPoint.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.Count.HasValue)
        {
            dataPoint.Count = json.Count.Value;
        }
        dataPoint.Sum = json.Sum;
        if (json.QuantileValues is not null)
        {
            foreach (var qv in json.QuantileValues)
            {
                dataPoint.QuantileValues.Add(ToProtobuf(qv));
            }
        }
        dataPoint.Flags = json.Flags;
        return dataPoint;
    }

    private static SummaryDataPoint.Types.ValueAtQuantile ToProtobuf(OtlpValueAtQuantileJson json)
    {
        var valueAtQuantile = new SummaryDataPoint.Types.ValueAtQuantile();
        valueAtQuantile.Quantile = json.Quantile;
        valueAtQuantile.Value = json.Value;
        return valueAtQuantile;
    }

    private static Exemplar ToProtobuf(OtlpExemplarJson json)
    {
        var exemplar = new Exemplar();
        if (json.FilteredAttributes is not null)
        {
            foreach (var attr in json.FilteredAttributes)
            {
                exemplar.FilteredAttributes.Add(ToProtobuf(attr));
            }
        }
        if (json.TimeUnixNano.HasValue)
        {
            exemplar.TimeUnixNano = json.TimeUnixNano.Value;
        }
        if (json.AsDouble.HasValue)
        {
            exemplar.AsDouble = json.AsDouble.Value;
        }
        else if (json.AsInt.HasValue)
        {
            exemplar.AsInt = json.AsInt.Value;
        }
        if (json.SpanId is not null)
        {
            exemplar.SpanId = HexToByteString(json.SpanId);
        }
        if (json.TraceId is not null)
        {
            exemplar.TraceId = HexToByteString(json.TraceId);
        }
        return exemplar;
    }

    private static Resource ToProtobuf(OtlpResourceJson json)
    {
        var resource = new Resource();
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                resource.Attributes.Add(ToProtobuf(attr));
            }
        }
        resource.DroppedAttributesCount = json.DroppedAttributesCount;
        return resource;
    }

    private static InstrumentationScope ToProtobuf(OtlpInstrumentationScopeJson json)
    {
        var scope = new InstrumentationScope();
        if (json.Name is not null)
        {
            scope.Name = json.Name;
        }
        if (json.Version is not null)
        {
            scope.Version = json.Version;
        }
        if (json.Attributes is not null)
        {
            foreach (var attr in json.Attributes)
            {
                scope.Attributes.Add(ToProtobuf(attr));
            }
        }
        scope.DroppedAttributesCount = json.DroppedAttributesCount;
        return scope;
    }

    private static KeyValue ToProtobuf(OtlpKeyValueJson json)
    {
        var kv = new KeyValue();
        if (json.Key is not null)
        {
            kv.Key = json.Key;
        }
        if (json.Value is not null)
        {
            kv.Value = ToProtobuf(json.Value);
        }
        return kv;
    }

    private static AnyValue ToProtobuf(OtlpAnyValueJson json)
    {
        var anyValue = new AnyValue();
        if (json.StringValue is not null)
        {
            anyValue.StringValue = json.StringValue;
        }
        else if (json.BoolValue.HasValue)
        {
            anyValue.BoolValue = json.BoolValue.Value;
        }
        else if (json.IntValue.HasValue)
        {
            anyValue.IntValue = json.IntValue.Value;
        }
        else if (json.DoubleValue.HasValue)
        {
            anyValue.DoubleValue = json.DoubleValue.Value;
        }
        else if (json.ArrayValue is not null)
        {
            anyValue.ArrayValue = ToProtobuf(json.ArrayValue);
        }
        else if (json.KvlistValue is not null)
        {
            anyValue.KvlistValue = ToProtobuf(json.KvlistValue);
        }
        else if (json.BytesValue is not null)
        {
            anyValue.BytesValue = ByteString.FromBase64(json.BytesValue);
        }
        return anyValue;
    }

    private static ArrayValue ToProtobuf(OtlpArrayValueJson json)
    {
        var arrayValue = new ArrayValue();
        if (json.Values is not null)
        {
            foreach (var v in json.Values)
            {
                arrayValue.Values.Add(ToProtobuf(v));
            }
        }
        return arrayValue;
    }

    private static KeyValueList ToProtobuf(OtlpKeyValueListJson json)
    {
        var kvList = new KeyValueList();
        if (json.Values is not null)
        {
            foreach (var kv in json.Values)
            {
                kvList.Values.Add(ToProtobuf(kv));
            }
        }
        return kvList;
    }

    /// <summary>
    /// Converts a hexadecimal string to a ByteString.
    /// </summary>
    /// <param name="hex">The hexadecimal string to convert.</param>
    /// <returns>A ByteString containing the decoded bytes.</returns>
    /// <exception cref="ArgumentException">Thrown when the hex string has an odd length.</exception>
    internal static ByteString HexToByteString(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return ByteString.Empty;
        }

        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex string must have an even length.", nameof(hex));
        }

        var hexSpan = hex.AsSpan();
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hexSpan.Slice(i * 2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        }
        return ByteString.CopyFrom(bytes);
    }
}

/// <summary>
/// Converts protobuf types to OTLP JSON types.
/// </summary>
internal static class OtlpProtobufToJsonConverter
{
    /// <summary>
    /// Converts an export trace service response from protobuf to JSON.
    /// </summary>
    public static OtlpExportTraceServiceResponseJson ToJson(ExportTraceServiceResponse response)
    {
        var json = new OtlpExportTraceServiceResponseJson();
        if (response.PartialSuccess is not null && (response.PartialSuccess.RejectedSpans > 0 || !string.IsNullOrEmpty(response.PartialSuccess.ErrorMessage)))
        {
            json.PartialSuccess = new OtlpExportTracePartialSuccessJson
            {
                RejectedSpans = response.PartialSuccess.RejectedSpans,
                ErrorMessage = response.PartialSuccess.ErrorMessage
            };
        }
        return json;
    }

    /// <summary>
    /// Converts an export logs service response from protobuf to JSON.
    /// </summary>
    public static OtlpExportLogsServiceResponseJson ToJson(ExportLogsServiceResponse response)
    {
        var json = new OtlpExportLogsServiceResponseJson();
        if (response.PartialSuccess is not null && (response.PartialSuccess.RejectedLogRecords > 0 || !string.IsNullOrEmpty(response.PartialSuccess.ErrorMessage)))
        {
            json.PartialSuccess = new OtlpExportLogsPartialSuccessJson
            {
                RejectedLogRecords = response.PartialSuccess.RejectedLogRecords,
                ErrorMessage = response.PartialSuccess.ErrorMessage
            };
        }
        return json;
    }

    /// <summary>
    /// Converts an export metrics service response from protobuf to JSON.
    /// </summary>
    public static OtlpExportMetricsServiceResponseJson ToJson(ExportMetricsServiceResponse response)
    {
        var json = new OtlpExportMetricsServiceResponseJson();
        if (response.PartialSuccess is not null && (response.PartialSuccess.RejectedDataPoints > 0 || !string.IsNullOrEmpty(response.PartialSuccess.ErrorMessage)))
        {
            json.PartialSuccess = new OtlpExportMetricsPartialSuccessJson
            {
                RejectedDataPoints = response.PartialSuccess.RejectedDataPoints,
                ErrorMessage = response.PartialSuccess.ErrorMessage
            };
        }
        return json;
    }
}
