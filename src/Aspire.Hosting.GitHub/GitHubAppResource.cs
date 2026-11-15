// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a GitHub App that resources in the application model can authenticate as.
/// </summary>
/// <remarks>
/// <para>
/// A GitHub App is identified by its numeric app ID and its client ID, and authenticates with a private key that
/// signs the JSON Web Tokens it exchanges for installation access tokens. A client secret is needed only when the
/// app signs users in, and a webhook secret only when the app receives webhooks.
/// </para>
/// <para>
/// Reference the app from another resource with <c>WithReference</c> to give that resource the app's credentials as
/// environment variables.
/// </para>
/// </remarks>
public sealed class GitHubAppResource : Resource
{
    internal const string DefaultApiUrl = "https://api.github.com";

    internal GitHubAppResource(
        string name,
        ParameterResource appId,
        ParameterResource clientId,
        ParameterResource privateKey,
        ParameterResource? clientSecret,
        ParameterResource? webhookSecret)
        : base(name)
    {
        AppIdParameter = appId;
        ClientIdParameter = clientId;
        PrivateKeyParameter = privateKey;
        ClientSecretParameter = clientSecret;
        WebhookSecretParameter = webhookSecret;
    }

    /// <summary>
    /// Gets the parameter that contains the app's numeric ID, shown as "App ID" on the app's settings page.
    /// </summary>
    public ParameterResource AppIdParameter { get; }

    /// <summary>
    /// Gets the parameter that contains the app's client ID, shown as "Client ID" on the app's settings page.
    /// </summary>
    public ParameterResource ClientIdParameter { get; }

    /// <summary>
    /// Gets the parameter that contains the PEM-encoded private key that the app signs JSON Web Tokens with.
    /// </summary>
    public ParameterResource PrivateKeyParameter { get; }

    /// <summary>
    /// Gets the parameter that contains the client secret used when the app signs users in, or <see langword="null"/>
    /// when the app doesn't have one.
    /// </summary>
    public ParameterResource? ClientSecretParameter { get; }

    /// <summary>
    /// Gets the parameter that contains the secret that GitHub signs webhook deliveries with, or <see langword="null"/>
    /// when the app doesn't receive webhooks.
    /// </summary>
    public ParameterResource? WebhookSecretParameter { get; }

    /// <summary>
    /// Gets the base URL of the GitHub REST API that the app's tokens are used with.
    /// </summary>
    public string ApiUrl { get; } = DefaultApiUrl;

    /// <summary>
    /// Gets the app's URL-friendly name (its slug), or <see langword="null"/> when it isn't known.
    /// </summary>
    /// <remarks>
    /// The slug is the last segment of the app's public page, <c>https://github.com/apps/{slug}</c>.
    /// </remarks>
    public string? Slug { get; internal set; }

    /// <summary>
    /// Gets the login of the organization that owns the app, or <see langword="null"/> when a personal account owns it
    /// or the owner isn't known.
    /// </summary>
    public string? Organization { get; internal set; }

    /// <summary>
    /// Gets or sets the description of the app that is registered on GitHub when the AppHost first runs, or
    /// <see langword="null"/> when the app already exists or the AppHost is publishing.
    /// </summary>
    internal GitHub.GitHubAppManifestSettings? Manifest { get; set; }
}
