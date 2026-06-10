// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Response wrapper for telemetry API responses containing OTLP data.
/// </summary>
internal sealed class TelemetryApiResponse
{
    /// <summary>
    /// Gets or sets the OTLP telemetry data payload.
    /// </summary>
    [JsonPropertyName("data")]
    public OtlpTelemetryDataJson? Data { get; set; }

    /// <summary>
    /// Gets or sets the total number of items available on the server.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the number of items returned in this response.
    /// </summary>
    [JsonPropertyName("returnedCount")]
    public int ReturnedCount { get; set; }
}
