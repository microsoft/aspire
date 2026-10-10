// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Aspire.Cli.Backchannel;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

internal static class McpToolHelpers
{
    private const int MaxExceptionTypeNameLength = 128;

    private static readonly HashSet<string> s_sensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token",
        "credential",
        "password",
        "key",
        "api_key",
        "apikey",
        "accountkey",
        "primarykey",
        "secondarykey",
        "sharedaccesskey",
        "subscriptionkey",
        "clientkey",
        "accesskey",
        "authkey",
        "devicekey",
        "secret",
        "sig",
        "signature",
        "code",
        "client_secret",
        "pwd",
        "passwd",
        "auth",
        "authorization",
        "jwt",
        "bearer",
        "sessionid",
        "sas"
    };

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

        var apiBaseUrl = DashboardUrls.NormalizeDashboardRequestUrl(dashboardInfo.ApiBaseUrl, stripLoginPath: false) ?? string.Empty;
        var dashboardBaseUrl = StripLoginPath(dashboardInfo.DashboardUrls.FirstOrDefault());

        return (dashboardInfo.ApiToken, apiBaseUrl, dashboardBaseUrl);
    }

    /// <summary>
    /// Strips the <c>/login</c> path segment and credentials from a dashboard URL
    /// returned by the AppHost. Other path segments and non-sensitive query values are preserved.
    /// </summary>
    internal static string? StripLoginPath(string? url) =>
        SanitizeUrl(
            url,
            stripLoginPath: true,
            normalizeLocalhost: false,
            removeDashboardLoginToken: true);

    /// <summary>
    /// Removes credentials and replaces AppHost-scoped <c>*.localhost</c> dashboard hostnames with <c>localhost</c>.
    /// </summary>
    /// <remarks>
    /// DNS resolvers typically don't implement RFC 6761 for localhost subdomains, so hosts
    /// like <c>dashboard.dev.localhost</c> fail to resolve when making HTTP requests.
    /// Rewriting to <c>localhost</c> ensures the CLI can reach the dashboard API.
    /// </remarks>
    internal static string NormalizeDashboardUrl(string url) =>
        SanitizeUrl(
            url,
            stripLoginPath: false,
            normalizeLocalhost: true,
            removeDashboardLoginToken: false) ?? string.Empty;

    /// <summary>
    /// Removes credentials from an absolute URL before it is returned by an MCP tool.
    /// </summary>
    internal static string? SanitizeUrl(string? url) =>
        SanitizeUrl(
            url,
            stripLoginPath: false,
            normalizeLocalhost: false,
            removeDashboardLoginToken: false,
            allowResourceSchemes: false);

    /// <summary>
    /// Sanitizes an absolute resource endpoint URI while preserving its non-file scheme.
    /// </summary>
    internal static string? SanitizeResourceUrl(string? url) =>
        SanitizeUrl(
            url,
            stripLoginPath: false,
            normalizeLocalhost: false,
            removeDashboardLoginToken: false,
            allowResourceSchemes: true);

    /// <summary>
    /// Removes credentials and dashboard login tokens before an outbound request URL is logged.
    /// </summary>
    internal static string? SanitizeDashboardRequestUrl(string? url) =>
        SanitizeUrl(
            url,
            stripLoginPath: false,
            normalizeLocalhost: false,
            removeDashboardLoginToken: true);

    /// <summary>
    /// Returns bounded exception metadata that is safe to include in diagnostics.
    /// </summary>
    internal static string GetBoundedExceptionDiagnostic(Exception exception)
    {
        var exceptionType = exception.GetType().Name;
        if (exceptionType.Length > MaxExceptionTypeNameLength)
        {
            exceptionType = exceptionType[..MaxExceptionTypeNameLength];
        }

        return exception is HttpRequestException { StatusCode: { } statusCode }
            ? $"{exceptionType}; HTTP {(int)statusCode} ({statusCode})"
            : exceptionType;
    }

    /// <summary>
    /// Maps a runtime resource state to the finite vocabulary exposed through MCP tools.
    /// </summary>
    internal static string? MapResourceState(string? state)
    {
        return state switch
        {
            null => null,
            "Active" or
            "Building" or
            "Exited" or
            "FailedToStart" or
            "Finished" or
            "NotStarted" or
            "Running" or
            "RuntimeUnhealthy" or
            "Starting" or
            "Stopping" or
            "ValueMissing" or
            "Waiting" => state,
            _ => "unknown"
        };
    }

    private static string? SanitizeUrl(
        string? url,
        bool stripLoginPath,
        bool normalizeLocalhost,
        bool removeDashboardLoginToken,
        bool allowResourceSchemes = false)
    {
        if (!(allowResourceSchemes
            ? TryCreateResourceUri(url, out var uri)
            : TryCreateHttpUri(url, out uri)))
        {
            return null;
        }

        // Dashboard login URLs look like:
        //   https://user:password@dashboard.localhost:18888/base/login?t=token&view=resources
        // User-info and sensitive query values must never cross the MCP boundary, while the
        // non-sensitive path and query values remain useful to clients.
        var isLoginPath = uri.AbsolutePath.EndsWith("/login", StringComparison.OrdinalIgnoreCase);
        var path = stripLoginPath && isLoginPath
            ? uri.AbsolutePath[..^"/login".Length]
            : uri.AbsolutePath;
        var builder = new UriBuilder(uri)
        {
            Host = normalizeLocalhost && IsLocalhostTld(uri.Host) ? "localhost" : uri.Host,
            Path = path,
            Query = SanitizeQuery(
                uri.Query,
                removeDashboardLoginToken: removeDashboardLoginToken || stripLoginPath || isLoginPath),
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        var sanitizedUri = builder.Uri;
        if (sanitizedUri.AbsolutePath == "/")
        {
            return sanitizedUri.GetLeftPart(UriPartial.Authority) + sanitizedUri.Query + sanitizedUri.Fragment;
        }

        return sanitizedUri.AbsoluteUri;
    }

    private static bool TryCreateHttpUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(uri.Host);
    }

    private static bool TryCreateResourceUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            uri.Scheme != Uri.UriSchemeFile &&
            value.StartsWith($"{uri.Scheme}://", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(uri.Host) &&
            Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown;
    }

    private static string SanitizeQuery(string query, bool removeDashboardLoginToken)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var sanitizedQuery = new StringBuilder(query.Length);
        var queryWithoutPrefix = query.AsSpan(1);
        var groupStart = 0;

        // Preserve '&' as the original query-group boundary. For example:
        //   ?apikey=AB;cd=ef&view=resources
        // If any parameter-shaped semicolon segment is sensitive, retaining another part of
        // that group could expose a secret tail, so the whole "apikey=AB;cd=ef" group is removed.
        for (var index = 0; index <= queryWithoutPrefix.Length; index++)
        {
            if (index < queryWithoutPrefix.Length && queryWithoutPrefix[index] != '&')
            {
                continue;
            }

            var group = queryWithoutPrefix[groupStart..index];
            if (!ContainsSensitiveQueryParameter(group, removeDashboardLoginToken))
            {
                if (sanitizedQuery.Length > 0)
                {
                    sanitizedQuery.Append('&');
                }

                sanitizedQuery.Append(group);
            }

            groupStart = index + 1;
        }

        return sanitizedQuery.ToString();
    }

    private static bool ContainsSensitiveQueryParameter(
        ReadOnlySpan<char> group,
        bool removeDashboardLoginToken)
    {
        if (IsSensitiveQueryParameter(group, removeDashboardLoginToken))
        {
            return true;
        }

        for (var index = 0; index < group.Length; index++)
        {
            if (group[index] == ';' &&
                StartsQueryParameter(group, index + 1) &&
                IsSensitiveQueryParameter(group[(index + 1)..], removeDashboardLoginToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSensitiveQueryParameter(
        ReadOnlySpan<char> parameter,
        bool removeDashboardLoginToken)
    {
        var parameterEnd = parameter.IndexOf(';');
        if (parameterEnd >= 0)
        {
            parameter = parameter[..parameterEnd];
        }

        var separatorIndex = parameter.IndexOf('=');
        var encodedKey = separatorIndex >= 0 ? parameter[..separatorIndex] : parameter;
        var key = HttpUtility.UrlDecode(encodedKey.ToString());
        return IsSensitiveQueryKey(key) ||
            (removeDashboardLoginToken && string.Equals(key, "t", StringComparison.OrdinalIgnoreCase));
    }

    private static bool StartsQueryParameter(ReadOnlySpan<char> queryGroup, int startIndex)
    {
        var remaining = queryGroup[startIndex..];
        var endIndex = remaining.IndexOf(';');
        if (endIndex >= 0)
        {
            remaining = remaining[..endIndex];
        }

        var equalsIndex = remaining.IndexOf('=');
        return equalsIndex > 0;
    }

    private static bool IsSensitiveQueryKey(string? key)
    {
        if (key is null)
        {
            return false;
        }

        var normalizedKey = key.Replace('-', '_');
        return s_sensitiveQueryKeys.Contains(normalizedKey) ||
            normalizedKey.EndsWith("_token", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("Token", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("_credential", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("Credential", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("_password", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("Password", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("_key", StringComparison.OrdinalIgnoreCase) ||
            HasSensitiveCamelCaseSuffix(normalizedKey, "Key") ||
            normalizedKey.EndsWith("_secret", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("Secret", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("_sig", StringComparison.OrdinalIgnoreCase) ||
            HasSensitiveCamelCaseSuffix(normalizedKey, "Sig") ||
            normalizedKey.EndsWith("_signature", StringComparison.OrdinalIgnoreCase) ||
            normalizedKey.EndsWith("Signature", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSensitiveCamelCaseSuffix(string key, string suffix)
    {
        if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffixStart = key.Length - suffix.Length;
        return suffixStart == 0 || key[suffixStart - 1] == '_' || char.IsUpper(key[suffixStart]);
    }

    private static bool IsLocalhostTld(string host)
    {
        return host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
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
