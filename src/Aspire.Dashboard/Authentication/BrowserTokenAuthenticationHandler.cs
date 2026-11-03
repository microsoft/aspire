// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Authentication;

internal sealed class BrowserTokenAuthenticationHandler(
    IOptionsMonitor<BrowserTokenAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<BrowserTokenAuthenticationOptions>(options, logger, encoder), IAuthenticationRequestHandler
{
    public async Task<bool> HandleRequestAsync()
    {
        if (Request.Path.Equals(Options.LoginPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!Options.Enabled)
            {
                RedirectAfterValidation();
                return true;
            }

            if (Request.Query.TryGetValue(Options.TokenParameter, out var value))
            {
                if (await TrySignInAsync(value.ToString()).ConfigureAwait(false))
                {
                    RedirectAfterValidation();
                }
                else
                {
                    // A failed /login?t=...&returnUrl=... must not leave the bad token in
                    // the URL when showing the manual login form. Preserve other query values.
                    var query = HttpUtility.ParseQueryString(Request.QueryString.ToString());
                    query.Remove(Options.TokenParameter);
                    var queryString = query.ToString();
                    Response.Redirect($"{Request.PathBase}{Request.Path}{(string.IsNullOrEmpty(queryString) ? "" : "?" + queryString)}");
                }

                return true;
            }
        }

        if (Options.Enabled &&
            HttpMethods.IsPost(Request.Method) &&
            Request.Path.Equals(Options.TokenValidationPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!Request.HasJsonContentType())
            {
                Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return true;
            }

            BrowserTokenLoginRequest? request;
            try
            {
                request = await Request.ReadFromJsonAsync(BrowserTokenAuthenticationJsonSerializerContext.Default.BrowserTokenLoginRequest, Context.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                Logger.LogDebug(ex, "Invalid browser token login request.");
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return true;
            }

            if (request is null)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return true;
            }

            var success = await TrySignInAsync(request.Token).ConfigureAwait(false);
            await Response.WriteAsJsonAsync(success, BrowserTokenAuthenticationJsonSerializerContext.Default.Boolean, contentType: null, cancellationToken: Context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        return Context.AuthenticateAsync(Options.SignInScheme);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = properties.RedirectUri;
        if (string.IsNullOrEmpty(returnUrl))
        {
            returnUrl = OriginalPathBase + OriginalPath + Request.QueryString;
        }

        var loginUrl = BuildRedirectUri(QueryHelpers.AddQueryString(Options.LoginPath.ToString(), Options.ReturnUrlParameter, returnUrl));
        // Match cookie challenges for AJAX requests rather than returning login HTML.
        if (Request.Headers.XRequestedWith == "XMLHttpRequest" || Request.Query["X-Requested-With"] == "XMLHttpRequest")
        {
            Response.Headers.Location = loginUrl;
            Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            Response.Redirect(loginUrl);
        }

        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        return Context.ForbidAsync(Options.SignInScheme, properties);
    }

    private async Task<bool> TrySignInAsync(string incomingBrowserToken)
    {
        if (string.IsNullOrEmpty(incomingBrowserToken) ||
            Options.TokenBytes is not { } expectedTokenBytes ||
            !CryptographicOperations.FixedTimeEquals(expectedTokenBytes, Encoding.UTF8.GetBytes(incomingBrowserToken)))
        {
            return false;
        }

        var identity = new ClaimsIdentity(Options.Claims, authenticationType: Options.SignInScheme);

        await Context.SignInAsync(
            Options.SignInScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = Options.IsPersistent }).ConfigureAwait(false);
        return true;
    }

    private void RedirectAfterValidation()
    {
        // Only local return URLs are allowed to prevent open redirects after login.
        var returnUrl = Request.Query[Options.ReturnUrlParameter].ToString();
        Response.Redirect(Uri.TryCreate(returnUrl, UriKind.Relative, out _) && RedirectHttpResult.IsLocalUrl(returnUrl)
            ? returnUrl
            : Request.PathBase + Options.DefaultRedirectPath);
    }
}
