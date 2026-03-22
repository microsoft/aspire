// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Aspire.Dashboard.Authentication;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Api;

/// <summary>
/// Authentication handler for the Dashboard API that supports API key auth
/// and falls back to frontend auth (browser token, OIDC, or unsecured based on configuration).
/// </summary>
/// <remarks>
/// When Api.AuthMode is ApiKey, the API key is required for programmatic access.
/// Browser-based access can still use frontend auth (browser token, OIDC).
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

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var currentOptions = dashboardOptions.CurrentValue;
        var apiAuthMode = currentOptions.Api.AuthMode;

        // If API auth is unsecured, allow access
        if (apiAuthMode is ApiAuthMode.Unsecured)
        {
            var id = new ClaimsIdentity([new Claim(ClaimName, bool.TrueString)], AuthenticationScheme);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
        }

        // If API auth requires API key
        if (apiAuthMode is ApiAuthMode.ApiKey)
        {
            var apiKeyBytes = currentOptions.Api.GetPrimaryApiKeyBytesOrNull();

            // If ApiKey mode is set but no key is configured, fail authentication
            // rather than silently falling through to frontend auth
            if (apiKeyBytes is null)
            {
                return AuthenticateResult.Fail("API key authentication is enabled but no API key is configured.");
            }

            if (Context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
            {
                // There must be exactly one header with the API key.
                if (apiKeyHeader.Count != 1)
                {
                    return AuthenticateResult.Fail("Invalid API key header.");
                }

                var providedApiKey = apiKeyHeader.ToString();
                if (string.IsNullOrEmpty(providedApiKey))
                {
                    return AuthenticateResult.Fail("Invalid API key header.");
                }

                // Check primary key
                if (CompareHelpers.CompareKey(apiKeyBytes, providedApiKey))
                {
                    var id = new ClaimsIdentity([new Claim(ClaimName, bool.TrueString)], AuthenticationScheme);
                    return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
                }

                // Check secondary key (for key rotation)
                if (currentOptions.Api.GetSecondaryApiKeyBytes() is { } secondaryBytes &&
                    CompareHelpers.CompareKey(secondaryBytes, providedApiKey))
                {
                    var id = new ClaimsIdentity([new Claim(ClaimName, bool.TrueString)], AuthenticationScheme);
                    return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), Scheme.Name));
                }

                return AuthenticateResult.Fail("Authentication failed.");
            }

            // API key header not provided - fall through to frontend auth for browser access
        }

        // Try frontend authentication (for browser-based access)
        var frontendResult = await Context.AuthenticateAsync(FrontendCompositeAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (frontendResult.Succeeded)
        {
            return frontendResult;
        }

        // Return frontend failure if present
        if (frontendResult.Failure is not null)
        {
            return frontendResult;
        }

        return AuthenticateResult.NoResult();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // If an API key was provided (but was wrong), return 401 instead of redirecting
        if (Context.Request.Headers.ContainsKey(ApiKeyHeaderName))
        {
            Context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        // For browser-based access without API key, redirect to login
        var frontendAuthMode = dashboardOptions.CurrentValue.Frontend.AuthMode;
        var scheme = frontendAuthMode switch
        {
            FrontendAuthMode.OpenIdConnect => FrontendAuthenticationDefaults.AuthenticationSchemeOpenIdConnect,
            FrontendAuthMode.BrowserToken => FrontendAuthenticationDefaults.AuthenticationSchemeBrowserToken,
            _ => null
        };

        if (scheme != null)
        {
            return Context.ChallengeAsync(scheme);
        }

        Context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public const string AuthenticationScheme = "Api";
    public const string PolicyName = "ApiPolicy";
    public const string ClaimName = "api";
}

public sealed class ApiAuthenticationHandlerOptions : AuthenticationSchemeOptions
{
}
