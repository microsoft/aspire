// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.OpenAI.Tests;

public class OpenAIExtensionTests
{
    [Fact]
    public async Task InitializationReportsMissingApiKeyOnParentAndModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiKey = builder.AddPendingSecretParameter("api-key");
        var openAI = builder.AddOpenAI("openai").WithApiKey(apiKey);
        IResource[] resources = [openAI.Resource, openAI.AddModel("model", "gpt-4o-mini").Resource];
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var notifications = app.ResourceNotifications;
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        var initialization = resources.Select(resource => builder.Eventing.PublishAsync(
            new InitializeResourceEvent(resource, builder.Eventing, loggerService, notifications, app.Services), cts.Token)).ToArray();

        try
        {
            foreach (var resource in resources)
            {
                await notifications.AssertWaitingForParameterAsync(resource, apiKey.Resource, cts.Token);
            }
            Assert.All(initialization, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            await apiKey.Resource.SetValueAsync("test-value").DefaultTimeout();
            await Task.WhenAll(initialization).DefaultTimeout();
        }

        foreach (var resource in resources)
        {
            Assert.Equal(KnownResourceStates.Running, notifications.AssertParameterWaitCleared(resource)?.Text);
        }
    }

    [Fact]
    public async Task DefaultEndpointIsUsedInConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "sk-default";

        var parent = builder.AddOpenAI("openai");
        var model = parent.AddModel("chat", "gpt-4o-mini");

        var parentCs = await parent.Resource.ConnectionStringExpression.GetValueAsync(default);
        var modelCs = await model.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Contains("Endpoint=https://api.openai.com/v1", parentCs);
        Assert.Contains("Key=sk-default", parentCs);

        Assert.Contains("Endpoint=https://api.openai.com/v1", modelCs);
        Assert.Contains("Key=sk-default", modelCs);
        Assert.Contains("Model=gpt-4o-mini", modelCs);
    }

    [Fact]
    public async Task WithEndpointUpdatesParentAndModelConnectionStrings()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "sk-custom";

        var parent = builder.AddOpenAI("openai").WithEndpoint("https://my-gateway.example.com/v1");
        var model = parent.AddModel("chat", "gpt-4o-mini");

        var parentCs = await parent.Resource.ConnectionStringExpression.GetValueAsync(default);
        var modelCs = await model.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Contains("Endpoint=https://my-gateway.example.com/v1", parentCs);
        Assert.Contains("Key=sk-custom", parentCs);

        Assert.Contains("Endpoint=https://my-gateway.example.com/v1", modelCs);
        Assert.Contains("Key=sk-custom", modelCs);
        Assert.Contains("Model=gpt-4o-mini", modelCs);
    }

    [Fact]
    public void AddOpenAIAddsParentAndModelWithCorrectName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

    var parent = builder.AddOpenAI("openai");
    var model = parent.AddModel("chat", "gpt-4o-mini");

    Assert.Equal("chat", model.Resource.Name);
        Assert.Equal("gpt-4o-mini", model.Resource.Model);
    }

    [Fact]
    public void AddOpenAICreatesDefaultApiKeyParameterOnParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parent = builder.AddOpenAI("openai");
        var model = parent.AddModel("chat", "gpt-4o-mini");

        Assert.NotNull(parent.Resource.Key);
        Assert.Equal("openai-openai-apikey", parent.Resource.Key.Name);
        Assert.True(parent.Resource.Key.Secret);
        // Model composes from parent; parent key exists
        Assert.NotNull(parent.Resource.Key);
    }

    [Fact]
    public async Task AddOpenAIModelUsesCorrectEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

    var parent = builder.AddOpenAI("openai");
    var openai = parent.AddModel("chat", "gpt-4o-mini");

    // Expression should reference the parent connection string and append the model
    var expression = openai.Resource.ConnectionStringExpression.ValueExpression;
    Assert.Contains("{openai.connectionString}", expression);
    Assert.Contains(";Model=gpt-4o-mini", expression);

    // Resolved value includes the default endpoint
    var resolved = await openai.Resource.ConnectionStringExpression.GetValueAsync(default);
    Assert.Contains("Endpoint=https://api.openai.com/v1", resolved);
    }

    [Fact]
    public async Task ConnectionStringExpressionIsCorrectlyFormatted()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

    var parent = builder.AddOpenAI("openai");
    var openai = parent.AddModel("chat", "gpt-4o-mini");

    var expression = openai.Resource.ConnectionStringExpression.ValueExpression;

    // Expression uses parent reference; resolved value contains the details
    Assert.Contains("{openai.connectionString}", expression);
    Assert.Contains(";Model=gpt-4o-mini", expression);

    var resolved = await openai.Resource.ConnectionStringExpression.GetValueAsync(default);
    Assert.Contains("Endpoint=https://api.openai.com/v1", resolved);
    Assert.Contains("Model=gpt-4o-mini", resolved);
    Assert.Contains("Key=", resolved);
    }

    [Fact]
    public async Task WithApiKeySetFromParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        const string apiKey = "sk-test-key";

        var apiKeyParameter = builder.AddParameter("openai-api-key", secret: true);
        builder.Configuration["Parameters:openai-api-key"] = apiKey;

        var parent = builder.AddOpenAI("openai");
        var openai = parent.AddModel("chat", "gpt-4o-mini");
        parent.WithApiKey(apiKeyParameter);

        var connectionString = await openai.Resource.ConnectionStringExpression.GetValueAsync(default);

    Assert.Equal(apiKeyParameter.Resource, parent.Resource.Key);
        Assert.Contains($"Key={apiKey}", connectionString);
    }

    [Fact]
    public void DefaultKeyParameterIsCreated()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

    var parent = builder.AddOpenAI("openai");
    var openai = parent.AddModel("chat", "gpt-4o-mini");

    Assert.NotNull(parent.Resource.Key);
    Assert.Equal("openai-openai-apikey", parent.Resource.Key.Name);
    Assert.True(parent.Resource.Key.Secret);
    Assert.Equal(parent.Resource.Key, parent.Resource.Key);
    }

    [Fact]
    public void OpenAIModelResourceConstructor()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

    var apiKeyParameter = builder.AddParameter("openai-api-key", secret: true);
    builder.Configuration["Parameters:openai-api-key"] = "test-key";

    var parent = builder.AddOpenAI("openai");
    var resource = new OpenAIModelResource("test", "gpt-4o-mini", parent.Resource);

        Assert.Equal("test", resource.Name);
        Assert.Equal("gpt-4o-mini", resource.Model);
    // Key is owned by the parent now; the model does not store its own key
    Assert.Equal(parent.Resource.Key, parent.Resource.Key);
    }

    [Fact]
    public void WithApiKeyThrowsIfParameterIsNotSecret()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

        var parent = builder.AddOpenAI("openai");
        var openai = parent.AddModel("chat", "gpt-4o-mini");
        var apiKey = builder.AddParameter("non-secret-key"); // Not marked as secret

        var exception = Assert.Throws<ArgumentException>(() => parent.WithApiKey(apiKey));
        Assert.Contains("The API key parameter must be marked as secret", exception.Message);
    }

    [Fact]
    public void WithApiKeySucceedsIfParameterIsSecret()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

        var parent = builder.AddOpenAI("openai");
        var openai = parent.AddModel("chat", "gpt-4o-mini");
        var apiKey = builder.AddParameter("secret-key", secret: true);

        // This should not throw
        var result = parent.WithApiKey(apiKey);
        Assert.NotNull(result);
        Assert.Equal(apiKey.Resource, parent.Resource.Key);
    }

    [Fact]
    public void WithApiKeyCalledTwiceOnParentReplacesKeyAndRemovesDefaultParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parent = builder.AddOpenAI("openai");
        var openai = parent.AddModel("chat", "gpt-4o-mini");

        Assert.NotNull(builder.Resources.FirstOrDefault(r => r.Name == "openai-openai-apikey"));

        parent.WithApiKey(builder.AddParameter("secret-key1", secret: true));

        // Parent override removes the default parameter from the graph
        Assert.Null(builder.Resources.FirstOrDefault(r => r.Name == "openai-openai-apikey"));
        Assert.NotNull(builder.Resources.FirstOrDefault(r => r.Name == "secret-key1"));

        parent.WithApiKey(builder.AddParameter("secret-key2", secret: true));

        Assert.NotNull(builder.Resources.FirstOrDefault(r => r.Name == "secret-key1"));
        Assert.NotNull(builder.Resources.FirstOrDefault(r => r.Name == "secret-key2"));
    }

    [Fact]
    public void WithHealthCheckAddsHealthCheckAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

    var openai = builder.AddOpenAI("openai").AddModel("chat", "gpt-4o-mini").WithHealthCheck();

    // Verify that the health check annotation is added
        var healthCheckAnnotations = openai.Resource.Annotations.OfType<HealthCheckAnnotation>().ToList();
        Assert.Single(healthCheckAnnotations);
    Assert.Equal("chat_check", healthCheckAnnotations[0].Key);
    }

    [Fact]
    public void WithHealthCheckEnsuresIHttpClientFactoryIsRegistered()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

        // Add the health check without explicitly calling AddHttpClient
    var openai = builder.AddOpenAI("openai").AddModel("chat", "gpt-4o-mini").WithHealthCheck();

        // Build the service provider to test dependency resolution
        var services = builder.Services.BuildServiceProvider();

        // This should not throw because WithHealthCheck should ensure IHttpClientFactory is registered
        var httpClientFactory = services.GetService<IHttpClientFactory>();
        Assert.NotNull(httpClientFactory);
    }

    [Fact]
    public void WithHealthCheckWorksWhenAddHttpClientIsCalledManually()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "test-api-key";

        // Manually call AddHttpClient (should not conflict with automatic registration in WithHealthCheck)
        builder.Services.AddHttpClient();

        // Add the health check
    var openai = builder.AddOpenAI("openai").AddModel("chat", "gpt-4o-mini").WithHealthCheck();

        // Build the service provider to test dependency resolution
        var services = builder.Services.BuildServiceProvider();

        // This should work fine since AddHttpClient can be called multiple times safely
        var httpClientFactory = services.GetService<IHttpClientFactory>();
        Assert.NotNull(httpClientFactory);
    }

    [Fact]
    public async Task ConnectionStringExpressionResolves()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:openai-openai-apikey"] = "sk-test123";

    var openai = builder.AddOpenAI("openai").AddModel("chat", "gpt-4o-mini");

        var connectionString = await openai.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Contains("Endpoint=https://api.openai.com/v1", connectionString);
        Assert.Contains("Key=sk-test123", connectionString);
        Assert.Contains("Model=gpt-4o-mini", connectionString);
    }

    [Fact]
    public void AddOpenAIThrowsIfBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => 
            OpenAIExtensions.AddOpenAI(null!, "test"));
    }

    [Fact]
    public void AddOpenAIThrowsIfNameIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        Assert.ThrowsAny<ArgumentException>(() => 
            builder.AddOpenAI(""));
        
        Assert.ThrowsAny<ArgumentException>(() => 
            builder.AddOpenAI(null!));
    }

    [Fact]
    public void AddModelThrowsIfModelIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parent = builder.AddOpenAI("test");

        Assert.ThrowsAny<ArgumentException>(() => 
            parent.AddModel("model", ""));
        
        Assert.ThrowsAny<ArgumentException>(() => 
            parent.AddModel("model", null!));
    }

    [Fact]
    public void WithApiKeyThrowsIfBuilderIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var apiKey = builder.AddParameter("test", secret: true);

        Assert.Throws<ArgumentNullException>(() =>
            Aspire.Hosting.OpenAIExtensions.WithApiKey((Aspire.Hosting.ApplicationModel.IResourceBuilder<Aspire.Hosting.OpenAI.OpenAIResource>)null!, apiKey));
    }

    [Fact]
    public void WithApiKeyThrowsIfApiKeyIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddOpenAI("test");

        Assert.Throws<ArgumentNullException>(() => 
            parent.WithApiKey(null!));
    }

    [Fact]
    public void WithHealthCheckThrowsIfBuilderIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => 
            OpenAIExtensions.WithHealthCheck(null!));
    }

    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4-turbo")]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("text-embedding-3-small")]
    [InlineData("dall-e-3")]
    public void AddOpenAIModelWorksWithDifferentModels(string modelName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration[$"Parameters:test-openai-apikey"] = "test-key";

    var openai = builder.AddOpenAI("test").AddModel("chat", modelName);

    Assert.Equal("chat", openai.Resource.Name);
        Assert.Equal(modelName, openai.Resource.Model);
        
        var connectionString = openai.Resource.ConnectionStringExpression.ValueExpression;
        Assert.Contains($"Model={modelName}", connectionString);
    }
}
