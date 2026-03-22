// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Mcp;

public class McpApiKeyAuthenticationHandler : AuthenticationHandler<McpApiKeyAuthenticationHandlerOptions>
{
    public const string PolicyName = "McpPolicy";
    public const string McpClaimName = "McpClaim";

    public const string AuthenticationScheme = "McpApiKey";

    /// <summary>
    /// Legacy MCP-specific API key header (for backward compatibility).
    /// </summary>
    public const string McpApiKeyHeaderName = "x-mcp-api-key";

    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;

    public McpApiKeyAuthenticationHandler(IOptionsMonitor<DashboardOptions> dashboardOptions, IOptionsMonitor<McpApiKeyAuthenticationHandlerOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder)
    {
        _dashboardOptions = dashboardOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = _dashboardOptions.CurrentValue.Mcp;

        // Try the new x-api-key header first, then fall back to legacy x-mcp-api-key
        string? headerName = null;
        Microsoft.Extensions.Primitives.StringValues apiKey;

        if (Context.Request.Headers.TryGetValue(ApiAuthenticationHandler.ApiKeyHeaderName, out apiKey))
        {
            headerName = ApiAuthenticationHandler.ApiKeyHeaderName;
        }
        else if (Context.Request.Headers.TryGetValue(McpApiKeyHeaderName, out apiKey))
        {
            headerName = McpApiKeyHeaderName;
        }

        if (headerName is not null)
        {
            // There must be only one header with the API key.
            if (apiKey.Count != 1)
            {
                return Task.FromResult(AuthenticateResult.Fail($"Multiple '{headerName}' headers in request."));
            }

            if (!CompareHelpers.CompareKey(options.GetPrimaryApiKeyBytes(), apiKey.ToString()))
            {
                if (options.GetSecondaryApiKeyBytes() is not { } secondaryBytes || !CompareHelpers.CompareKey(secondaryBytes, apiKey.ToString()))
                {
                    return Task.FromResult(AuthenticateResult.Fail($"Incoming API key from '{headerName}' header doesn't match configured API key."));
                }
            }
        }
        else
        {
            return Task.FromResult(AuthenticateResult.Fail($"API key header is missing. Use '{ApiAuthenticationHandler.ApiKeyHeaderName}' or '{McpApiKeyHeaderName}'."));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

public sealed class McpApiKeyAuthenticationHandlerOptions : AuthenticationSchemeOptions
{
}
