// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Authentication.Connection;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Middleware adds headers related to browser security that aren't built into ASP.NET Core:
/// - Content-Security-Policy
/// - Referrer-Policy
/// - X-Content-Type-Options
/// </summary>
internal sealed class BrowserSecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _cspContentHttp;
    private readonly string _cspContentHttps;

    public BrowserSecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        _next = next;

        _cspContentHttp = GenerateCspContent(environment, isHttps: false);
        _cspContentHttps = GenerateCspContent(environment, isHttps: true);
    }

    private static string GenerateCspContent(IHostEnvironment environment, bool isHttps)
    {
        // Based on Blazor documentation recommendations:
        // https://learn.microsoft.com/aspnet/core/blazor/security/content-security-policy#server-side-blazor-apps
        // Changes:
        // - style-src adds inline styles as they're used extensively by Blazor FluentUI.
        // - frame-src allows https/http sources because custom pages registered via IFramePageOptions
        //   need to embed external content in iframes. Since Blazor is an SPA, the CSP is set once on
        //   the initial document load and can't be changed per-route during client-side navigation.
        //   Preventing the dashboard itself from being embedded is handled by frame-ancestors, not frame-src.
        var content = "base-uri 'self'; " +
            "object-src 'none'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "frame-src https: http:;";

        if (isHttps)
        {
            // Only allow https images when the site is served over https.
            content += " img-src data: https:;";
        }
        else
        {
            content += " img-src data: http: https:;";
        }

        // default-src limits where content can fetched from.
        // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Security-Policy/default-src
        // This value stops BrowserLink and automatic hot reload from working in development.
        // Add this value for all non-development environments.
        if (!environment.IsDevelopment())
        {
            content += " default-src 'self';";
        }

        return content;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Don't set browser security headers on non-frontend requests.
        var feature = context.Features.Get<IConnectionTypeFeature>();
        if (feature == null || feature.ConnectionTypes.Contains(ConnectionType.Frontend))
        {
            context.Response.Headers.ContentSecurityPolicy = context.Request.IsHttps
                ? _cspContentHttps
                : _cspContentHttp;

            // Recommended best practice value: https://web.dev/articles/referrer-best-practices
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            context.Response.Headers.XContentTypeOptions = "nosniff";
        }

        return _next(context);
    }
}
