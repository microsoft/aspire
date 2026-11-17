// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Legacy.Layout;

/// <summary>
/// Maps the dashboard's page URLs to their routes in the previous experience, which are the same routes below /legacy.
/// The legacy layout also rewrites unprefixed navigations, but the menus need the real URL so the current page is
/// marked active: NavLink and the mobile menu's patterns compare against the browser's URL.
/// </summary>
internal static class LegacyNavUrls
{
    public const string BasePath = "/legacy";

    /// <summary>
    /// "/" -> "/legacy", "/?resource=api" -> "/legacy?resource=api", "/traces" -> "/legacy/traces".
    /// </summary>
    public static string ToLegacyUrl(string url) =>
        url.StartsWith('/') && (url.Length == 1 || url[1] == '?') ? BasePath + url[1..] : BasePath + url;

    /// <summary>
    /// Gets whether the legacy layout should rewrite a navigation to a base-relative path, for example "traces?x=1" or
    /// "api/set-language?language=fr&amp;redirectUrl=%2Flegacy", to the same route below /legacy.
    /// </summary>
    /// <remarks>
    /// Only Blazor pages that both experiences have live below /legacy. Paths that are already legacy routes, pages
    /// that only exist once (login, error, terminal-window), and server endpoints must keep their URL. Server
    /// endpoints such as /api/set-language are reached with a forced page load, so rewriting them to
    /// /legacy/api/... ends on the not found page. Keep the excluded paths in sync with
    /// DashboardExperienceRedirectMiddleware, which skips the same server paths.
    /// </remarks>
    public static bool ShouldRewriteToLegacy(string relativePath) =>
        !relativePath.StartsWith("legacy", StringComparison.OrdinalIgnoreCase) &&
        !relativePath.StartsWith("login", StringComparison.OrdinalIgnoreCase) &&
        !relativePath.StartsWith("error", StringComparison.OrdinalIgnoreCase) &&
        !relativePath.StartsWith("terminal-window", StringComparison.OrdinalIgnoreCase) &&
        !relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase) &&
        !relativePath.StartsWith('_');

    /// <summary>
    /// Gets whether a base-relative path without query is the previous experience's resources page. The legacy layout
    /// navigates to "legacy/" when it rewrites "/", and "legacy/resources" is the page's second route.
    /// </summary>
    public static bool IsResourcesPath(string trimmedPath) =>
        trimmedPath.TrimEnd('/') is var path &&
        (string.Equals(path, "legacy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "legacy/resources", StringComparison.OrdinalIgnoreCase));
}
