// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Confluent.Kafka;

namespace Aspire.Confluent.Kafka;

/// <summary>
/// Provides the client configuration settings for connecting to a Kafka message broker to produce messages.
/// </summary>
public sealed class KafkaProducerSettings
{
    /// <summary>
    /// Gets or sets the connection string of the Kafka server to connect to.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets the configuration settings for the Kafka producer.
    /// </summary>
    public ProducerConfig Config { get; } = new ProducerConfig();

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry metrics are enabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableMetrics { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the Kafka health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; }

    internal void Consolidate()
    {
        Debug.Assert(Config is not null);

        if (ConnectionString is not null)
        {
            KafkaConnectionString.Apply(ConnectionString, Config);
        }

        if (!DisableMetrics)
        {
            Config.StatisticsIntervalMs ??= 1000;
        }
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Config.BootstrapServers))
        {
            throw new InvalidOperationException("No bootstrap servers configured.");
        }
    }
}
