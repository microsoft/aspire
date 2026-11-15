// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding GitHub App resources to the application model.
/// </summary>
public static class GitHubAppResourceExtensions
{
    private const string ResourceType = "GitHubApp";
    private const string SettingsUrlName = "Settings";
    private const string PublicPageUrlName = "Public page";
    private const string RegisterUrlName = "Register";
    private const string WaitingForRegistrationState = "Waiting for registration";

    // The configuration section that .NET resources read by default, such as builder.Configuration.GetSection("GitHub:App").
    private const string DefaultDotnetSection = "GitHub__App";

    // The prefix of the variables that every resource receives by default. GitHub's own tooling and many SDK samples
    // read GITHUB_APP_ID, GITHUB_PRIVATE_KEY and similar names.
    private const string DefaultEnvironmentVariablePrefix = "GITHUB";

    /// <summary>
    /// Adds a GitHub App that already exists on GitHub to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appId">A parameter containing the app's numeric App ID.</param>
    /// <param name="clientId">A parameter containing the app's Client ID.</param>
    /// <param name="privateKey">A secret parameter containing the app's PEM-encoded private key.</param>
    /// <param name="clientSecret">
    /// An optional secret parameter containing a client secret, needed when the app signs users in.
    /// </param>
    /// <param name="webhookSecret">
    /// An optional secret parameter containing the secret that GitHub signs webhook deliveries with, needed when the app
    /// receives webhooks.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{GitHubAppResource}"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="privateKey"/>, <paramref name="clientSecret"/> or <paramref name="webhookSecret"/> is not a
    /// secret parameter.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The values are read from GitHub when the app is registered: the App ID and Client ID are shown on the app's settings
    /// page, where you can also generate a private key and a client secret. Nothing is sent to GitHub; when the AppHost
    /// starts, the resource checks that the values are well formed and shows the app in the dashboard as running, or as
    /// failed to start with the problems in its console logs.
    /// </para>
    /// <para>
    /// Reference the app from another resource with
    /// <see cref="WithReference{T}(IResourceBuilder{T}, IResourceBuilder{GitHubAppResource}, string?)"/>.
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var appId = builder.AddParameter("github-app-id");
    /// var clientId = builder.AddParameter("github-client-id");
    /// var privateKey = builder.AddParameter("github-private-key", secret: true);
    ///
    /// var gitHubApp = builder.AddExistingGitHubApp("github-app", appId, clientId, privateKey)
    ///     .WithAppSlug("my-app");
    ///
    /// builder.AddDotnetProject("api", "../Api/Api.csproj")
    ///     .WithReference(gitHubApp);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> AddExistingGitHubApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> appId,
        IResourceBuilder<ParameterResource> clientId,
        IResourceBuilder<ParameterResource> privateKey,
        IResourceBuilder<ParameterResource>? clientSecret = null,
        IResourceBuilder<ParameterResource>? webhookSecret = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(privateKey);

        // Secret parameters are masked in the dashboard and kept out of plain-text configuration such as appsettings.json.
        ThrowIfNotSecret(privateKey, "private key", nameof(privateKey));
        ThrowIfNotSecret(clientSecret, "client secret", nameof(clientSecret));
        ThrowIfNotSecret(webhookSecret, "webhook secret", nameof(webhookSecret));

        var resource = new GitHubAppResource(name, appId.Resource, clientId.Resource, privateKey.Resource, clientSecret?.Resource, webhookSecret?.Resource);

        return builder.AddResource(resource)
            .WithIconName("Bot")
            // The resource waits until its values are known, which takes a while when they come from parameters that the
            // dashboard prompts for. Initialization then validates them and moves the resource to Running or FailedToStart.
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = ResourceType,
                State = KnownResourceStates.Waiting,
                Properties = []
            })
            .OnInitializeResource(InitializeGitHubAppResourceAsync);
    }

    /// <summary>
    /// Adds a GitHub App that is registered on GitHub the first time the AppHost runs.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="organization">
    /// The login of the organization to register the app under, or <see langword="null"/> to register it under the
    /// personal account of the user who completes the registration.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{GitHubAppResource}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="organization"/> is not in the format GitHub uses.</exception>
    /// <remarks>
    /// <para>
    /// When the AppHost runs and the app hasn't been registered yet, the resource waits and the dashboard shows a
    /// notification with a link that opens GitHub's "Register new GitHub App" page, filled in from a
    /// <see href="https://docs.github.com/apps/sharing-github-apps/registering-a-github-app-from-a-manifest">manifest</see>
    /// built from <see cref="WithCallback(IResourceBuilder{GitHubAppResource}, string)"/> and
    /// <see cref="WithPermission(IResourceBuilder{GitHubAppResource}, string, GitHubAppPermissionLevel)"/>. The link is
    /// also written to the resource's console logs. After the user creates the app, GitHub returns the browser to a
    /// temporary listener on 127.0.0.1, which retrieves the app's credentials and continues to the page where the app is
    /// installed on repositories.
    /// </para>
    /// <para>
    /// The credentials are saved to the AppHost's user secrets, under keys that include a hash of the AppHost's path so
    /// that each clone or worktree of a repository registers its own app. Later runs use the saved app. To register a new
    /// app, delete the <c>GitHub:Apps</c> keys from the user secrets.
    /// </para>
    /// <para>
    /// The manifest is used only when the app is created. Later changes to its callbacks or permissions must be made on
    /// the app's settings page.
    /// </para>
    /// <para>
    /// When publishing, the app is treated as an existing app whose values come from the parameters
    /// <c>{name}-app-id</c>, <c>{name}-client-id</c>, <c>{name}-private-key</c> and <c>{name}-client-secret</c>.
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var api = builder.AddDotnetProject("api", "../Api/Api.csproj");
    ///
    /// var gitHubApp = builder.AddGitHubApp("github-app")
    ///     .WithCallback(api.GetEndpoint("https"), "/signin-github")
    ///     .WithPermission("issues", GitHubAppPermissionLevel.Read);
    ///
    /// api.WithReference(gitHubApp);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> AddGitHubApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? organization = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (organization is not null && GitHubAppValidation.ValidateOrganization(organization) is { } organizationError)
        {
            throw new ArgumentException($"The organization '{organization}' is not valid. {organizationError}", nameof(organization));
        }

        if (builder.ExecutionContext.IsPublishMode)
        {
            // A deployed application can't open a browser on the deployer's machine, so it uses an app registered
            // beforehand, typically the one that was registered when the AppHost ran locally.
            var appIdParameter = builder.AddParameter($"{name}-app-id");
            var clientIdParameter = builder.AddParameter($"{name}-client-id");
            var privateKeyParameter = builder.AddParameter($"{name}-private-key", secret: true);
            var clientSecretParameter = builder.AddParameter($"{name}-client-secret", secret: true);
            return builder.AddExistingGitHubApp(name, appIdParameter, clientIdParameter, privateKeyParameter, clientSecretParameter);
        }

        // The parameters aren't added to the application model, so the dashboard doesn't prompt for them: their values
        // come from the registration, or from the user secrets that a previous registration saved.
        var configuration = builder.Configuration;
        var prefix = GitHubAppCredentialStore.GetPrefix(configuration, name);
        ParameterResource CreateParameter(string suffix, string key, bool secret) =>
            new($"{name}-{suffix}", _ => configuration[$"{prefix}:{key}"] is { Length: > 0 } value
                ? value
                : throw new MissingParameterValueException($"GitHub App '{name}' hasn't been registered."), secret)
            {
                Required = false
            };

        var resource = new GitHubAppResource(
            name,
            CreateParameter("app-id", "AppId", secret: false),
            CreateParameter("client-id", "ClientId", secret: false),
            CreateParameter("private-key", "PrivateKey", secret: true),
            CreateParameter("client-secret", "ClientSecret", secret: true),
            webhookSecret: null)
        {
            Organization = organization,
            Manifest = new GitHubAppManifestSettings
            {
                AppName = CreateDefaultAppName(name),
                Description = $"Registered by the Aspire AppHost '{builder.Environment.ApplicationName}' for the resource '{name}'.",
                Organization = organization
            }
        };

        return builder.AddResource(resource)
            .WithIconName("Bot")
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = ResourceType,
                State = KnownResourceStates.Waiting,
                Properties = []
            })
            .OnInitializeResource(InitializeManifestGitHubAppResourceAsync);
    }

    /// <summary>
    /// Adds a URL that GitHub redirects users to after they authorize the app, to an app registered by
    /// <see cref="AddGitHubApp(IDistributedApplicationBuilder, string, string?)"/>.
    /// </summary>
    /// <param name="builder">The GitHub App resource builder.</param>
    /// <param name="url">The absolute <c>http</c> or <c>https</c> URL of the callback.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="url"/> isn't an absolute http or https URL.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the app wasn't added with <see cref="AddGitHubApp(IDistributedApplicationBuilder, string, string?)"/>, or
    /// already has the 10 callbacks that GitHub allows.
    /// </exception>
    /// <remarks>
    /// The callback is the app's OAuth redirect, used when the app signs users in. It is unrelated to the URL that GitHub
    /// returns to when the app is registered. Callbacks are set only when the app is created. When publishing, the
    /// callback is ignored.
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> WithCallback(this IResourceBuilder<GitHubAppResource> builder, string url)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"The callback URL '{url}' must be an absolute http or https URL.", nameof(url));
        }

        return AddCallback(builder, ReferenceExpression.Create($"{url}"));
    }

    /// <summary>
    /// Adds a URL that GitHub redirects users to after they authorize the app, to an app registered by
    /// <see cref="AddGitHubApp(IDistributedApplicationBuilder, string, string?)"/>.
    /// </summary>
    /// <param name="builder">The GitHub App resource builder.</param>
    /// <param name="url">An expression for the absolute URL of the callback, such as one built from an endpoint.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the app wasn't added with <see cref="AddGitHubApp(IDistributedApplicationBuilder, string, string?)"/>, or
    /// already has the 10 callbacks that GitHub allows.
    /// </exception>
    /// <remarks>
    /// The expression is evaluated when the app is registered. Endpoints that the callback refers to should use a fixed
    /// port, because the callback isn't updated when the port changes on a later run.
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> WithCallback(this IResourceBuilder<GitHubAppResource> builder, ReferenceExpression url)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);

        return AddCallback(builder, url);
    }

    /// <summary>
    /// Adds a URL that GitHub redirects users to after they authorize the app, built from an endpoint and a path.
    /// </summary>
    /// <param name="builder">The GitHub App resource builder.</param>
    /// <param name="endpoint">The endpoint that receives the callback.</param>
    /// <param name="path">The path of the callback, such as <c>/signin-github</c>.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <inheritdoc cref="WithCallback(IResourceBuilder{GitHubAppResource}, ReferenceExpression)" path="/exception"/>
    /// <inheritdoc cref="WithCallback(IResourceBuilder{GitHubAppResource}, ReferenceExpression)" path="/remarks"/>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> WithCallback(this IResourceBuilder<GitHubAppResource> builder, EndpointReference endpoint, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(path);

        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        return AddCallback(builder, ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Url)}{normalizedPath}"));
    }

    /// <summary>
    /// Requests a permission for an app registered by <see cref="AddGitHubApp(IDistributedApplicationBuilder, string, string?)"/>.
    /// </summary>
    /// <param name="builder">The GitHub App resource builder.</param>
    /// <param name="permission">
    /// GitHub's name for the permission, such as <c>issues</c>, <c>contents</c> or <c>pull_requests</c>. The names are
    /// listed in the
    /// <see href="https://docs.github.com/rest/apps/apps#create-an-installation-access-token-for-an-app">permissions object</see>
    /// of GitHub's REST API.
    /// </param>
    /// <param name="level">The level of access to request.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="permission"/> isn't in the format GitHub uses.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the app wasn't added with <see cref="AddGitHubApp(IDistributedApplicationBuilder, string, string?)"/>.
    /// </exception>
    /// <remarks>
    /// Requesting a permission again replaces its level. Permissions are set only when the app is created. When
    /// publishing, the permission is ignored.
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> WithPermission(
        this IResourceBuilder<GitHubAppResource> builder,
        string permission,
        GitHubAppPermissionLevel level)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(permission);

        if (GitHubAppValidation.ValidatePermissionName(permission) is { } error)
        {
            throw new ArgumentException($"The permission '{permission}' is not valid. {error}", nameof(permission));
        }

        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown permission level.");
        }

        if (GetManifest(builder, nameof(WithPermission)) is { } manifest)
        {
            manifest.Permissions[permission] = level;
        }

        return builder;
    }

    private static IResourceBuilder<GitHubAppResource> AddCallback(IResourceBuilder<GitHubAppResource> builder, ReferenceExpression url)
    {
        if (GetManifest(builder, nameof(WithCallback)) is { } manifest)
        {
            if (manifest.CallbackUrls.Count >= GitHubAppManifestSettings.MaxCallbackUrls)
            {
                throw new InvalidOperationException(
                    $"GitHub App '{builder.Resource.Name}' can't have more than {GitHubAppManifestSettings.MaxCallbackUrls} callback URLs.");
            }

            manifest.CallbackUrls.Add(url);
        }

        return builder;
    }

    // Returns null when publishing, where AddGitHubApp produces an existing app and the manifest isn't used.
    private static GitHubAppManifestSettings? GetManifest(IResourceBuilder<GitHubAppResource> builder, string methodName)
    {
        if (builder.Resource.Manifest is { } manifest)
        {
            return manifest;
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return null;
        }

        throw new InvalidOperationException(
            $"{methodName} can only be used with GitHub Apps added by {nameof(AddGitHubApp)}. Change the settings of an existing app on its settings page on GitHub.");
    }

    // GitHub App names are unique across GitHub and at most 34 characters long, so the suggested name ends with a random
    // suffix. The user can change it on GitHub's registration page.
    internal static string CreateDefaultAppName(string resourceName)
    {
        const int MaxLength = 34;
        var suffix = "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));

        var sanitized = new string([.. resourceName.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]).Trim('-');
        if (sanitized.Length == 0)
        {
            sanitized = "aspire-app";
        }

        if (sanitized.Length > MaxLength - suffix.Length)
        {
            sanitized = sanitized[..(MaxLength - suffix.Length)].TrimEnd('-');
        }

        return sanitized + suffix;
    }

    /// <summary>
    /// Sets the app's slug and owner, which the dashboard uses to link to the app's pages on GitHub.
    /// </summary>
    /// <param name="builder">The GitHub App resource builder.</param>
    /// <param name="slug">The app's URL-friendly name, the last segment of its public page <c>https://github.com/apps/{slug}</c>.</param>
    /// <param name="organization">
    /// The login of the organization that owns the app, or <see langword="null"/> when a personal account owns it.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="slug"/> or <paramref name="organization"/> is not in the format GitHub uses.
    /// </exception>
    /// <remarks>
    /// Without a slug, the dashboard links to the list of apps that the signed-in user owns, because finding an app's
    /// slug from its App ID requires calling GitHub.
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<GitHubAppResource> WithAppSlug(
        this IResourceBuilder<GitHubAppResource> builder,
        string slug,
        string? organization = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(slug);

        if (GitHubAppValidation.ValidateSlug(slug) is { } slugError)
        {
            throw new ArgumentException($"The slug '{slug}' is not valid. {slugError}", nameof(slug));
        }

        if (organization is not null && GitHubAppValidation.ValidateOrganization(organization) is { } organizationError)
        {
            throw new ArgumentException($"The organization '{organization}' is not valid. {organizationError}", nameof(organization));
        }

        builder.Resource.Slug = slug;
        builder.Resource.Organization = organization;
        return builder;
    }

    /// <summary>
    /// Gives a resource the credentials of a GitHub App as environment variables.
    /// </summary>
    /// <typeparam name="T">The type of the destination resource.</typeparam>
    /// <param name="builder">The resource that receives the credentials.</param>
    /// <param name="source">The GitHub App resource to reference.</param>
    /// <param name="connectionName">
    /// The name that the variables are grouped under, or <see langword="null"/> for the default names. Use a connection
    /// name when a resource references more than one app.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="connectionName"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the resource already references a different app whose variables have the same names.
    /// </exception>
    /// <remarks>
    /// <para>
    /// By default, every resource receives these variables:
    /// </para>
    /// <list type="table">
    /// <listheader><term>Variable</term><description>Value</description></listheader>
    /// <item><term><c>GITHUB_APP_ID</c></term><description>The App ID.</description></item>
    /// <item><term><c>GITHUB_CLIENT_ID</c></term><description>The Client ID.</description></item>
    /// <item><term><c>GITHUB_PRIVATE_KEY</c></term><description>The PEM-encoded private key.</description></item>
    /// <item><term><c>GITHUB_CLIENT_SECRET</c></term><description>The client secret, when the app has one.</description></item>
    /// <item><term><c>GITHUB_WEBHOOK_SECRET</c></term><description>The webhook secret, when the app has one.</description></item>
    /// <item><term><c>GITHUB_API_URL</c></term><description>The base URL of the GitHub REST API.</description></item>
    /// <item><term><c>APP_ID</c>, <c>PRIVATE_KEY</c>, <c>WEBHOOK_SECRET</c></term><description>The names that Probot apps read.</description></item>
    /// </list>
    /// <para>
    /// .NET program resources also receive the values as the <c>GitHub:App</c> configuration section, through the
    /// variables <c>GitHub__App__AppId</c>, <c>GitHub__App__ClientId</c>, <c>GitHub__App__PrivateKey</c>,
    /// <c>GitHub__App__ClientSecret</c>, <c>GitHub__App__WebhookSecret</c> and <c>GitHub__App__ApiUrl</c>.
    /// </para>
    /// <para>
    /// A connection name replaces the <c>GITHUB</c> prefix with the name in upper snake case, so <c>"orders-bot"</c>
    /// produces <c>ORDERS_BOT_APP_ID</c>, and names the .NET configuration section, so <c>"OrdersBot"</c> produces
    /// <c>OrdersBot__AppId</c>. A <c>:</c> in the name separates nested sections.
    /// </para>
    /// <para>
    /// The Probot names are always given, but they can hold only one app's credentials. When a resource references more
    /// than one app, the Probot names hold the app referenced without a connection name, or otherwise the app referenced
    /// first.
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddDotnetProject("api", "../Api/Api.csproj")
    ///     .WithReference(gitHubApp);
    ///
    /// builder.AddDotnetProject("worker", "../Worker/Worker.csproj")
    ///     .WithReference(ordersBot, connectionName: "OrdersBot")
    ///     .WithReference(releaseBot, connectionName: "ReleaseBot");
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "GitHub App resources are not yet available to polyglot AppHosts.")]
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<GitHubAppResource> source,
        string? connectionName = null)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        if (connectionName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        }

        var app = source.Resource;

        // Inspect the resource itself so .NET defaults also apply when the builder's type has been erased.
#pragma warning disable ASPIREPROJECTS001
        var isDotnetProgram = builder.Resource is IDotnetProgramResource;
#pragma warning restore ASPIREPROJECTS001

        var environmentVariables = GetEnvironmentVariables(app, connectionName, isDotnetProgram);
        var names = environmentVariables.Keys.ToArray();

        // Environment variable names are case-insensitive on Windows, so compare them that way everywhere to report the
        // same collisions on every platform.
        foreach (var existing in builder.Resource.Annotations.OfType<GitHubAppReferenceAnnotation>())
        {
            if (existing.Source == app)
            {
                continue;
            }

            var collision = existing.EnvironmentVariableNames.Intersect(names, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (collision is not null)
            {
                throw new InvalidOperationException(
                    $"Resource '{builder.Resource.Name}' can't reference GitHub App '{app.Name}' because its environment variable '{collision}' " +
                    $"is already set by the reference to GitHub App '{existing.Source.Name}'. Pass a different connectionName to WithReference for each app.");
            }
        }

        var resource = builder.Resource;
        var reference = new GitHubAppReferenceAnnotation(app, connectionName, names);
        builder.WithReferenceRelationship(app);
        resource.Annotations.Add(reference);

        // An app registered from a manifest has no credentials until the user completes the registration, so a resource
        // that references it starts only once it is running.
        if (app.Manifest is not null && !resource.Annotations.OfType<WaitAnnotation>().Any(wait => wait.Resource == app))
        {
            resource.Annotations.Add(new WaitAnnotation(app, WaitType.WaitUntilHealthy));
        }

        return builder.WithEnvironment(context =>
        {
            foreach (var (name, value) in environmentVariables)
            {
                context.EnvironmentVariables[name] = value;
            }

            // The Probot names aren't prefixed, so only one reference on the resource can set them. Choosing the owner
            // when the environment is built, rather than when the reference is added, makes the result independent of
            // the order of the WithReference calls.
            var references = resource.Annotations.OfType<GitHubAppReferenceAnnotation>().ToList();
            var probotReference = references.FirstOrDefault(r => r.ConnectionName is null) ?? references[0];
            if (probotReference == reference)
            {
                AddProbotSettings(context.EnvironmentVariables, app);
            }
        });
    }

    internal static Dictionary<string, object> GetEnvironmentVariables(GitHubAppResource app, string? connectionName, bool isDotnetProgram)
    {
        var environmentVariables = new Dictionary<string, object>(StringComparer.Ordinal);

        // Names such as "orders-bot" and "OrdersBot" both become ORDERS_BOT, the conventional form of an environment
        // variable name, and the encoder replaces characters that aren't portable in names, such as ':'.
        var prefix = connectionName is null
            ? DefaultEnvironmentVariablePrefix
            : JsonNamingPolicy.SnakeCaseUpper.ConvertName(EnvironmentVariableNameEncoder.Encode(connectionName));

        AddSettings(environmentVariables, app, key => $"{prefix}_{JsonNamingPolicy.SnakeCaseUpper.ConvertName(key)}");

        if (isDotnetProgram)
        {
            // .NET configuration maps "__" in an environment variable name to the ":" section separator.
            var section = connectionName?.Replace(":", "__", StringComparison.Ordinal) ?? DefaultDotnetSection;
            AddSettings(environmentVariables, app, key => $"{section}__{key}");
        }

        return environmentVariables;
    }

    private static void AddProbotSettings(IDictionary<string, object> environmentVariables, GitHubAppResource app)
    {
        // Probot reads APP_ID, PRIVATE_KEY and WEBHOOK_SECRET, and also GITHUB_CLIENT_ID and GITHUB_CLIENT_SECRET.
        // See https://probot.github.io/docs/configuration/.
        environmentVariables["APP_ID"] = app.AppIdParameter;
        environmentVariables["PRIVATE_KEY"] = app.PrivateKeyParameter;
        if (app.WebhookSecretParameter is { } webhookSecret)
        {
            environmentVariables["WEBHOOK_SECRET"] = webhookSecret;
        }
    }

    private static void AddSettings(Dictionary<string, object> environmentVariables, GitHubAppResource app, Func<string, string> getName)
    {
        // A secret that the app doesn't have is left unset, so that a consumer can tell it apart from an empty value.
        environmentVariables[getName("AppId")] = app.AppIdParameter;
        environmentVariables[getName("ClientId")] = app.ClientIdParameter;
        environmentVariables[getName("PrivateKey")] = app.PrivateKeyParameter;

        if (app.ClientSecretParameter is { } clientSecret)
        {
            environmentVariables[getName("ClientSecret")] = clientSecret;
        }

        if (app.WebhookSecretParameter is { } webhookSecret)
        {
            environmentVariables[getName("WebhookSecret")] = webhookSecret;
        }

        environmentVariables[getName("ApiUrl")] = app.ApiUrl;
    }

    private static void ThrowIfNotSecret(IResourceBuilder<ParameterResource>? parameter, string setting, string parameterName)
    {
        if (parameter is not null && !parameter.Resource.Secret)
        {
            throw new ArgumentException(
                $"The {setting} parameter must be marked as secret. Use AddParameter with secret: true when creating the parameter.",
                parameterName);
        }
    }

    private static async Task InitializeManifestGitHubAppResourceAsync(
        GitHubAppResource resource,
        InitializeResourceEvent evt,
        CancellationToken cancellationToken)
    {
        var manifest = resource.Manifest!;
        var logger = evt.Logger;
        var store = new GitHubAppCredentialStore(
            evt.Services.GetRequiredService<IConfiguration>(),
            evt.Services.GetRequiredService<IUserSecretsManager>(),
            resource.Name);

        var credentials = store.Load();
        if (credentials is null)
        {
            // Without somewhere to save the credentials, every run would register another app on GitHub.
            if (!store.CanSave)
            {
                logger.LogError(
                    "GitHub App '{ResourceName}' hasn't been registered, and can't be because the AppHost has no user secrets to save its credentials to. " +
                    "Add a UserSecretsId to the AppHost project, for example by running 'dotnet user-secrets init' in its directory.",
                    resource.Name);
                await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
                return;
            }

            try
            {
                credentials = await RegisterAsync(resource, manifest, evt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to register GitHub App '{ResourceName}'. Restart the resource to try again.", resource.Name);
                await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
                return;
            }

            if (store.Save(credentials))
            {
                logger.LogInformation("Saved the credentials of GitHub App '{Slug}' to the AppHost's user secrets in '{FilePath}'.", credentials.Slug, store.FilePath);
            }
            else
            {
                logger.LogWarning(
                    "Failed to save the credentials of GitHub App '{Slug}' to the AppHost's user secrets in '{FilePath}'. The next run will register another app.",
                    credentials.Slug, store.FilePath);
            }

            await resource.AppIdParameter.SetValueAsync(credentials.AppId, cancellationToken).ConfigureAwait(false);
            await resource.ClientIdParameter.SetValueAsync(credentials.ClientId, cancellationToken).ConfigureAwait(false);
            await resource.PrivateKeyParameter.SetValueAsync(credentials.PrivateKey, cancellationToken).ConfigureAwait(false);
            if (resource.ClientSecretParameter is { } clientSecret)
            {
                await clientSecret.SetValueAsync(credentials.ClientSecret, cancellationToken).ConfigureAwait(false);
            }
        }

        resource.Slug = credentials.Slug;
        resource.Organization = credentials.Organization;

        await InitializeGitHubAppResourceAsync(resource, evt, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubAppCredentials> RegisterAsync(
        GitHubAppResource resource,
        GitHubAppManifestSettings manifest,
        InitializeResourceEvent evt,
        CancellationToken cancellationToken)
    {
        var logger = evt.Logger;

        var flow = new GitHubAppManifestFlow(evt.Services.GetRequiredService<IHttpClientFactory>(), logger, resource.ApiUrl);
        await using var flowScope = flow.ConfigureAwait(false);
        await flow.StartAsync(
            (redirectUrl, ct) => GitHubAppManifest.CreateJsonAsync(manifest, redirectUrl, ct),
            manifest.Organization,
            cancellationToken).ConfigureAwait(false);

        var startUrl = flow.StartUrl!;
        logger.LogInformation(
            "GitHub App '{ResourceName}' hasn't been registered. Open {StartUrl} in a browser on this machine to review and create the app on GitHub.",
            resource.Name, startUrl);

        await evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = new ResourceStateSnapshot(WaitingForRegistrationState, KnownResourceStateStyles.Info),
            Urls = [.. snapshot.Urls.Where(url => url.Name != RegisterUrlName), new UrlSnapshot(RegisterUrlName, startUrl, IsInternal: false)
            {
                DisplayProperties = new UrlDisplayPropertiesSnapshot("Register on GitHub")
            }]
        }).ConfigureAwait(false);

        // The notification stays in the dashboard until it is dismissed, so it isn't awaited. Cancelling it once the
        // registration has finished removes it. It handles its own exceptions.
        using var notificationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ShowRegistrationNotificationAsync(evt.Services, resource, startUrl, logger, notificationCts.Token);

        try
        {
            return await flow.WaitForCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            notificationCts.Cancel();

            await evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                Urls = [.. snapshot.Urls.Where(url => url.Name != RegisterUrlName)]
            }).ConfigureAwait(false);
        }
    }

    private static async Task ShowRegistrationNotificationAsync(
        IServiceProvider services,
        GitHubAppResource resource,
        string startUrl,
        ILogger logger,
        CancellationToken cancellationToken)
    {
#pragma warning disable ASPIREINTERACTION001
        if (services.GetService<IInteractionService>() is not { IsAvailable: true } interactionService)
        {
            return;
        }

        try
        {
            await interactionService.PromptNotificationAsync(
                "Register GitHub App",
                $"GitHub App '{resource.Name}' needs to be registered on GitHub before the resources that use it can start.",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Information,
                    LinkText = "Register on GitHub",
                    LinkUrl = startUrl
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The link is also in the console logs and on the resource, so the registration can continue without it.
            logger.LogDebug(ex, "Failed to show the registration notification.");
        }
#pragma warning restore ASPIREINTERACTION001
    }

    private static async Task InitializeGitHubAppResourceAsync(
        GitHubAppResource resource,
        InitializeResourceEvent evt,
        CancellationToken cancellationToken)
    {
        var logger = evt.Logger;

        // Check every setting before failing, so that a misconfigured resource reports all of its problems at once
        // rather than one per restart of the AppHost.
        var appId = await ResolveAsync(resource.AppIdParameter, "App ID", GitHubAppValidation.ValidateAppId, logger, cancellationToken).ConfigureAwait(false);
        var clientId = await ResolveAsync(resource.ClientIdParameter, "Client ID", GitHubAppValidation.ValidateClientId, logger, cancellationToken).ConfigureAwait(false);
        var privateKey = await ResolveAsync(resource.PrivateKeyParameter, "private key", GitHubAppValidation.ValidatePrivateKey, logger, cancellationToken).ConfigureAwait(false);

        var hasInvalidSetting = appId is null || clientId is null || privateKey is null;

        if (resource.ClientSecretParameter is { } clientSecret &&
            await ResolveAsync(clientSecret, "client secret", GitHubAppValidation.ValidateSecret, logger, cancellationToken).ConfigureAwait(false) is null)
        {
            hasInvalidSetting = true;
        }

        if (resource.WebhookSecretParameter is { } webhookSecret &&
            await ResolveAsync(webhookSecret, "webhook secret", GitHubAppValidation.ValidateSecret, logger, cancellationToken).ConfigureAwait(false) is null)
        {
            hasInvalidSetting = true;
        }

        if (hasInvalidSetting)
        {
            await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
            return;
        }

        try
        {
            await evt.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, evt.Services), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "A {EventName} subscriber failed.", nameof(BeforeResourceStartedEvent));
            await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
            return;
        }

        var urls = ImmutableArray.CreateBuilder<UrlSnapshot>();
        urls.Add(new UrlSnapshot(SettingsUrlName, GitHubAppUrls.GetSettingsUrl(resource.Slug, resource.Organization), IsInternal: false)
        {
            DisplayProperties = new UrlDisplayPropertiesSnapshot(resource.Slug is null ? "GitHub Apps" : "App settings")
        });

        if (GitHubAppUrls.GetPublicUrl(resource.Slug) is { } publicUrl)
        {
            urls.Add(new UrlSnapshot(PublicPageUrlName, publicUrl, IsInternal: false)
            {
                DisplayProperties = new UrlDisplayPropertiesSnapshot("Public page")
            });
        }

        var properties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
        properties.Add(CreateHighlightedProperty("github.app.id", "App ID", appId!, isSensitive: resource.AppIdParameter.Secret, sortOrder: 0));
        properties.Add(CreateHighlightedProperty("github.client.id", "Client ID", clientId!, isSensitive: resource.ClientIdParameter.Secret, sortOrder: 1));
        properties.Add(CreateHighlightedProperty("github.api.url", "API URL", resource.ApiUrl, isSensitive: false, sortOrder: 2));
        if (resource.Slug is { } slug)
        {
            properties.Add(CreateHighlightedProperty("github.app.slug", "Slug", slug, isSensitive: false, sortOrder: 3));
        }

        if (resource.Organization is { } organization)
        {
            properties.Add(CreateHighlightedProperty("github.app.owner", "Organization", organization, isSensitive: false, sortOrder: 4));
        }

        await evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            Urls = [.. snapshot.Urls.Where(url => url.Name is not SettingsUrlName and not PublicPageUrlName and not RegisterUrlName), .. urls],
            Properties = [.. snapshot.Properties.Where(property => !properties.Any(updated => updated.Name == property.Name)), .. properties],
            State = KnownResourceStates.Running
        }).ConfigureAwait(false);
    }

    // Gets a parameter's value and validates it. Problems are logged to the resource's console and reported by returning
    // null. Values are never logged, because the parameter may be secret.
    private static async Task<string?> ResolveAsync(
        ParameterResource parameter,
        string setting,
        Func<string?, string?> validate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? value;
        try
        {
            value = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to get the value of the {Setting} parameter '{ParameterName}'.", setting, parameter.Name);
            return null;
        }

        if (validate(value) is { } error)
        {
            logger.LogError("Parameter '{ParameterName}' does not contain a valid {Setting}. {Reason}", parameter.Name, setting, error);
            return null;
        }

        return value;
    }

    private static Task PublishFailedToStartAsync(GitHubAppResource resource, InitializeResourceEvent evt)
    {
        return evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = KnownResourceStates.FailedToStart
        });
    }

    // Unknown property names are hidden in the dashboard unless they are highlighted.
    private static ResourcePropertySnapshot CreateHighlightedProperty(string name, string displayName, string value, bool isSensitive, int sortOrder)
    {
        return new(name, value)
        {
            DisplayName = displayName,
            IsHighlighted = true,
            IsSensitive = isSensitive,
            SortOrder = sortOrder
        };
    }
}
