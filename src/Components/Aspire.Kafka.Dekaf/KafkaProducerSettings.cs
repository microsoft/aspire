// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf.Extensions.HealthChecks;

namespace Aspire.Kafka.Dekaf;

/// <summary>
/// Provides settings for a Dekaf Kafka producer.
/// </summary>
public sealed class KafkaProducerSettings
{
    /// <summary>
    /// Gets or sets the comma-separated bootstrap servers.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets whether health checks are disabled. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets the options for the producer health check.
    /// </summary>
    public DekafProducerHealthCheckOptions HealthCheck { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this registration adds the shared Dekaf meter to OpenTelemetry. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableMetrics { get; set; }

    /// <summary>
    /// Gets or sets whether this registration adds the shared Dekaf activity source to OpenTelemetry. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableTracing { get; set; }
}
