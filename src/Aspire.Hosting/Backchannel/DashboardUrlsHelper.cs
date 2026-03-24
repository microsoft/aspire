// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Devcontainers.Codespaces;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Helper class for retrieving dashboard connection information.
/// </summary>
internal static class DashboardUrlsHelper
{
    private const string McpEndpointName = "mcp";

    /// <summary>
    /// Gets all dashboard connection information in a single call.
    /// Waits for the dashboard to become healthy before returning.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Complete dashboard connection information.</returns>
    public static async Task<DashboardConnectionInfo> GetDashboardConnectionInfoAsync(
        IServiceProvider serviceProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var resourceNotificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();

        // Wait for the dashboard to be healthy
        try
        {
            await resourceNotificationService.WaitForResourceHealthyAsync(
                KnownResourceNames.AspireDashboard,
                WaitBehavior.StopOnResourceUnavailable,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DistributedApplicationException ex)
        {
            logger.LogWarning(ex, "An error occurred while waiting for the Aspire Dashboard to become healthy.");
            return DashboardConnectionInfo.Unhealthy;
        }

        var dashboardOptions = serviceProvider.GetService<IOptions<DashboardOptions>>()?.Value;
        if (dashboardOptions is null)
        {
            logger.LogWarning("Dashboard options not found.");
            return DashboardConnectionInfo.Unhealthy;
        }

        // Find the dashboard resource and get all endpoints
        var appModel = serviceProvider.GetService<DistributedApplicationModel>();
        var dashboardResource = appModel?.Resources.SingleOrDefault(
            r => string.Equals(r.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName)) as IResourceWithEndpoints;

        string? apiBaseUrl = null;
        string? mcpBaseUrl = null;

        if (dashboardResource is not null)
        {
            // API endpoint (https or http) - used for Dashboard UI and Telemetry API
            var httpsEndpoint = dashboardResource.GetEndpoint("https");
            var httpEndpoint = dashboardResource.GetEndpoint("http");
            var apiEndpoint = httpsEndpoint.Exists ? httpsEndpoint : httpEndpoint;
            if (apiEndpoint.Exists)
            {
                apiBaseUrl = await EndpointHostHelpers.GetUrlWithTargetHostAsync(apiEndpoint, cancellationToken).ConfigureAwait(false);
            }

            // MCP endpoint
            var mcpEndpoint = dashboardResource.GetEndpoint(McpEndpointName);
            if (mcpEndpoint.Exists)
            {
                var mcpUrl = await mcpEndpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(mcpUrl))
                {
                    mcpBaseUrl = $"{mcpUrl}/mcp";
                }
            }
        }

        // Fall back to configured URL if we couldn't get it from the resource
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            if (StringUtils.TryGetUriFromDelimitedString(dashboardOptions.DashboardUrl, ";", out var dashboardUri))
            {
                apiBaseUrl = dashboardUri.GetLeftPart(UriPartial.Authority);
            }
        }

        // Build login URLs
        var codespacesUrlRewriter = serviceProvider.GetService<CodespacesUrlRewriter>();
        string? baseUrlWithLoginToken = null;
        string? codespacesUrlWithLoginToken = null;

        if (!string.IsNullOrEmpty(apiBaseUrl) && !string.IsNullOrEmpty(dashboardOptions.DashboardToken))
        {
            baseUrlWithLoginToken = $"{apiBaseUrl.TrimEnd('/')}/login?t={dashboardOptions.DashboardToken}";
            var rewrittenUrl = codespacesUrlRewriter?.RewriteUrl(baseUrlWithLoginToken);
            if (rewrittenUrl != baseUrlWithLoginToken)
            {
                codespacesUrlWithLoginToken = rewrittenUrl;
            }
        }

        return new DashboardConnectionInfo
        {
            IsHealthy = true,
            ApiBaseUrl = apiBaseUrl,
            ApiToken = dashboardOptions.ApiKey,
            McpBaseUrl = mcpBaseUrl,
            McpApiToken = dashboardOptions.McpApiKey,
            BaseUrlWithLoginToken = baseUrlWithLoginToken,
            CodespacesUrlWithLoginToken = codespacesUrlWithLoginToken
        };
    }

    /// <summary>
    /// Gets the dashboard URLs including the login token.
    /// Waits for the dashboard to become healthy before returning.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The Dashboard URLs state including health and login URLs.</returns>
    public static async Task<DashboardUrlsState> GetDashboardUrlsAsync(
        IServiceProvider serviceProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var info = await GetDashboardConnectionInfoAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false);
        return new DashboardUrlsState
        {
            DashboardHealthy = info.IsHealthy,
            BaseUrlWithLoginToken = info.BaseUrlWithLoginToken,
            CodespacesUrlWithLoginToken = info.CodespacesUrlWithLoginToken
        };
    }
}

/// <summary>
/// Contains all dashboard connection information.
/// </summary>
internal sealed class DashboardConnectionInfo
{
    public static readonly DashboardConnectionInfo Unhealthy = new() { IsHealthy = false };

    public bool IsHealthy { get; init; }
    public string? ApiBaseUrl { get; init; }
    public string? ApiToken { get; init; }
    public string? McpBaseUrl { get; init; }
    public string? McpApiToken { get; init; }
    public string? BaseUrlWithLoginToken { get; init; }
    public string? CodespacesUrlWithLoginToken { get; init; }
}
