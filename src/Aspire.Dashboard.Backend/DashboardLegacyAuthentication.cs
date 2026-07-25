// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Backend;

internal static class DashboardLegacyAuthentication
{
    public static IApplicationBuilder UseDashboardLegacyAuthentication(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var proxy = context.RequestServices.GetRequiredService<IDashboardLegacyApiProxy>();
            if (proxy.IsConfigured && IsOpenIdConnectResponse(context.Request))
            {
                // The legacy dashboard deliberately uses "/" as its OIDC callback path. Let the
                // authority process only callback-shaped root requests; ordinary SPA navigation
                // is still authorized first and then served by the AOT host.
                await proxy.ProxyAsync(
                    context,
                    $"{context.Request.Path}{context.Request.QueryString}").ConfigureAwait(false);
                return;
            }

            if (!proxy.IsConfigured || IsAnonymousOrDelegatedPath(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // Authentication is intentionally validated by the existing dashboard instead of
            // duplicating its BrowserToken or OIDC cookie authority. The browser cookie is scoped
            // to the shared hostname (not the port), and the proxy preserves the public AOT Host,
            // so both processes participate in one identity session during convergence.
            if (await proxy.AuthorizeAsync(context).ConfigureAwait(false))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    private static bool IsOpenIdConnectResponse(HttpRequest request)
    {
        return request.Path.Equals("/")
            && request.Query.ContainsKey("state")
            && (request.Query.ContainsKey("code") || request.Query.ContainsKey("error"));
    }

    private static bool IsAnonymousOrDelegatedPath(PathString path)
    {
        if (path.Equals(DashboardApiContract.DiscoveryPath)
            || path.Equals("/login")
            || path.Equals("/api/validatetoken")
            || path.StartsWithSegments("/authentication")
            || path.StartsWithSegments("/assets")
            || path.StartsWithSegments("/Components")
            || path.StartsWithSegments("/js")
            || path.StartsWithSegments("/fonts")
            || path.Equals("/favicon.ico"))
        {
            return true;
        }

        // These versioned routes proxy directly to legacy endpoints that already own their
        // authorization and challenge behavior. Preflighting them would perform the operation
        // twice or consume a streaming request body before it reaches its actual handler.
        return path.Equals(DashboardApiContract.ShellPath)
            || path.Equals(DashboardApiContract.CulturePath)
            || path.Equals(DashboardApiContract.AuthenticationLogoutPath)
            || path.StartsWithSegments(DashboardApiContract.ManageDataPath);
    }
}
