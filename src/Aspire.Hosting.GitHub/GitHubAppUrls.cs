// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.GitHub;

/// <summary>
/// Builds links to a GitHub App's pages on github.com.
/// </summary>
internal static class GitHubAppUrls
{
    // The app's settings page lives under its owner:
    //   https://github.com/settings/apps/{slug}                          (personal account)
    //   https://github.com/organizations/{organization}/settings/apps/{slug}
    // Without a slug, link the signed-in user's list of apps, which is the closest page that needs no lookup.
    internal static string GetSettingsUrl(string? slug, string? organization) => (slug, organization) switch
    {
        (null, _) => "https://github.com/settings/apps",
        (_, null) => $"https://github.com/settings/apps/{slug}",
        _ => $"https://github.com/organizations/{organization}/settings/apps/{slug}"
    };

    internal static string? GetPublicUrl(string? slug) => slug is null ? null : $"https://github.com/apps/{slug}";
}
