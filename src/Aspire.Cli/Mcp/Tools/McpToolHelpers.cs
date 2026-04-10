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

        var dashboardBaseUrl = StripLoginPath(dashboardInfo.DashboardUrls.FirstOrDefault());

        return (dashboardInfo.ApiToken, dashboardInfo.ApiBaseUrl, dashboardBaseUrl);
    }

    /// <summary>
    /// Strips the <c>/login</c> path segment (and any query string) from a dashboard URL
    /// returned by the AppHost. Other path segments are preserved.
    /// </summary>
    internal static string? StripLoginPath(string? url)
    {
        if (url is null)
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Dashboard URLs from the AppHost look like: http://localhost:18888/login?t=abcd1234
            // or with a base path: http://localhost:18888/base/login?t=abcd1234
            // Strip the trailing /login segment but preserve any other path components.
            var path = uri.AbsolutePath;
            if (path.EndsWith("/login", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^"/login".Length];
            }

            return $"{uri.Scheme}://{uri.Authority}{path.TrimEnd('/')}";
        }

        return url;
    }
}
