// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Shared.Model.Serialization;
#if !CLI
using Aspire.Dashboard.Otlp.Model.Serialization;
#endif

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Source-generated JSON serializer context for OTLP and resource types.
/// Provides AOT-compatible serialization for all shared OTLP JSON types.
/// The CLI define is used to conditionally include project-specific types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
// Common types
[JsonSerializable(typeof(OtlpAnyValueJson))]
[JsonSerializable(typeof(OtlpArrayValueJson))]
[JsonSerializable(typeof(OtlpKeyValueListJson))]
[JsonSerializable(typeof(OtlpKeyValueJson))]
[JsonSerializable(typeof(OtlpInstrumentationScopeJson))]
[JsonSerializable(typeof(OtlpEntityRefJson))]
[JsonSerializable(typeof(OtlpResourceJson))]
[JsonSerializable(typeof(OtlpTelemetryDataJson))]
// Trace types
[JsonSerializable(typeof(OtlpResourceSpansJson))]
[JsonSerializable(typeof(OtlpScopeSpansJson))]
[JsonSerializable(typeof(OtlpSpanJson))]
[JsonSerializable(typeof(OtlpSpanEventJson))]
[JsonSerializable(typeof(OtlpSpanLinkJson))]
[JsonSerializable(typeof(OtlpSpanStatusJson))]
[JsonSerializable(typeof(OtlpExportTraceServiceRequestJson))]
// Log types
[JsonSerializable(typeof(OtlpResourceLogsJson))]
[JsonSerializable(typeof(OtlpScopeLogsJson))]
[JsonSerializable(typeof(OtlpLogRecordJson))]
[JsonSerializable(typeof(OtlpExportLogsServiceRequestJson))]
// Metric types
[JsonSerializable(typeof(OtlpResourceMetricsJson))]
[JsonSerializable(typeof(OtlpScopeMetricsJson))]
[JsonSerializable(typeof(OtlpMetricJson))]
[JsonSerializable(typeof(OtlpGaugeJson))]
[JsonSerializable(typeof(OtlpSumJson))]
[JsonSerializable(typeof(OtlpHistogramJson))]
[JsonSerializable(typeof(OtlpExponentialHistogramJson))]
[JsonSerializable(typeof(OtlpSummaryJson))]
[JsonSerializable(typeof(OtlpNumberDataPointJson))]
[JsonSerializable(typeof(OtlpHistogramDataPointJson))]
[JsonSerializable(typeof(OtlpExponentialHistogramDataPointJson))]
[JsonSerializable(typeof(OtlpExponentialHistogramBucketsJson))]
[JsonSerializable(typeof(OtlpSummaryDataPointJson))]
[JsonSerializable(typeof(OtlpValueAtQuantileJson))]
[JsonSerializable(typeof(OtlpExemplarJson))]
// Resource model types
[JsonSerializable(typeof(ResourceJson))]
// Telemetry API types
[JsonSerializable(typeof(TelemetryApiResponse))]
[JsonSerializable(typeof(ResourceInfoJson))]
[JsonSerializable(typeof(ResourceInfoJson[]))]
#if !CLI
// Dashboard-specific types
[JsonSerializable(typeof(OtlpExportTraceServiceResponseJson))]
[JsonSerializable(typeof(OtlpExportTracePartialSuccessJson))]
[JsonSerializable(typeof(OtlpExportLogsServiceResponseJson))]
[JsonSerializable(typeof(OtlpExportLogsPartialSuccessJson))]
[JsonSerializable(typeof(OtlpExportMetricsServiceRequestJson))]
[JsonSerializable(typeof(OtlpExportMetricsServiceResponseJson))]
[JsonSerializable(typeof(OtlpExportMetricsPartialSuccessJson))]
#endif
internal sealed partial class OtlpJsonSerializerContext : JsonSerializerContext
{
    /// <summary>
    /// Gets the default serializer options for OTLP JSON serialization.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = Default
    };

    /// <summary>
    /// Gets the serializer options for OTLP JSON serialization with indented output.
    /// </summary>
    public static JsonSerializerOptions IndentedOptions { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = Default
    };
}
