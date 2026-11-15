// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHub;

/// <summary>
/// Describes the GitHub App that <c>AddGitHubApp</c> registers on GitHub from a manifest the first time the AppHost runs.
/// </summary>
internal sealed class GitHubAppManifestSettings
{
    // GitHub allows up to 10 user authorization callback URLs per app.
    // See https://docs.github.com/apps/creating-github-apps/registering-a-github-app/about-the-user-authorization-callback-url.
    internal const int MaxCallbackUrls = 10;

    /// <summary>
    /// Gets or sets the app name suggested on GitHub's registration page, where the user can change it.
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// Gets the description shown on the app's public page.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the homepage URL, which GitHub requires every app to have.
    /// </summary>
    public string HomepageUrl { get; init; } = "https://aspire.dev";

    /// <summary>
    /// Gets the login of the organization to register the app under, or <see langword="null"/> to register it under the
    /// signed-in user's personal account.
    /// </summary>
    public string? Organization { get; init; }

    /// <summary>
    /// Gets the URLs that GitHub redirects users to after they authorize the app. These are the app's OAuth callbacks,
    /// unrelated to the URL that GitHub redirects to when the registration finishes.
    /// </summary>
    public List<ReferenceExpression> CallbackUrls { get; } = [];

    /// <summary>
    /// Gets the repository, organization and account permissions that the app requests, keyed by GitHub's name for the
    /// permission, such as <c>issues</c>.
    /// </summary>
    public SortedDictionary<string, GitHubAppPermissionLevel> Permissions { get; } = new(StringComparer.Ordinal);
}
