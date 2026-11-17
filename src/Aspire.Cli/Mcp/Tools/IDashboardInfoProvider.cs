// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
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
internal sealed class StaticDashboardInfoProvider(
    string dashboardUrl,
    string? apiKey,
    IHttpClientFactory? httpClientFactory = null,
    ILogger? logger = null) : IDashboardInfoProvider
{
    public bool IsDirectConnection => true;

    public async Task<(string apiToken, string apiBaseUrl, string? dashboardBaseUrl)> GetDashboardInfoAsync(CancellationToken cancellationToken)
    {
        var loginToken = DashboardUrls.ExtractDashboardLoginToken(dashboardUrl);
        var apiBaseUrl = DashboardUrls.NormalizeDashboardRequestUrl(dashboardUrl, stripLoginPath: true);
        if (apiBaseUrl is null)
        {
            throw new McpProtocolException(
                "The dashboard URL must be an absolute HTTP or HTTPS URL.",
                McpErrorCode.InvalidParams);
        }

        if (loginToken is not null)
        {
            apiBaseUrl = DashboardUrls.RemoveDashboardLoginToken(apiBaseUrl) ?? apiBaseUrl;
        }

        var apiToken = apiKey;
        if (apiToken is null && loginToken is not null)
        {
            if (httpClientFactory is null || logger is null)
            {
                throw new McpProtocolException(
                    "The configured dashboard login token could not be exchanged for an API key.",
                    McpErrorCode.InternalError);
            }

            var exchange = await TelemetryCommandHelpers.ExchangeLoginTokenForApiKeyAsync(
                httpClientFactory,
                apiBaseUrl,
                loginToken,
                logger,
                cancellationToken).ConfigureAwait(false);
            if (!exchange.Success)
            {
                throw new McpProtocolException(
                    "The configured dashboard login token could not be exchanged for an API key.",
                    McpErrorCode.InternalError);
            }

            apiToken = exchange.ApiKey;
        }

        var dashboardBaseUrl = McpToolHelpers.StripLoginPath(dashboardUrl);
        return (apiToken ?? string.Empty, apiBaseUrl, dashboardBaseUrl);
    }
}
