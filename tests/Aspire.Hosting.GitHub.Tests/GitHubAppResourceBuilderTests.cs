// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.GitHub.Tests;

public class GitHubAppResourceBuilderTests
{
    private const string AppId = "123456";
    private const string ClientId = "Iv23liAbCdEf01234567";
    private const string PrivateKey = "test-private-key";
    private const string ClientSecret = "test-client-secret";
    private const string WebhookSecret = "test-webhook-secret";

    [Fact]
    public void AddExistingGitHubApp_StoresParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var appId = builder.AddParameter("app-id", AppId);
        var clientId = builder.AddParameter("client-id", ClientId);
        var privateKey = builder.AddParameter("private-key", PrivateKey, secret: true);
        var clientSecret = builder.AddParameter("client-secret", ClientSecret, secret: true);
        var webhookSecret = builder.AddParameter("webhook-secret", WebhookSecret, secret: true);

        var gitHubApp = builder.AddExistingGitHubApp("github-app", appId, clientId, privateKey, clientSecret, webhookSecret);

        Assert.Same(appId.Resource, gitHubApp.Resource.AppIdParameter);
        Assert.Same(clientId.Resource, gitHubApp.Resource.ClientIdParameter);
        Assert.Same(privateKey.Resource, gitHubApp.Resource.PrivateKeyParameter);
        Assert.Same(clientSecret.Resource, gitHubApp.Resource.ClientSecretParameter);
        Assert.Same(webhookSecret.Resource, gitHubApp.Resource.WebhookSecretParameter);
        Assert.Equal("https://api.github.com", gitHubApp.Resource.ApiUrl);
        Assert.Null(gitHubApp.Resource.Slug);
        Assert.Null(gitHubApp.Resource.Organization);
    }

    [Theory]
    [InlineData("privateKey")]
    [InlineData("clientSecret")]
    [InlineData("webhookSecret")]
    public void AddExistingGitHubApp_NonSecretCredential_Throws(string parameterName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var notSecret = builder.AddParameter("not-secret", "value");
        var secret = builder.AddParameter("secret", "value", secret: true);

        var ex = Assert.Throws<ArgumentException>(() => builder.AddExistingGitHubApp(
            "github-app",
            builder.AddParameter("app-id", AppId),
            builder.AddParameter("client-id", ClientId),
            parameterName == "privateKey" ? notSecret : secret,
            parameterName == "clientSecret" ? notSecret : secret,
            parameterName == "webhookSecret" ? notSecret : secret));

        Assert.Equal(parameterName, ex.ParamName);
    }

    [Fact]
    public void WithAppSlug_SetsSlugAndOrganization()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder).WithAppSlug("my-app", "contoso");

        Assert.Equal("my-app", gitHubApp.Resource.Slug);
        Assert.Equal("contoso", gitHubApp.Resource.Organization);
    }

    [Theory]
    [InlineData("My App", null, "slug")]
    [InlineData("my-app", "-contoso", "organization")]
    public void WithAppSlug_InvalidValue_Throws(string slug, string? organization, string parameterName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder);

        var ex = Assert.Throws<ArgumentException>(() => gitHubApp.WithAppSlug(slug, organization));
        Assert.Equal(parameterName, ex.ParamName);
    }

    [Fact]
    public async Task WithReference_NonDotnetResource_GetsGitHubAndProbotVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, withClientSecret: true, withWebhookSecret: true);
        var worker = builder.AddContainer("worker", "image").WithReference(gitHubApp);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["GITHUB_APP_ID"] = AppId,
            ["GITHUB_CLIENT_ID"] = ClientId,
            ["GITHUB_PRIVATE_KEY"] = PrivateKey,
            ["GITHUB_CLIENT_SECRET"] = ClientSecret,
            ["GITHUB_WEBHOOK_SECRET"] = WebhookSecret,
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["APP_ID"] = AppId,
            ["PRIVATE_KEY"] = PrivateKey,
            ["WEBHOOK_SECRET"] = WebhookSecret
        }, env);
    }

    [Fact]
    public async Task WithReference_DotnetResource_AlsoGetsConfigurationSection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder);
        var api = builder.AddResource(new ProjectResource("api")).WithReference(gitHubApp);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            api.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["GITHUB_APP_ID"] = AppId,
            ["GITHUB_CLIENT_ID"] = ClientId,
            ["GITHUB_PRIVATE_KEY"] = PrivateKey,
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["APP_ID"] = AppId,
            ["PRIVATE_KEY"] = PrivateKey,
            ["GitHub__App__AppId"] = AppId,
            ["GitHub__App__ClientId"] = ClientId,
            ["GitHub__App__PrivateKey"] = PrivateKey,
            ["GitHub__App__ApiUrl"] = "https://api.github.com"
        }, env);
    }

    [Fact]
    public async Task WithReference_ConnectionName_RenamesVariablesAndOmitsProbotVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder, withWebhookSecret: true);
        var worker = builder.AddContainer("worker", "image").WithReference(gitHubApp, connectionName: "orders-bot");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["ORDERS_BOT_APP_ID"] = AppId,
            ["ORDERS_BOT_CLIENT_ID"] = ClientId,
            ["ORDERS_BOT_PRIVATE_KEY"] = PrivateKey,
            ["ORDERS_BOT_WEBHOOK_SECRET"] = WebhookSecret,
            ["ORDERS_BOT_API_URL"] = "https://api.github.com"
        }, env);
    }

    [Fact]
    public async Task WithReference_DotnetResourceWithNestedConnectionName_UsesNestedSection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder);
        var api = builder.AddResource(new ProjectResource("api")).WithReference(gitHubApp, connectionName: "Bots:OrdersBot");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            api.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(new Dictionary<string, string>
        {
            ["BOTS_ORDERS_BOT_APP_ID"] = AppId,
            ["BOTS_ORDERS_BOT_CLIENT_ID"] = ClientId,
            ["BOTS_ORDERS_BOT_PRIVATE_KEY"] = PrivateKey,
            ["BOTS_ORDERS_BOT_API_URL"] = "https://api.github.com",
            ["Bots__OrdersBot__AppId"] = AppId,
            ["Bots__OrdersBot__ClientId"] = ClientId,
            ["Bots__OrdersBot__PrivateKey"] = PrivateKey,
            ["Bots__OrdersBot__ApiUrl"] = "https://api.github.com"
        }, env);
    }

    [Fact]
    public async Task WithReference_PublishMode_ReferencesParametersInsteadOfValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var gitHubApp = AddGitHubApp(builder);
        var worker = builder.AddContainer("worker", "image").WithReference(gitHubApp);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal("{app-id.value}", env["GITHUB_APP_ID"]);
        Assert.Equal("{private-key.value}", env["GITHUB_PRIVATE_KEY"]);
    }

    [Fact]
    public void WithReference_AddsReferenceRelationshipAndAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder);
        var worker = builder.AddContainer("worker", "image").WithReference(gitHubApp, "orders-bot");

        var relationship = Assert.Single(worker.Resource.Annotations.OfType<ResourceRelationshipAnnotation>());
        Assert.Same(gitHubApp.Resource, relationship.Resource);
        Assert.Equal("Reference", relationship.Type);

        var reference = Assert.Single(worker.Resource.Annotations.OfType<GitHubAppReferenceAnnotation>());
        Assert.Same(gitHubApp.Resource, reference.Source);
        Assert.Equal("orders-bot", reference.ConnectionName);
    }

    [Fact]
    public void WithReference_TwoAppsWithDefaultNames_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = AddGitHubApp(builder, "first");
        var second = AddGitHubApp(builder, "second");
        var worker = builder.AddContainer("worker", "image").WithReference(first);

        var ex = Assert.Throws<InvalidOperationException>(() => worker.WithReference(second));
        Assert.Equal(
            "Resource 'worker' can't reference GitHub App 'second' because its environment variable 'GITHUB_APP_ID' is already set by the reference to GitHub App 'first'. " +
            "Pass a different connectionName to WithReference for each app.",
            ex.Message);
    }

    [Fact]
    public void WithReference_ConnectionNamesThatDifferOnlyInCase_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = AddGitHubApp(builder, "first");
        var second = AddGitHubApp(builder, "second");
        var worker = builder.AddContainer("worker", "image").WithReference(first, "OrdersBot");

        Assert.Throws<InvalidOperationException>(() => worker.WithReference(second, "orders_bot"));
    }

    [Fact]
    public async Task WithReference_TwoAppsWithDifferentConnectionNames_GetsBoth()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = AddGitHubApp(builder, "first");
        var second = AddGitHubApp(builder, "second");
        var worker = builder.AddContainer("worker", "image")
            .WithReference(first)
            .WithReference(second, "release-bot")
            .WithReference(first);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(AppId, env["GITHUB_APP_ID"]);
        Assert.Equal(AppId, env["RELEASE_BOT_APP_ID"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void WithReference_EmptyConnectionName_Throws(string connectionName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var gitHubApp = AddGitHubApp(builder);
        var worker = builder.AddContainer("worker", "image");

        Assert.Throws<ArgumentException>(() => worker.WithReference(gitHubApp, connectionName));
    }

    private static IResourceBuilder<GitHubAppResource> AddGitHubApp(
        IDistributedApplicationBuilder builder,
        string name = "github-app",
        bool withClientSecret = false,
        bool withWebhookSecret = false)
    {
        // Parameter names must be unique, so apps after the first get their own.
        var suffix = name == "github-app" ? "" : $"-{name}";
        return builder.AddExistingGitHubApp(
            name,
            builder.AddParameter($"app-id{suffix}", AppId),
            builder.AddParameter($"client-id{suffix}", ClientId),
            builder.AddParameter($"private-key{suffix}", PrivateKey, secret: true),
            withClientSecret ? builder.AddParameter($"client-secret{suffix}", ClientSecret, secret: true) : null,
            withWebhookSecret ? builder.AddParameter($"webhook-secret{suffix}", WebhookSecret, secret: true) : null);
    }
}
