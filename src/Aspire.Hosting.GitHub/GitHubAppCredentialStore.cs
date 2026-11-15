// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.GitHub;

/// <summary>
/// The credentials and identity of a GitHub App that was registered from a manifest.
/// </summary>
internal sealed record GitHubAppCredentials(
    string AppId,
    string ClientId,
    string PrivateKey,
    string? ClientSecret,
    string? WebhookSecret,
    string? Slug,
    string? Organization);

/// <summary>
/// Saves the credentials of apps registered from a manifest to the AppHost's user secrets, and reads them back from
/// configuration on later runs.
/// </summary>
/// <remarks>
/// <para>
/// User secrets are keyed by the AppHost project's <c>UserSecretsId</c>, which every git worktree of a repository
/// shares. Each worktree registers its own app, so the keys include the hash of the AppHost's path, for example
/// <c>GitHub:Apps:{pathSha256}:{resourceName}:AppId</c>, to keep one worktree from using another's app.
/// </para>
/// <para>
/// User secrets are loaded into the AppHost's configuration at startup, so a later run finds the values there.
/// </para>
/// </remarks>
internal sealed class GitHubAppCredentialStore(IConfiguration configuration, IUserSecretsManager userSecrets, string resourceName)
{
    // Set by DistributedApplicationBuilder from the AppHost's path, so it differs between worktrees.
    private const string AppHostPathShaKey = "AppHost:PathSha256";

    private const string AppIdKey = "AppId";
    private const string ClientIdKey = "ClientId";
    private const string PrivateKeyKey = "PrivateKey";
    private const string ClientSecretKey = "ClientSecret";
    private const string WebhookSecretKey = "WebhookSecret";
    private const string SlugKey = "Slug";
    private const string OrganizationKey = "Organization";

    private readonly string _prefix = GetPrefix(configuration, resourceName);

    public bool CanSave => userSecrets.IsAvailable;

    public string FilePath => userSecrets.FilePath;

    internal static string GetPrefix(IConfiguration configuration, string resourceName)
    {
        var pathSha = configuration[AppHostPathShaKey];
        return string.IsNullOrEmpty(pathSha)
            ? $"GitHub:Apps:{resourceName}"
            : $"GitHub:Apps:{pathSha}:{resourceName}";
    }

    /// <summary>
    /// Gets the configuration key that holds one of the app's values, such as <c>AppId</c>.
    /// </summary>
    internal string GetKey(string name) => $"{_prefix}:{name}";

    /// <summary>
    /// Reads the saved credentials, or returns <see langword="null"/> when the app hasn't been registered.
    /// </summary>
    public GitHubAppCredentials? Load()
    {
        var appId = configuration[GetKey(AppIdKey)];
        var clientId = configuration[GetKey(ClientIdKey)];
        var privateKey = configuration[GetKey(PrivateKeyKey)];

        if (string.IsNullOrEmpty(appId) && string.IsNullOrEmpty(clientId) && string.IsNullOrEmpty(privateKey))
        {
            return null;
        }

        // Partially saved credentials are returned as they are, so that validation reports the missing values rather
        // than silently registering a second app.
        return new GitHubAppCredentials(
            appId ?? string.Empty,
            clientId ?? string.Empty,
            privateKey ?? string.Empty,
            NullIfEmpty(configuration[GetKey(ClientSecretKey)]),
            NullIfEmpty(configuration[GetKey(WebhookSecretKey)]),
            NullIfEmpty(configuration[GetKey(SlugKey)]),
            NullIfEmpty(configuration[GetKey(OrganizationKey)]));
    }

    /// <summary>
    /// Saves the credentials, returning <see langword="false"/> when any value couldn't be saved.
    /// </summary>
    public bool Save(GitHubAppCredentials credentials)
    {
        var saved = true;
        saved &= Set(AppIdKey, credentials.AppId);
        saved &= Set(ClientIdKey, credentials.ClientId);
        saved &= Set(PrivateKeyKey, credentials.PrivateKey);
        saved &= Set(ClientSecretKey, credentials.ClientSecret);
        saved &= Set(WebhookSecretKey, credentials.WebhookSecret);
        saved &= Set(SlugKey, credentials.Slug);
        saved &= Set(OrganizationKey, credentials.Organization);
        return saved;
    }

    private bool Set(string name, string? value)
    {
        return value is null || userSecrets.TrySetSecret(GetKey(name), value);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
