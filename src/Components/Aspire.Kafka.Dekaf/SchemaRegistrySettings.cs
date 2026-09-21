// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf.SchemaRegistry;

namespace Aspire.Kafka.Dekaf;

/// <summary>
/// Provides settings for a Dekaf Schema Registry client.
/// </summary>
public sealed class SchemaRegistrySettings
{
    /// <summary>
    /// Gets or sets the native client configuration, including registry URLs, authentication, TLS, and caching.
    /// </summary>
    /// <remarks>
    /// A matching connection string overrides configured URLs before the settings callback runs.
    /// Native options are init-only; replace this property to configure them in code.
    /// <code>settings.Config = new SchemaRegistryConfig { Url = "https://registry.example.com" };</code>
    /// </remarks>
    public SchemaRegistryConfig Config { get; set; } = new() { Url = string.Empty };

    /// <summary>
    /// Gets or sets whether health checks are disabled. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets the timeout for listing registry subjects during a health check. The default is 30 seconds.
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
