// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Web;
using Aspire.Cli.Backchannel;
using Aspire.Dashboard.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

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

        var apiBaseUrl = NormalizeDashboardUrl(dashboardInfo.ApiBaseUrl);
        var dashboardBaseUrl = StripLoginPath(dashboardInfo.DashboardUrls.FirstOrDefault());

        return (dashboardInfo.ApiToken, apiBaseUrl, dashboardBaseUrl);
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

    /// <summary>
    /// Replaces AppHost-scoped <c>*.localhost</c> dashboard hostnames with <c>localhost</c>.
    /// </summary>
    /// <remarks>
    /// DNS resolvers typically don't implement RFC 6761 for localhost subdomains, so hosts
    /// like <c>dashboard.dev.localhost</c> fail to resolve when making HTTP requests.
    /// Rewriting to <c>localhost</c> ensures the CLI can reach the dashboard API.
    /// </remarks>
    internal static string NormalizeDashboardUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLocalhostTld(uri.Host))
        {
            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            var pathAndQuery = uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery;
            return $"{uri.Scheme}://localhost{port}{pathAndQuery}{uri.Fragment}";
        }

        return url;
    }

    private static bool IsLocalhostTld(string host)
    {
        return host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the browser token (<c>t</c> query parameter) from a dashboard login URL.
    /// Returns <c>null</c> if the URL does not contain a login token.
    /// </summary>
    internal static string? ExtractLoginToken(string? url)
    {
        if (url is null)
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.EndsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            // Parse query string to find 't' parameter
            var queryParams = HttpUtility.ParseQueryString(uri.Query);
            var token = queryParams["t"];
            if (!string.IsNullOrEmpty(token))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether a resource snapshot has the <c>resource.excludeFromMcp</c> property set to true.
    /// Resources with this property should be excluded from all MCP tool results.
    /// </summary>
    internal static bool IsExcludedFromMcp(ResourceSnapshot snapshot)
    {
        if (snapshot.Properties.TryGetValue(KnownProperties.Resource.ExcludeFromMcp, out var value) && value is not null)
        {
            if (value is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<bool>(out var boolValue))
                {
                    return boolValue;
                }

                if (jsonValue.TryGetValue<string>(out var stringValue) && bool.TryParse(stringValue, out var parsedBool))
                {
                    return parsedBool;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the error message text for a resource that is excluded from MCP.
    /// </summary>
    internal static string GetResourceNotAvailableMessage(string resourceName) =>
        $"Resource '{resourceName}' is not available.";

    /// <summary>
    /// Gets resource snapshots from the backchannel and checks whether the specified resource is excluded from MCP.
    /// Returns an error <see cref="CallToolResult"/> if the resource is excluded, or <c>null</c> if it is not excluded.
    /// </summary>
    internal static async Task<CallToolResult?> CheckResourceExcludedAsync(
        IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var excludedNames = await GetExcludedResourceNamesAsync(auxiliaryBackchannelMonitor, cancellationToken).ConfigureAwait(false);
        return CreateExcludedResult(excludedNames, resourceName);
    }

    /// <summary>
    /// Checks whether the specified resource is excluded from MCP using an existing connection.
    /// Returns an error <see cref="CallToolResult"/> if the resource is excluded, or <c>null</c> if it is not excluded.
    /// </summary>
    internal static async Task<CallToolResult?> CheckResourceExcludedAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        CancellationToken cancellationToken)
    {
        var excludedNames = await GetExcludedResourceNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        return CreateExcludedResult(excludedNames, resourceName);
    }

    private static CallToolResult? CreateExcludedResult(HashSet<string> excludedNames, string resourceName)
    {
        if (excludedNames.Contains(resourceName))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = GetResourceNotAvailableMessage(resourceName) }],
                IsError = true
            };
        }

        return null;
    }

    /// <summary>
    /// Gets the set of resource names that are excluded from MCP.
    /// </summary>
    internal static async Task<HashSet<string>> GetExcludedResourceNamesAsync(
        IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
        CancellationToken cancellationToken)
    {
        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(auxiliaryBackchannelMonitor, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return [];
        }

        return await GetExcludedResourceNamesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the set of resource names that are excluded from MCP using an existing connection.
    /// </summary>
    internal static async Task<HashSet<string>> GetExcludedResourceNamesAsync(
        IAppHostAuxiliaryBackchannel connection,
        CancellationToken cancellationToken)
    {
        var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
        var excludedNames = new HashSet<string>(StringComparers.ResourceName);

        foreach (var snapshot in snapshots)
        {
            if (IsExcludedFromMcp(snapshot))
            {
                excludedNames.Add(snapshot.Name);
                if (snapshot.DisplayName is not null)
                {
                    excludedNames.Add(snapshot.DisplayName);
                }
            }
        }

        return excludedNames;
    }
}
