// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Aspire.Cli.Mcp.Tools;

internal static class McpToolHelpers
{
    public static async Task<(string apiToken, string apiBaseUrl, string? dashboardBaseUrl)> GetDashboardInfoAsync(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, ILogger logger, CancellationToken cancellationToken)
    {
        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
        }

        var dashboardInfo = await connection.GetDashboardInfoV2Async(cancellationToken).ConfigureAwait(false);
        if (dashboardInfo?.ApiBaseUrl is null || dashboardInfo.ApiToken is null)
        {
            logger.LogWarning("Dashboard API is not available");
            throw new McpProtocolException(McpErrorMessages.DashboardNotAvailable, McpErrorCode.InternalError);
        }

        var dashboardBaseUrl = GetBaseUrl(dashboardInfo.DashboardUrls.FirstOrDefault());

        return (dashboardInfo.ApiToken, dashboardInfo.ApiBaseUrl, dashboardBaseUrl);
    }

    /// <summary>
    /// Extracts the base URL (scheme, host, and port) from a URL, removing any path and query string.
    /// </summary>
    /// <param name="url">The full URL that may contain path and query string.</param>
    /// <returns>The base URL with only scheme, host, and port, or null if the input is null or invalid.</returns>
    internal static string? GetBaseUrl(string? url)
    {
        if (url is null)
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Authority}";
        }

        return url;
    }
}
