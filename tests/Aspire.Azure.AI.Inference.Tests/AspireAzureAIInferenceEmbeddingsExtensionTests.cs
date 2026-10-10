// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.Inference;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Azure.AI.Inference.Tests;

public class AspireAzureAIInferenceEmbeddingsExtensionTests
{
    private const string ConnectionString = "Endpoint=https://fakeendpoint;Key=fakekey;DeploymentId=deployment";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", ConnectionString)
        ]);
        if (useKeyed)
        {
            builder.AddKeyedAzureEmbeddingsClient("embedding");
        }
        else
        {
            builder.AddAzureEmbeddingsClient("embedding");
        }
        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetKeyedService<EmbeddingsClient>("embedding") :
            host.Services.GetService<EmbeddingsClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", "Endpoint=https://endpoint;Key=myAccount;DeploymentId=unused")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureEmbeddingsClient("embedding", settings => settings.ConnectionString = ConnectionString);
        }
        else
        {
            builder.AddAzureEmbeddingsClient("embedding", settings => settings.ConnectionString = ConnectionString);
        }

        using var host = builder.Build();

        var client = useKeyed ?
            host.Services.GetKeyedService<EmbeddingsClient>("embedding") :
            host.Services.GetService<EmbeddingsClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding1", ConnectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:embedding2", ConnectionString + "2")
        ]);
        builder.AddKeyedAzureEmbeddingsClient("embedding1");
        builder.AddKeyedAzureEmbeddingsClient("embedding2");
        using var host = builder.Build();
        var client1 = host.Services.GetKeyedService<EmbeddingsClient>("embedding1");
        var client2 = host.Services.GetKeyedService<EmbeddingsClient>("embedding2");
        Assert.NotNull(client1);
        Assert.NotNull(client2);

        Assert.NotSame(client1, client2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanRegisterAsAnIChatClient(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", ConnectionString)
        ]);
        if (useKeyed)
        {
            builder.AddKeyedAzureEmbeddingsClient("embedding").AddKeyedEmbeddingGenerator("embedding");
        }
        else
        {
            builder.AddAzureEmbeddingsClient("embedding").AddEmbeddingGenerator();
        }
        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("embedding") :
            host.Services.GetService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddChatClientUsesCustomDeploymentId(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", ConnectionString)
        ]);
        if (useKeyed)
        {
            builder.AddKeyedAzureEmbeddingsClient("embedding").AddKeyedEmbeddingGenerator("embedding", deploymentName: "other");
        }
        else
        {
            builder.AddAzureEmbeddingsClient("embedding").AddEmbeddingGenerator(deploymentName: "other");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetKeyedService<IEmbeddingGenerator>("embedding") :
            host.Services.GetService<IEmbeddingGenerator>();

        var metadata = client?.GetService<EmbeddingGeneratorMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal("other", metadata?.DefaultModelId);
    }

    [Theory]
    [InlineData("https://account.services.ai.azure.com/models", true)]
    [InlineData("https://account.openai.azure.com/openai/deployments/model", false)]
    [InlineData("https://account.openai.azure.us/openai/deployments/model", false)]
    [InlineData("https://account.openai.azure.cn/openai/deployments/model", false)]
    [InlineData("https://account.openai.azure.de/openai/deployments/model", false)]
    [InlineData("https://account.services.ai.azure.com/OpenAI/v1", false)]
    [InlineData("http://127.0.0.1:50920/", false)]         // Foundry Local (IPv4 loopback)
    [InlineData("http://127.0.0.1:50920/v1", false)]       // Foundry Local with /v1 path
    [InlineData("http://[::1]:50920/", false)]             // Foundry Local (IPv6 loopback)
    [InlineData("http://localhost:11434/v1", false)]       // Ollama-style local server
    public void HealthCheckRegistrationMatchesEndpointSupport(string endpoint, bool expected)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", $"Endpoint={endpoint};Key=fakekey;Model=model")
        ]);

        builder.AddAzureEmbeddingsClient("embedding");

        using var host = builder.Build();

        Assert.Equal(expected, host.Services.GetService<HealthCheckService>() is not null);
    }

    [Theory]
    [InlineData(200, HealthStatus.Healthy)]
    [InlineData(500, HealthStatus.Unhealthy)]
    public async Task HealthCheckReturnsExpectedStatus(int responseStatus, HealthStatus expectedStatus)
    {
        var transport = new MockTransport(_ => CreateResponse(responseStatus));
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", "Endpoint=https://account.services.ai.azure.com/models;Key=fakekey;Model=model")
        ]);

        builder.AddAzureEmbeddingsClient(
            "embedding",
            configureClientBuilder: clientBuilder => clientBuilder.ConfigureOptions(options =>
            {
                options.Transport = transport;
                options.Retry.MaxRetries = 0;
            }));

        using var host = builder.Build();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync();

        Assert.Equal(expectedStatus, Assert.Single(report.Entries).Value.Status);
        Assert.Equal("/models/info", Assert.Single(transport.Requests).Uri.Path);
    }

    private static MockResponse CreateResponse(int status)
    {
        var response = new MockResponse(status).SetContent("""
            {
              "model_name": "model",
              "model_type": "embeddings",
              "model_provider_name": "provider"
            }
            """);
        response.AddHeader(new HttpHeader("Content-Type", "application/json"));

        return response;
    }

    [Theory]
    [InlineData("Deployment")]
    [InlineData("DeploymentId")]
    [InlineData("Model")]
    public void EmbeddingsClientSettings_AcceptsSingleDeploymentKey(string keyName)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionString = $"Endpoint=https://fakeendpoint;Key=fakekey;{keyName}=testdeployment";
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", connectionString)
        ]);

        builder.AddAzureEmbeddingsClient("embedding");

        using var host = builder.Build();
        var client = host.Services.GetService<EmbeddingsClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("Deployment", "DeploymentId")]
    [InlineData("Deployment", "Model")]
    [InlineData("DeploymentId", "Model")]
    public void EmbeddingsClientSettings_RejectsMultipleDeploymentKeys(string key1, string key2)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionString = $"Endpoint=https://fakeendpoint;Key=fakekey;{key1}=value1;{key2}=value2";
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:embedding", connectionString)
        ]);

        // The exception should be thrown during this call
        var ex = Assert.Throws<ArgumentException>(() => builder.AddAzureEmbeddingsClient("embedding"));
        Assert.Contains("multiple deployment/model keys", ex.Message);
        Assert.Contains(key1, ex.Message);
        Assert.Contains(key2, ex.Message);
    }
}
