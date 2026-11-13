// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Authentication.OtlpApiKey;

public class OtlpApiKeyAuthenticationHandler : AuthenticationHandler<OtlpApiKeyAuthenticationHandlerOptions>
{
    public const string ApiKeyHeaderName = "x-otlp-api-key";

    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;

    public OtlpApiKeyAuthenticationHandler(IOptionsMonitor<DashboardOptions> dashboardOptions, IOptionsMonitor<OtlpApiKeyAuthenticationHandlerOptions> options, ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder)
    {
        _dashboardOptions = dashboardOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = _dashboardOptions.CurrentValue.Otlp;

        if (Context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKey))
        {
            // There must be only one header with the API key.
            if (apiKey.Count != 1)
            {
                return Task.FromResult(AuthenticateResult.Fail($"Multiple '{ApiKeyHeaderName}' headers in request."));
            }

            var providedApiKey = apiKey.ToString();
            if (string.IsNullOrEmpty(providedApiKey))
            {
                return Task.FromResult(AuthenticateResult.Fail($"API key from '{ApiKeyHeaderName}' header is empty."));
            }

            if (!CompareHelpers.CompareKey(options.GetPrimaryApiKeyBytes(), providedApiKey))
            {
                if (options.GetSecondaryApiKeyBytes() is not { } secondaryBytes || !CompareHelpers.CompareKey(secondaryBytes, providedApiKey))
                {
                    return Task.FromResult(AuthenticateResult.Fail($"Incoming API key from '{ApiKeyHeaderName}' header doesn't match configured API key."));
                }
            }
        }
        else
        {
            return Task.FromResult(AuthenticateResult.Fail($"API key from '{ApiKeyHeaderName}' header is missing."));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }
}

public static class OtlpApiKeyAuthenticationDefaults
{
    public const string AuthenticationScheme = "OtlpApiKey";
}

public sealed class OtlpApiKeyAuthenticationHandlerOptions : AuthenticationSchemeOptions
{
}
