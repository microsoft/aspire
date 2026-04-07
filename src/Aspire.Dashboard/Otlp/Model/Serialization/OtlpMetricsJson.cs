// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Otlp.Serialization;

namespace Aspire.Dashboard.Otlp.Model.Serialization;

/// <summary>
/// Represents the export metrics service request.
/// </summary>
internal sealed class OtlpExportMetricsServiceRequestJson
{
    /// <summary>
    /// An array of ResourceMetrics.
    /// </summary>
    [JsonPropertyName("resourceMetrics")]
    public OtlpResourceMetricsJson[]? ResourceMetrics { get; set; }
}

/// <summary>
/// Represents the export metrics service response.
/// </summary>
internal sealed class OtlpExportMetricsServiceResponseJson
{
    /// <summary>
    /// The details of a partially successful export request.
    /// </summary>
    [JsonPropertyName("partialSuccess")]
    public OtlpExportMetricsPartialSuccessJson? PartialSuccess { get; set; }
}

/// <summary>
/// Represents partial success information for metrics export.
/// </summary>
internal sealed class OtlpExportMetricsPartialSuccessJson
{
    /// <summary>
    /// The number of rejected data points. Serialized as string per protojson spec for int64.
    /// </summary>
    [JsonPropertyName("rejectedDataPoints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long RejectedDataPoints { get; set; }

    /// <summary>
    /// A developer-facing human-readable error message.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
