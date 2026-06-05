// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Otlp.Model.Serialization;

/// <summary>
/// Represents the export trace service response.
/// </summary>
internal sealed class OtlpExportTraceServiceResponseJson
{
    /// <summary>
    /// The details of a partially successful export request.
    /// </summary>
    [JsonPropertyName("partialSuccess")]
    public OtlpExportTracePartialSuccessJson? PartialSuccess { get; set; }
}

/// <summary>
/// Represents partial success information for trace export.
/// </summary>
internal sealed class OtlpExportTracePartialSuccessJson
{
    /// <summary>
    /// The number of rejected spans. Serialized as string per protojson spec for int64.
    /// </summary>
    [JsonPropertyName("rejectedSpans")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long RejectedSpans { get; set; }

    /// <summary>
    /// A developer-facing human-readable error message.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Represents the export logs service response.
/// </summary>
internal sealed class OtlpExportLogsServiceResponseJson
{
    /// <summary>
    /// The details of a partially successful export request.
    /// </summary>
    [JsonPropertyName("partialSuccess")]
    public OtlpExportLogsPartialSuccessJson? PartialSuccess { get; set; }
}

/// <summary>
/// Represents partial success information for logs export.
/// </summary>
internal sealed class OtlpExportLogsPartialSuccessJson
{
    /// <summary>
    /// The number of rejected log records. Serialized as string per protojson spec for int64.
    /// </summary>
    [JsonPropertyName("rejectedLogRecords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    public long RejectedLogRecords { get; set; }

    /// <summary>
    /// A developer-facing human-readable error message.
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
