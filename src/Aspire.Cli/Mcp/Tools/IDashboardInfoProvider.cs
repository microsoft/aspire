// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// Provides dashboard connection info (API token, base URL, dashboard UI URL) for telemetry access.
/// </summary>
internal interface IDashboardInfoProvider
{
    /// <summary>
    /// Whether the dashboard URL was provided directly (e.g. via --dashboard-url) rather than discovered through an AppHost.
    /// </summary>
    bool IsDirectConnection { get; }

    /// <summary>
    /// Gets dashboard connection info for telemetry API access.
    /// </summary>
    /// <returns>A tuple of (apiToken, apiBaseUrl, dashboardBaseUrl). apiToken may be empty for unsecured dashboards.</returns>
    Task<(string apiToken, string apiBaseUrl, string? dashboardBaseUrl)> GetDashboardInfoAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Gets dashboard info from the AppHost backchannel (default behavior).
/// </summary>
internal sealed class BackchannelDashboardInfoProvider(
    IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
    ILogger logger) : IDashboardInfoProvider
{
    public bool IsDirectConnection => false;

    public Task<(string apiToken, string apiBaseUrl, string? dashboardBaseUrl)> GetDashboardInfoAsync(CancellationToken cancellationToken)
    {
        return McpToolHelpers.GetDashboardInfoAsync(auxiliaryBackchannelMonitor, logger, cancellationToken);
    }
}

/// <summary>
/// Returns dashboard info from statically-provided URL and optional API key (for standalone dashboards).
/// </summary>
internal sealed class StaticDashboardInfoProvider(string dashboardUrl, string? apiKey) : IDashboardInfoProvider
{
    public bool IsDirectConnection => true;

    public Task<(string apiToken, string apiBaseUrl, string? dashboardBaseUrl)> GetDashboardInfoAsync(CancellationToken cancellationToken)
    {
        // For unsecured dashboards, apiToken is empty string (no X-API-Key header will be sent)
        var apiToken = apiKey ?? string.Empty;
        // Normalize the API base URL (e.g., rewrite *.localhost to localhost) for HTTP requests,
        // but preserve the original URL as the dashboard display URL.
        var apiBaseUrl = McpToolHelpers.NormalizeDashboardUrl(dashboardUrl);
        return Task.FromResult((apiToken, apiBaseUrl, (string?)dashboardUrl));
    }
}
