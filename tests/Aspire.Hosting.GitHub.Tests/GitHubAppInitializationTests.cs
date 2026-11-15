// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.GitHub.Tests;

public class GitHubAppInitializationTests
{
    private const string AppId = "123456";
    private const string ClientId = "Iv23liAbCdEf01234567";

    private static readonly string s_privateKey = CreatePrivateKey();

    [Fact]
    public async Task Initialize_ValidApp_IsRunningWithLinkToAppsListAndProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, s_privateKey);

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new UrlSnapshot[]
        {
            new("Settings", "https://github.com/settings/apps", IsInternal: false) { DisplayProperties = new("GitHub Apps") }
        }, snapshot.Urls);
        Assert.Equal(new ResourcePropertySnapshot[]
        {
            new("github.app.id", AppId) { DisplayName = "App ID", IsHighlighted = true, SortOrder = 0 },
            new("github.client.id", ClientId) { DisplayName = "Client ID", IsHighlighted = true, SortOrder = 1 },
            new("github.api.url", "https://api.github.com") { DisplayName = "API URL", IsHighlighted = true, SortOrder = 2 }
        }, snapshot.Properties);
    }

    [Fact]
    public async Task Initialize_WithSlug_LinksToPersonalSettingsAndPublicPage()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, s_privateKey).WithAppSlug("my-app");

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal(new UrlSnapshot[]
        {
            new("Settings", "https://github.com/settings/apps/my-app", IsInternal: false) { DisplayProperties = new("App settings") },
            new("Public page", "https://github.com/apps/my-app", IsInternal: false) { DisplayProperties = new("Public page") }
        }, snapshot.Urls);
        Assert.Equal(
            new ResourcePropertySnapshot("github.app.slug", "my-app") { DisplayName = "Slug", IsHighlighted = true, SortOrder = 3 },
            snapshot.Properties[3]);
    }

    [Fact]
    public async Task Initialize_WithSlugAndOrganization_LinksToOrganizationSettings()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, s_privateKey).WithAppSlug("my-app", "contoso");

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.Equal("https://github.com/organizations/contoso/settings/apps/my-app", snapshot.Urls[0].Url);
        Assert.Equal(
            new ResourcePropertySnapshot("github.app.owner", "contoso") { DisplayName = "Organization", IsHighlighted = true, SortOrder = 4 },
            snapshot.Properties[4]);
    }

    [Fact]
    public async Task Initialize_SecretAppId_IsShownAsSensitive()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddExistingGitHubApp(
            "github-app",
            builder.AddParameter("app-id", AppId, secret: true),
            builder.AddParameter("client-id", ClientId),
            builder.AddParameter("private-key", s_privateKey, secret: true));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.True(snapshot.Properties.Single(p => p.Name == "github.app.id").IsSensitive);
        Assert.False(snapshot.Properties.Single(p => p.Name == "github.client.id").IsSensitive);
    }

    [Fact]
    public async Task Initialize_RaisesBeforeResourceStartedEvent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, s_privateKey);
        var raised = false;
        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(gitHubApp.Resource, (_, _) =>
        {
            raised = true;
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
        Assert.True(raised);
    }

    [Fact]
    public async Task Initialize_InvalidValues_FailsToStartAndLogsEveryProblemWithoutValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddExistingGitHubApp(
            "github-app",
            builder.AddParameter("app-id", "Iv23liSecretLooking"),
            builder.AddParameter("client-id", "654321"),
            builder.AddParameter("private-key", "secret-private-key", secret: true),
            builder.AddParameter("client-secret", "", secret: true),
            builder.AddParameter("webhook-secret", "secret-webhook", secret: true));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Empty(snapshot.Urls);
        Assert.Empty(snapshot.Properties);
        Assert.Equal(new (string, bool)[]
        {
            ("Parameter 'app-id' does not contain a valid App ID. Expected the numeric App ID shown on the app's settings page, such as '123456'. The client ID, which starts with 'Iv', is a different value.", true),
            ("Parameter 'client-id' does not contain a valid Client ID. Expected the Client ID shown on the app's settings page, such as 'Iv23liAbCdEf01234567'. A numeric value is the App ID.", true),
            ("Parameter 'private-key' does not contain a valid private key. Expected the contents of the .pem file generated on the app's settings page, which starts with '-----BEGIN RSA PRIVATE KEY-----'.", true),
            ("Parameter 'client-secret' does not contain a valid client secret. No value was provided.", true)
        }, await ReadLogsAsync(app, gitHubApp.Resource));
    }

    [Fact]
    public async Task Initialize_MissingParameterValue_FailsToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = builder.AddExistingGitHubApp(
            "github-app",
            builder.AddParameter("app-id", AppId),
            builder.AddParameter("client-id", ClientId),
            builder.AddParameter("missing-private-key", secret: true));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        var log = Assert.Single(await ReadLogsAsync(app, gitHubApp.Resource));
        Assert.True(log.IsErrorMessage);
        Assert.StartsWith("Failed to get the value of the private key parameter 'missing-private-key'.", log.Message);
    }

    [Fact]
    public async Task Initialize_BeforeResourceStartedSubscriberFails_FailsToStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, s_privateKey);
        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(gitHubApp.Resource, (_, _) => throw new InvalidOperationException("The callback failed."));

        using var app = builder.Build();
        var snapshot = await InitializeAsync(app, gitHubApp.Resource);

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        var log = Assert.Single(await ReadLogsAsync(app, gitHubApp.Resource));
        Assert.StartsWith(
            $"A BeforeResourceStartedEvent subscriber failed.\n{typeof(InvalidOperationException).FullName}: The callback failed.",
            log.Message);
    }

    private static IResourceBuilder<GitHubAppResource> AddGitHubApp(IDistributedApplicationBuilder builder, string privateKey)
    {
        return builder.AddExistingGitHubApp(
            "github-app",
            builder.AddParameter("app-id", AppId),
            builder.AddParameter("client-id", ClientId),
            builder.AddParameter("private-key", privateKey, secret: true));
    }

    private static string CreatePrivateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    // The orchestrator raises InitializeResourceEvent when the app starts. Raising it directly runs the resource's
    // initialization without starting the orchestrator or DCP.
    private static async Task<CustomResourceSnapshot> InitializeAsync(DistributedApplication app, IResource resource)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();

        await eventing.PublishAsync(new InitializeResourceEvent(resource, eventing, loggerService, notifications, app.Services));

        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        return resourceEvent.Snapshot;
    }

    // Reads the lines logged to the resource's console. Each line has the form
    //   2026-10-02T18:34:22.1234567Z Parameter 'app-id' does not contain a valid App ID. Expected ...
    // The timestamp contains no spaces, so the message starts after the first space. A message logged with an exception
    // stays one line, with "\n" and the exception's ToString() appended to it.
    private static async Task<(string Message, bool IsErrorMessage)[]> ReadLogsAsync(DistributedApplication app, IResource resource)
    {
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // A watch first returns everything logged so far as one batch, then waits for new lines. Only call this after
        // something was logged, because otherwise the first read waits until the timeout.
        await using var batches = loggerService.WatchAsync(resource).GetAsyncEnumerator(cts.Token);
        Assert.True(await batches.MoveNextAsync());

        return [.. batches.Current.Select(line => (line.Content[(line.Content.IndexOf(' ') + 1)..], line.IsErrorMessage))];
    }
}
