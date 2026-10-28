// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

internal sealed class DashboardExperienceRedirectMiddleware(
    RequestDelegate next,
    IOptionsMonitor<DashboardOptions> options)
{
    private const string ExperienceCookieName = "Aspire_Dashboard_Experience";

    public Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true))
        {
            return next(context);
        }

        var path = context.Request.Path.Value ?? "/";
        if (IsExcludedPath(path))
        {
            return next(context);
        }

        var isLegacyPath = string.Equals(path, "/legacy", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/legacy/", StringComparison.OrdinalIgnoreCase);
        var cookieValue = context.Request.Cookies[ExperienceCookieName];
        var experience = options.CurrentValue.UI.DisableModernDashboard == true
            ? "legacy"
            : cookieValue is "legacy" or "modern"
                ? cookieValue
                : "modern";

        if (experience == "legacy" && !isLegacyPath)
        {
            // An explicit empty fragment prevents browsers from carrying the source URL's fragment across
            // the HTTP redirect. The browser normalizes the final URL to /legacy.
            context.Response.Redirect("/legacy#");
            return Task.CompletedTask;
        }

        if (experience == "modern" && isLegacyPath)
        {
            context.Response.Redirect("/#");
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsExcludedPath(string path) =>
        string.Equals(path, "/login", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/error", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/terminal-window", StringComparison.OrdinalIgnoreCase);
}
