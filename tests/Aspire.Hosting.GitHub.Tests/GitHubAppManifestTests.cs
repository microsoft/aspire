// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.GitHub.Tests;

public class GitHubAppManifestTests
{
    private static readonly string s_privateKey = CreatePrivateKey();

    [Fact]
    public async Task CreateJson_SeparatesRedirectUrlFromCallbacksAndMapsPermissions()
    {
        var settings = new GitHubAppManifestSettings
        {
            AppName = "my-app-abc123",
            Description = "A test app."
        };
        settings.CallbackUrls.Add(ReferenceExpression.Create($"https://localhost:7001/signin-github"));
        settings.Permissions["issues"] = GitHubAppPermissionLevel.Read;
        settings.Permissions["contents"] = GitHubAppPermissionLevel.Write;
        settings.Permissions["administration"] = GitHubAppPermissionLevel.Admin;

        var json = await GitHubAppManifest.CreateJsonAsync(settings, "http://127.0.0.1:5000/abc/callback", CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""
                {
                  "name": "my-app-abc123",
                  "url": "https://aspire.dev",
                  "description": "A test app.",
                  "redirect_url": "http://127.0.0.1:5000/abc/callback",
                  "public": false,
                  "callback_urls": ["https://localhost:7001/signin-github"],
                  "default_permissions": { "administration": "admin", "contents": "write", "issues": "read" }
                }
                """),
            JsonNode.Parse(json)), json);
    }

    [Fact]
    public async Task CreateJson_WithoutCallbacks_OmitsCallbackUrls()
    {
        var settings = new GitHubAppManifestSettings { AppName = "my-app", Description = "A test app." };

        var json = JsonNode.Parse(await GitHubAppManifest.CreateJsonAsync(settings, "http://127.0.0.1:5000/abc/callback", CancellationToken.None))!;

        Assert.Null(json["callback_urls"]);
        Assert.Null(json["hook_attributes"]);
        Assert.Empty(json["default_permissions"]!.AsObject());
    }

    [Theory]
    [InlineData(null, "https://github.com/settings/apps/new?state=abc")]
    [InlineData("contoso", "https://github.com/organizations/contoso/settings/apps/new?state=abc")]
    public void GetRegistrationUrl_TargetsPersonalOrOrganizationAccount(string? organization, string expected)
    {
        Assert.Equal(expected, GitHubAppManifestFlow.GetRegistrationUrl(organization, "abc"));
    }

    [Theory]
    [InlineData("github-app")]
    [InlineData("My_GitHub.App")]
    [InlineData("a-very-long-resource-name-that-exceeds-the-github-limit")]
    [InlineData("___")]
    public void CreateDefaultAppName_IsValidGitHubAppName(string resourceName)
    {
        var name = GitHubAppResourceExtensions.CreateDefaultAppName(resourceName);

        Assert.InRange(name.Length, 1, 34);
        Assert.Matches("^[a-z0-9][a-z0-9-]*-[0-9a-f]{6}$", name);
    }

    [Fact]
    public void ParseConversion_PersonalApp_HasNoOrganization()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": 42, "slug": "octoapp", "client_id": "Iv1.abc", "client_secret": "secret", "webhook_secret": null,
              "pem": "-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----\n",
              "owner": { "login": "octocat", "type": "User" }
            }
            """);

        Assert.Equal(
            new GitHubAppCredentials("42", "Iv1.abc", "-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----\n", "secret", null, "octoapp", null),
            GitHubAppManifestFlow.ParseConversion(document.RootElement));
    }

    [Fact]
    public void ParseConversion_OrganizationApp_HasOrganization()
    {
        using var document = JsonDocument.Parse("""
            { "id": 42, "slug": "octoapp", "client_id": "Iv1.abc", "pem": "pem", "owner": { "login": "contoso", "type": "Organization" } }
            """);

        Assert.Equal("contoso", GitHubAppManifestFlow.ParseConversion(document.RootElement).Organization);
    }

    [Theory]
    [InlineData("""{ "slug": "octoapp", "client_id": "Iv1.abc", "pem": "pem" }""")]
    [InlineData("""{ "id": "42", "client_id": "Iv1.abc", "pem": "pem" }""")]
    [InlineData("""{ "id": 42, "pem": "pem" }""")]
    [InlineData("""{ "id": 42, "client_id": "Iv1.abc" }""")]
    public void ParseConversion_MissingRequiredValue_Throws(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Throws<InvalidOperationException>(() => GitHubAppManifestFlow.ParseConversion(document.RootElement));
    }

    [Fact]
    public void GetPrefix_IncludesAppHostPathHash()
    {
        var withHash = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AppHost:PathSha256"] = "abc123" }).Build();
        var withoutHash = new ConfigurationBuilder().Build();

        Assert.Equal("GitHub:Apps:abc123:github-app", GitHubAppCredentialStore.GetPrefix(withHash, "github-app"));
        Assert.Equal("GitHub:Apps:github-app", GitHubAppCredentialStore.GetPrefix(withoutHash, "github-app"));
    }

    [Fact]
    public void WithCallback_RelativeUrl_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddGitHubApp("github-app");

        Assert.Throws<ArgumentException>(() => gitHubApp.WithCallback("/signin-github"));
        Assert.Throws<ArgumentException>(() => gitHubApp.WithCallback("ftp://localhost/signin-github"));
    }

    [Fact]
    public void WithCallback_MoreThanTen_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddGitHubApp("github-app");
        for (var i = 0; i < 10; i++)
        {
            gitHubApp.WithCallback($"https://localhost:{7000 + i}/signin-github");
        }

        Assert.Throws<InvalidOperationException>(() => gitHubApp.WithCallback("https://localhost:8000/signin-github"));
    }

    [Fact]
    public void WithPermission_CalledAgain_ReplacesLevel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddGitHubApp("github-app")
            .WithPermission("issues", GitHubAppPermissionLevel.Read)
            .WithPermission("issues", GitHubAppPermissionLevel.Write);

        Assert.Equal(GitHubAppPermissionLevel.Write, Assert.Single(gitHubApp.Resource.Manifest!.Permissions).Value);
    }

    [Theory]
    [InlineData("Issues")]
    [InlineData("pull-requests")]
    [InlineData("")]
    public void WithPermission_InvalidName_Throws(string permission)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddGitHubApp("github-app");

        Assert.Throws<ArgumentException>(() => gitHubApp.WithPermission(permission, GitHubAppPermissionLevel.Read));
    }

    [Fact]
    public void WithCallbackAndPermission_ExistingApp_Throw()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddExistingGitHubApp(
            "github-app",
            builder.AddParameter("app-id", "123"),
            builder.AddParameter("client-id", "Iv1.abc"),
            builder.AddParameter("private-key", s_privateKey, secret: true));

        Assert.Throws<InvalidOperationException>(() => gitHubApp.WithCallback("https://localhost:7001/signin-github"));
        Assert.Throws<InvalidOperationException>(() => gitHubApp.WithPermission("issues", GitHubAppPermissionLevel.Read));
    }

    [Fact]
    public void AddGitHubApp_PublishMode_UsesParametersAndIgnoresManifestSettings()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var gitHubApp = builder.AddGitHubApp("github-app")
            .WithCallback("https://localhost:7001/signin-github")
            .WithPermission("issues", GitHubAppPermissionLevel.Read);

        Assert.Null(gitHubApp.Resource.Manifest);
        Assert.Equal(
            ["github-app-app-id", "github-app-client-id", "github-app-private-key", "github-app-client-secret"],
            builder.Resources.OfType<ParameterResource>().Select(p => p.Name));
        Assert.True(gitHubApp.Resource.PrivateKeyParameter.Secret);
        Assert.True(gitHubApp.Resource.ClientSecretParameter!.Secret);
    }

    [Fact]
    public void AddGitHubApp_RunMode_DoesNotAddParametersToModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGitHubApp("github-app");

        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }

    [Fact]
    public void WithReference_ManifestApp_WaitsForApp()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddGitHubApp("github-app");
        var api = builder.AddContainer("api", "image").WithReference(gitHubApp);

        var wait = Assert.Single(api.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(gitHubApp.Resource, wait.Resource);
        Assert.Equal(WaitType.WaitUntilHealthy, wait.WaitType);
    }

    [Fact]
    public async Task Initialize_SavedCredentials_IsRunningWithoutRegistering()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var prefix = GitHubAppCredentialStore.GetPrefix(builder.Configuration, "github-app");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{prefix}:AppId"] = "42",
            [$"{prefix}:ClientId"] = "Iv1.abc",
            [$"{prefix}:PrivateKey"] = s_privateKey,
            [$"{prefix}:ClientSecret"] = "secret",
            [$"{prefix}:Slug"] = "octoapp",
            [$"{prefix}:Organization"] = "contoso"
        });
        var handler = new FakeConversionHandler(HttpStatusCode.InternalServerError, "{}");
        builder.Services.AddHttpClient(GitHubAppManifestFlow.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var gitHubApp = builder.AddGitHubApp("github-app");

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal("https://github.com/organizations/contoso/settings/apps/octoapp", snapshot.Urls[0].Url);
        Assert.Equal("42", await gitHubApp.Resource.AppIdParameter.GetValueAsync(default));
        Assert.Equal("secret", await gitHubApp.Resource.ClientSecretParameter!.GetValueAsync(default));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Initialize_NoUserSecrets_FailsToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IUserSecretsManager>(new MockUserSecretsManager(canSetSecret: false));
        var gitHubApp = builder.AddGitHubApp("github-app");

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
    }

    [Fact]
    public async Task Initialize_Registration_SavesCredentialsAndIsRunning()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var userSecrets = new MockUserSecretsManager();
        var interactions = new TestInteractionService();
        builder.Services.AddSingleton<IUserSecretsManager>(userSecrets);
        builder.Services.AddSingleton<IInteractionService>(interactions);
        var conversion = JsonSerializer.Serialize(new
        {
            id = 42,
            slug = "octoapp",
            client_id = "Iv1.abc",
            client_secret = "secret",
            webhook_secret = (string?)null,
            pem = s_privateKey,
            owner = new { login = "octocat", type = "User" }
        });
        var handler = new FakeConversionHandler(HttpStatusCode.Created, conversion);
        builder.Services.AddHttpClient(GitHubAppManifestFlow.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var gitHubApp = builder.AddGitHubApp("github-app")
            .WithCallback("https://localhost:7001/signin-github")
            .WithPermission("issues", GitHubAppPermissionLevel.Read);

        using var app = builder.Build();
        var prefix = GitHubAppCredentialStore.GetPrefix(app.Services.GetRequiredService<IConfiguration>(), "github-app");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var initialize = InitializeAsync(app, gitHubApp.Resource);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var waiting = await notifications.WaitForResourceAsync("github-app", e => e.Snapshot.State?.Text == "Waiting for registration", cts.Token);
        var startUrl = Assert.Single(waiting.Snapshot.Urls, u => u.Name == "Register").Url;

        var notification = await interactions.Interactions.Reader.ReadAsync(cts.Token);
        Assert.Equal(InteractionType.Notification, notification.Type);
        Assert.Equal(startUrl, ((NotificationInteractionOptions)notification.Options!).LinkUrl);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var startPage = await client.GetStringAsync(startUrl, cts.Token);

        // The start page posts the manifest to GitHub, for example:
        //   <form method="post" action="https://github.com/settings/apps/new?state=0a1b...">
        //   <input type="hidden" name="manifest" value="{&quot;name&quot;:...}">
        var state = Regex.Match(startPage, "state=([0-9a-f]+)").Groups[1].Value;
        var manifest = JsonNode.Parse(WebUtility.HtmlDecode(Regex.Match(startPage, "name=\"manifest\" value=\"([^\"]*)\"").Groups[1].Value))!;
        var callbackUrl = manifest["redirect_url"]!.GetValue<string>();
        Assert.StartsWith("https://github.com/settings/apps/new?state=", startPage[startPage.IndexOf("https://github.com", StringComparison.Ordinal)..]);
        Assert.StartsWith("http://127.0.0.1:", callbackUrl);
        Assert.Equal("https://localhost:7001/signin-github", Assert.Single(manifest["callback_urls"]!.AsArray())!.GetValue<string>());
        Assert.Equal("read", manifest["default_permissions"]!["issues"]!.GetValue<string>());

        using var badState = await client.GetAsync($"{callbackUrl}?code=abc&state=wrong", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        Assert.Equal(0, handler.RequestCount);

        using var callback = await client.GetAsync($"{callbackUrl}?code=abc123&state={state}", cts.Token);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("https://github.com/apps/octoapp/installations/new", callback.Headers.Location!.ToString());

        var snapshot = await initialize.WaitAsync(cts.Token);
        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(
            ["https://github.com/settings/apps/octoapp", "https://github.com/apps/octoapp"],
            snapshot.Urls.Select(u => u.Url));
        Assert.Equal("https://api.github.com/app-manifests/abc123/conversions", handler.LastRequestUri);
        Assert.Equal("42", await gitHubApp.Resource.AppIdParameter.GetValueAsync(default));
        Assert.Equal(s_privateKey, await gitHubApp.Resource.PrivateKeyParameter.GetValueAsync(default));
        Assert.Equal(new Dictionary<string, string>
        {
            [$"{prefix}:AppId"] = "42",
            [$"{prefix}:ClientId"] = "Iv1.abc",
            [$"{prefix}:PrivateKey"] = s_privateKey,
            [$"{prefix}:ClientSecret"] = "secret",
            [$"{prefix}:Slug"] = "octoapp"
        }, userSecrets.Secrets.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary());
        Assert.True(notification.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Initialize_ConversionFails_FailsToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var userSecrets = new MockUserSecretsManager();
        builder.Services.AddSingleton<IUserSecretsManager>(userSecrets);
        var handler = new FakeConversionHandler(HttpStatusCode.NotFound, "{}");
        builder.Services.AddHttpClient(GitHubAppManifestFlow.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        var gitHubApp = builder.AddGitHubApp("github-app");

        using var app = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var initialize = InitializeAsync(app, gitHubApp.Resource);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var waiting = await notifications.WaitForResourceAsync("github-app", e => e.Snapshot.State?.Text == "Waiting for registration", cts.Token);
        var startUrl = Assert.Single(waiting.Snapshot.Urls, u => u.Name == "Register").Url;

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var startPage = await client.GetStringAsync(startUrl, cts.Token);
        var state = Regex.Match(startPage, "state=([0-9a-f]+)").Groups[1].Value;
        var callbackUrl = startUrl[..^"/start".Length] + "/callback";

        using var callback = await client.GetAsync($"{callbackUrl}?code=abc123&state={state}", cts.Token);
        Assert.Equal(HttpStatusCode.BadGateway, callback.StatusCode);

        var snapshot = await initialize.WaitAsync(cts.Token);
        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Empty(userSecrets.Secrets);
    }

    private static string CreatePrivateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    // Raising InitializeResourceEvent directly runs the resource's initialization without starting the orchestrator.
    private static async Task<CustomResourceSnapshot> InitializeAsync(DistributedApplication app, IResource resource)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();

        await Task.Yield();
        await eventing.PublishAsync(new InitializeResourceEvent(resource, eventing, loggerService, notifications, app.Services));

        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        return resourceEvent.Snapshot;
    }

    private sealed class FakeConversionHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
