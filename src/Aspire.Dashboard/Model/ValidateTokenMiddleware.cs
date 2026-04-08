// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Web;
using System.Diagnostics.CodeAnalysis;

namespace Aspire.Dashboard.Model;

internal sealed class ValidateTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<DashboardOptions> _options;
    private readonly ILogger<ValidateTokenMiddleware> _logger;

    public ValidateTokenMiddleware(RequestDelegate next, IOptionsMonitor<DashboardOptions> options, ILogger<ValidateTokenMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/login", StringComparisons.UrlPath))
        {
            if (_options.CurrentValue.Frontend.AuthMode != FrontendAuthMode.BrowserToken)
            {
                _logger.LogDebug($"Request to validate token URL but auth mode isn't set to {FrontendAuthMode.BrowserToken}.");

                RedirectAfterValidation(context);
            }
            else if (context.Request.Query.TryGetValue("t", out var value) && _options.CurrentValue.Frontend.AuthMode == FrontendAuthMode.BrowserToken)
            {
                var dashboardOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<DashboardOptions>>();
                if (await TryAuthenticateAsync(value.ToString(), context, dashboardOptions).ConfigureAwait(false))
                {
                    // Success. Redirect to the app.
                    RedirectAfterValidation(context);
                }
                else
                {
                    // Failure.
                    // The bad token in the query string could be confusing with the token in the text box.
                    // Remove it before the presenting the UI to the user.
                    var qs = HttpUtility.ParseQueryString(context.Request.QueryString.ToString());
                    qs.Remove("t");

                    // Collection created by ParseQueryString handles escaping names and values.
                    var newQuerystring = qs.ToString();
                    if (!string.IsNullOrEmpty(newQuerystring))
                    {
                        newQuerystring = "?" + newQuerystring;
                    }
                    context.Response.Redirect($"{context.Request.Path}{newQuerystring}");
                }

                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static void RedirectAfterValidation(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("returnUrl", out var returnUrl)
            && IsLocalUrl(returnUrl.ToString()))
        {
            context.Response.Redirect(returnUrl.ToString());
        }
        else
        {
            context.Response.Redirect(DashboardUrls.ResourcesUrl());
        }
    }

    // Copied from ASP.NET Core's IsLocalUrl implementation:
    // https://github.com/dotnet/aspnetcore/blob/7cbda0e023075490b4365a0754ca410ce6eff59a/src/Shared/ResultsHelpers/SharedUrlHelper.cs#L33
    internal static bool IsLocalUrl([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        // Allows "/" or "/foo" but not "//" or "/\".
        if (url[0] == '/')
        {
            // url is exactly "/"
            if (url.Length == 1)
            {
                return true;
            }

            // url doesn't start with "//" or "/\"
            if (url[1] != '/' && url[1] != '\\')
            {
                return !HasControlCharacter(url.AsSpan(1));
            }

            return false;
        }

        // Allows "~/" or "~/foo" but not "~//" or "~/\".
        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            // url is exactly "~/"
            if (url.Length == 2)
            {
                return true;
            }

            // url doesn't start with "~//" or "~/\"
            if (url[2] != '/' && url[2] != '\\')
            {
                return !HasControlCharacter(url.AsSpan(2));
            }

            return false;
        }

        return false;

        static bool HasControlCharacter(ReadOnlySpan<char> readOnlySpan)
        {
            // URLs may not contain ASCII control characters.
            for (var i = 0; i < readOnlySpan.Length; i++)
            {
                if (char.IsControl(readOnlySpan[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static async Task<bool> TryAuthenticateAsync(string incomingBrowserToken, HttpContext httpContext, IOptionsMonitor<DashboardOptions> dashboardOptions)
    {
        if (string.IsNullOrEmpty(incomingBrowserToken) || dashboardOptions.CurrentValue.Frontend.GetBrowserTokenBytes() is not { } expectedBrowserTokenBytes)
        {
            return false;
        }

        if (!CompareHelpers.CompareKey(expectedBrowserTokenBytes, incomingBrowserToken))
        {
            return false;
        }

        var claimsIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "Local")],
            authenticationType: CookieAuthenticationDefaults.AuthenticationScheme);
        var claims = new ClaimsPrincipal(claimsIdentity);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            claims,
            new AuthenticationProperties { IsPersistent = true }).ConfigureAwait(false);
        return true;
    }
}
