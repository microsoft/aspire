// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Api;

/// <summary>
/// Authentication handler for the Dashboard API that supports API key authentication.
/// </summary>
/// <remarks>
/// When Api.AuthMode is ApiKey, all requests must include a valid API key via the x-api-key header.
/// When Api.AuthMode is Unsecured, no authentication is required.
/// </remarks>
public sealed class ApiAuthenticationHandler(
    IOptionsMonitor<DashboardOptions> dashboardOptions,
    IOptionsMonitor<ApiAuthenticationHandlerOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
        : AuthenticationHandler<ApiAuthenticationHandlerOptions>(options, loggerFactory, encoder)
{
    /// <summary>
    /// The header name for the Dashboard API key.
    /// </summary>
    public const string ApiKeyHeaderName = "x-api-key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var currentOptions = dashboardOptions.CurrentValue;
        var apiAuthMode = currentOptions.Api.AuthMode;

        // If API auth is unsecured, allow access
        if (apiAuthMode is ApiAuthMode.Unsecured)
        {
            var id = new ClaimsIdentity([new Claim(ClaimName, bool.TrueString)], AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name)));
        }

        // If API auth requires API key
        if (apiAuthMode is ApiAuthMode.ApiKey)
        {
            var apiKeyBytes = currentOptions.Api.GetPrimaryApiKeyBytesOrNull();

            // If ApiKey mode is set but no key is configured, fail authentication
            if (apiKeyBytes is null)
            {
                return Task.FromResult(AuthenticateResult.Fail("API key authentication is enabled but no API key is configured."));
            }

            if (Context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
            {
                // There must be exactly one header with the API key.
                if (apiKeyHeader.Count != 1)
                {
                    return Task.FromResult(AuthenticateResult.Fail("Invalid API key header."));
                }

                var providedApiKey = apiKeyHeader.ToString();
                if (string.IsNullOrEmpty(providedApiKey))
                {
                    return Task.FromResult(AuthenticateResult.Fail("Invalid API key header."));
                }

                // Check primary key
                if (CompareHelpers.CompareKey(apiKeyBytes, providedApiKey))
                {
                    var id = new ClaimsIdentity([new Claim(ClaimName, bool.TrueString)], AuthenticationScheme);
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name)));
                }

                // Check secondary key (for key rotation)
                if (currentOptions.Api.GetSecondaryApiKeyBytes() is { } secondaryBytes &&
                    CompareHelpers.CompareKey(secondaryBytes, providedApiKey))
                {
                    var id = new ClaimsIdentity([new Claim(ClaimName, bool.TrueString)], AuthenticationScheme);
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name)));
                }

                return Task.FromResult(AuthenticateResult.Fail("Authentication failed."));
            }
            else
            {
                return Task.FromResult(AuthenticateResult.Fail($"API key from '{ApiKeyHeaderName}' header is missing."));
            }
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }

    public const string AuthenticationScheme = "Api";
    public const string PolicyName = "ApiPolicy";
    public const string ClaimName = "api";
}

public sealed class ApiAuthenticationHandlerOptions : AuthenticationSchemeOptions
{
}
