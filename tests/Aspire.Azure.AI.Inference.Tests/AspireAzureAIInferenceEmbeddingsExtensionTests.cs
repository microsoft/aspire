// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.Inference;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
