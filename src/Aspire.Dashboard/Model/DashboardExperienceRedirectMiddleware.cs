// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model;

internal sealed class DashboardExperienceRedirectMiddleware(
    RequestDelegate next,
    IOptionsMonitor<DashboardOptions> options,
    DashboardExperienceAvailability availability)
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
        var experience = availability.LegacyAvailable && options.CurrentValue.UI.DisableModernDashboard == true
            ? "legacy"
            : availability.LegacyAvailable && cookieValue is "legacy"
                ? "legacy"
                : cookieValue == "modern"
                ? cookieValue
                : "modern";

        // An explicit empty fragment prevents browsers from carrying the source URL's fragment across
        // the HTTP redirect. The browser normalizes the final URL to the URL without '#'. The redirect uses the
        // escaped path so resource names with reserved characters survive it.
        var escapedPath = context.Request.Path.ToUriComponent();
        if (experience == "legacy" && !isLegacyPath)
        {
            context.Response.Redirect($"{context.Request.PathBase.ToUriComponent()}{GetLegacyUrl(escapedPath, context.Request.QueryString.Value)}#");
            return Task.CompletedTask;
        }

        if (experience == "modern" && isLegacyPath)
        {
            context.Response.Redirect($"{context.Request.PathBase.ToUriComponent()}{GetModernUrl(escapedPath, context.Request.QueryString.Value)}#");
            return Task.CompletedTask;
        }

        return next(context);
    }

    // Pages that both experiences have, with the same route and query string below the experience's root, for example
    // /traces/detail/{traceId} and /legacy/traces/detail/{traceId}. Links to them, such as links from the CLI or a
    // shared trace link, keep working in either experience. Other pages, such as the resources pages, differ between
    // the experiences, so they go to the experience's root.
    // Keep in sync with wwwroot/js/app-dashboard-experience.js.
    private static readonly string[] s_sharedPageSegments = ["consolelogs", "structuredlogs", "traces", "metrics", "terminals"];

    internal static string GetLegacyUrl(string path, string? query)
    {
        if (IsSharedPage(path))
        {
            return $"/legacy{path}{query}";
        }

        // The root is the resources page in the previous experience, and its query selects a resource or the view and
        // filters, for example /?resource=api from the CLI's describe command and MCP tools, or a /?view=Graph bookmark.
        return path is "/" or "" ? $"/legacy{query}" : "/legacy";
    }

    internal static string GetModernUrl(string path, string? query)
    {
        // "/legacy/traces/detail/abc" -> "/traces/detail/abc", "/legacy" -> "".
        var modernPath = path["/legacy".Length..];
        return IsSharedPage(modernPath) ? $"{modernPath}{query}" : "/";
    }

    private static bool IsSharedPage(string path)
    {
        // The first segment of "/traces/detail/abc" is "traces".
        var segment = path.TrimStart('/');
        var separatorIndex = segment.IndexOf('/');
        if (separatorIndex >= 0)
        {
            segment = segment[..separatorIndex];
        }

        return s_sharedPageSegments.Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcludedPath(string path) =>
        string.Equals(path, "/login", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/error", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/terminal-window", StringComparison.OrdinalIgnoreCase);
}

internal sealed record DashboardExperienceAvailability(bool LegacyAvailable);
