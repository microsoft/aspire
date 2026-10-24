// <copyright file="ChaosProxyClientSettings.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Chaos.Client;

/// <summary>
/// Configuration for the Aspire chaos proxy client integration. Bound from the
/// <c>Aspire:Chaos:Client</c> (or <c>Aspire:Chaos:Client:{name}</c> for keyed) configuration
/// section by <see cref="Microsoft.Extensions.Hosting.AspireChaosClientExtensions.AddChaosProxyClient"/>.
/// </summary>
public sealed class ChaosProxyClientSettings
{
    /// <summary>
    /// The connection string to the chaos proxy. Treated as the proxy's base URL
    /// (e.g., <c>http://chaos-dtfx-queue:1234</c>). When set, overrides
    /// <see cref="Endpoint"/>. Typically resolved from
    /// <c>builder.Configuration.GetConnectionString(connectionName)</c>, which Aspire
    /// populates from <c>WithReference(proxy)</c> on the consumer service.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Explicit base URI for the chaos proxy. Use this in place of
    /// <see cref="ConnectionString"/> when configuring out-of-band (e.g., in tests).
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Disables the chaos proxy health check registration. Defaults to <see langword="false"/>
    /// — i.e., the integration adds a health check that probes <c>GET /chaos/healthz</c>.
    /// </summary>
    public bool DisableHealthChecks { get; set; }
}
