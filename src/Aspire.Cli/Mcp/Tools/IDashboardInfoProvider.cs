// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

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
        // Build the request URL from the original value so query-based authentication remains
        // available to HttpClient. The display URL is sanitized independently for MCP output.
        var apiBaseUrl = DashboardUrls.NormalizeDashboardRequestUrl(dashboardUrl, stripLoginPath: true);
        if (apiBaseUrl is null)
        {
            throw new McpProtocolException(
                "The dashboard URL must be an absolute HTTP or HTTPS URL.",
                McpErrorCode.InvalidParams);
        }

        var dashboardBaseUrl = McpToolHelpers.StripLoginPath(dashboardUrl);
        return Task.FromResult((apiToken, apiBaseUrl, dashboardBaseUrl));
    }
}
