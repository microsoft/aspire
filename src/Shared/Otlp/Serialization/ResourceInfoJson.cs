// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Information about a resource that has telemetry data.
/// Shared between Dashboard and CLI for serialization/deserialization of the telemetry resources API.
/// </summary>
internal sealed class ResourceInfoJson
{
    /// <summary>
    /// Gets or sets the base resource name (e.g., "catalogservice").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Gets or sets the instance ID if this is a replica (e.g., "abc123"), or null if single instance.
    /// </summary>
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    /// <summary>
    /// Gets or sets the full display name including instance ID (e.g., "catalogservice-abc123" or "catalogservice").
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets whether this resource has structured logs.
    /// </summary>
    [JsonPropertyName("hasLogs")]
    public bool HasLogs { get; set; }

    /// <summary>
    /// Gets or sets whether this resource has traces/spans.
    /// </summary>
    [JsonPropertyName("hasTraces")]
    public bool HasTraces { get; set; }

    /// <summary>
    /// Gets or sets whether this resource has metrics.
    /// </summary>
    [JsonPropertyName("hasMetrics")]
    public bool HasMetrics { get; set; }

    /// <summary>
    /// Gets the composite name by combining Name and InstanceId.
    /// </summary>
    /// <returns>The composite name (e.g., "catalogservice-abc123" or "catalogservice").</returns>
    public string GetCompositeName()
    {
        if (InstanceId is null)
        {
            return Name;
        }

        return $"{Name}-{InstanceId}";
    }
}
