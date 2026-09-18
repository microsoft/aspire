// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.HealthModels;

/// <summary>
/// The data source a signal reads from.
/// </summary>
/// <remarks>
/// Mirrors the <c>SignalKind</c> discriminator in Azure Monitor health models. Signals produced locally by the
/// dashboard use <see cref="External"/> because, like Azure external signals, their state is reported by the
/// app host rather than computed by the health model service from a metric or query.
/// </remarks>
public enum SignalKind
{
    /// <summary>A platform metric read from an Azure resource.</summary>
    AzureResourceMetric,

    /// <summary>A KQL query run against a Log Analytics workspace.</summary>
    LogAnalyticsQuery,

    /// <summary>A PromQL query run against an Azure Monitor workspace.</summary>
    PrometheusMetricsQuery,

    /// <summary>A state reported by an external producer rather than evaluated by the health model itself.</summary>
    External
}
