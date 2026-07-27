// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using Aspire.Dashboard.Authentication.Connection;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Authentication;

public sealed class FrontendCompositeAuthenticationHandler(
    IOptionsMonitor<DashboardOptions> dashboardOptions,
    IOptionsMonitor<FrontendCompositeAuthenticationHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
        : AuthenticationHandler<FrontendCompositeAuthenticationHandlerOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var result = await Context.AuthenticateAsync(ConnectionTypeAuthenticationDefaults.AuthenticationSchemeFrontend).ConfigureAwait(false);
        if (result.Failure is not null)
        {
            return AuthenticateResult.Fail(
                result.Failure,
                new AuthenticationProperties(
                    items: new Dictionary<string, string?>(),
                    parameters: new Dictionary<string, object?> { [AspirePolicyEvaluator.SuppressChallengeKey] = true }));
        }

        result = await Context.AuthenticateAsync().ConfigureAwait(false);
        return result;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var scheme = GetRelevantAuthenticationScheme();
        if (scheme != null)
        {
            // Preserve an explicit return location when a reverse-hosted dashboard delegates
            // authentication to this authority. Cookie and OIDC handlers both use these
            // properties to return the browser to its original same-origin route.
            await Context.ChallengeAsync(scheme, properties).ConfigureAwait(false);
        }
    }

    private string? GetRelevantAuthenticationScheme()
    {
        return dashboardOptions.CurrentValue.Frontend.AuthMode switch
        {
            FrontendAuthMode.OpenIdConnect => FrontendAuthenticationDefaults.AuthenticationSchemeOpenIdConnect,
            FrontendAuthMode.BrowserToken => FrontendAuthenticationDefaults.AuthenticationSchemeBrowserToken,
            _ => null
        };
    }
}

public static class FrontendCompositeAuthenticationDefaults
{
    public const string AuthenticationScheme = "FrontendComposite";
}

public sealed class FrontendCompositeAuthenticationHandlerOptions : AuthenticationSchemeOptions
{
}
