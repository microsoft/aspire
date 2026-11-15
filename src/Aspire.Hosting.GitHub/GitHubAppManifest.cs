// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHub;

/// <summary>
/// Creates the JSON manifest that describes a GitHub App to GitHub's registration page.
/// </summary>
/// <remarks>
/// The manifest format is documented at
/// https://docs.github.com/apps/sharing-github-apps/registering-a-github-app-from-a-manifest#github-app-manifest-parameters.
/// </remarks>
internal static class GitHubAppManifest
{
    /// <summary>
    /// Creates the manifest JSON.
    /// </summary>
    /// <param name="settings">The app to describe.</param>
    /// <param name="redirectUrl">
    /// The URL that GitHub redirects to with a temporary code once the user creates the app. This only completes the
    /// registration, and is a different setting from the app's OAuth callback URLs.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal static async Task<string> CreateJsonAsync(GitHubAppManifestSettings settings, string redirectUrl, CancellationToken cancellationToken)
    {
        // Callback URLs can reference endpoints, which only have values once they are allocated.
        var callbackUrls = new List<string>(settings.CallbackUrls.Count);
        foreach (var callbackUrl in settings.CallbackUrls)
        {
            var value = await callbackUrl.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"The callback URL '{callbackUrl.ValueExpression}' has no value.");
            }

            callbackUrls.Add(value);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", settings.AppName);
            writer.WriteString("url", settings.HomepageUrl);
            writer.WriteString("description", settings.Description);
            writer.WriteString("redirect_url", redirectUrl);
            writer.WriteBoolean("public", false);

            if (callbackUrls.Count > 0)
            {
                writer.WriteStartArray("callback_urls");
                foreach (var callbackUrl in callbackUrls)
                {
                    writer.WriteStringValue(callbackUrl);
                }
                writer.WriteEndArray();
            }

            // Without hook_attributes GitHub creates the app with no webhook, so it needs no publicly reachable URL.
            writer.WriteStartObject("default_permissions");
            foreach (var (permission, level) in settings.Permissions)
            {
                writer.WriteString(permission, ToManifestValue(level));
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ToManifestValue(GitHubAppPermissionLevel level) => level switch
    {
        GitHubAppPermissionLevel.Read => "read",
        GitHubAppPermissionLevel.Write => "write",
        GitHubAppPermissionLevel.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown permission level.")
    };
}
