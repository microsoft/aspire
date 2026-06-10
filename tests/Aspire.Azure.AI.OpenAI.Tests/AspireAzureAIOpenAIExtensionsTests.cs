// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.OpenAI;
using Azure.Core.Extensions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAI;
using Xunit;

namespace Aspire.Azure.AI.OpenAI.Tests;

public class AspireAzureAIOpenAIExtensionsTests
{
    private const string ConnectionString = "Endpoint=https://aspireopenaitests.openai.azure.com/;Key=fake";

    /// <summary>
    /// Azure OpenAI registers both <see cref="AzureOpenAIClient"/> and <see cref="OpenAIClient"/> services.
    /// This way consumers can use either service type to resolve the client.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegistersBothServiceTypes(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:openai", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureOpenAIClient("openai");
        }
        else
        {
            builder.AddAzureOpenAIClient("openai");
        }

        using var host = builder.Build();
        var azureClient = useKeyed ?
            host.Services.GetRequiredKeyedService<AzureOpenAIClient>("openai") :
            host.Services.GetRequiredService<AzureOpenAIClient>();

        var unbrandedClient = useKeyed ?
            host.Services.GetRequiredKeyedService<OpenAIClient>("openai") :
            host.Services.GetRequiredService<OpenAIClient>();

        Assert.Same(azureClient, unbrandedClient);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:openai", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureOpenAIClient("openai");
        }
        else
        {
            builder.AddAzureOpenAIClient("openai");
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<AzureOpenAIClient>("openai") :
            host.Services.GetRequiredService<AzureOpenAIClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var uri = new Uri("https://aspireopenaitests.openai.azure.com/");
        var key = "fake";
        var builder = Host.CreateEmptyApplicationBuilder(null);

        if (useKeyed)
        {
            builder.AddKeyedAzureOpenAIClient("openai", settings => { settings.Endpoint = uri; settings.Key = key; });
        }
        else
        {
            builder.AddAzureOpenAIClient("openai", settings => { settings.Endpoint = uri; settings.Key = key; });
        }

        using var host = builder.Build();
        var client = useKeyed ?
            host.Services.GetRequiredKeyedService<AzureOpenAIClient>("openai") :
            host.Services.GetRequiredService<AzureOpenAIClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("https://yourservice.openai.azure.com/")]
    [InlineData("http://domain:12345")]
    [InlineData("Endpoint=http://domain.com:12345;Key=abc123")]
    [InlineData("Endpoint=http://domain.com:12345")]
    public void ReadsFromConnectionStringsFormats(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:openai", connectionString)
        ]);

        builder.AddAzureOpenAIClient("openai");

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<AzureOpenAIClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:openai1", ConnectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:openai2", ConnectionString + "2"),
            new KeyValuePair<string, string?>("ConnectionStrings:openai3", ConnectionString + "3")
        ]);

        builder.AddAzureOpenAIClient("openai1");
        builder.AddKeyedAzureOpenAIClient("openai2");
        builder.AddKeyedAzureOpenAIClient("openai3");

        using var host = builder.Build();

        // Unkeyed services don't work with keyed services. See https://github.com/microsoft/aspire/issues/3890
        //var client1 = host.Services.GetRequiredService<OpenAIClient>();
        var client2 = host.Services.GetRequiredKeyedService<AzureOpenAIClient>("openai2");
        var client3 = host.Services.GetRequiredKeyedService<AzureOpenAIClient>("openai3");

        //Assert.NotSame(client1, client2);
        //Assert.NotSame(client1, client3);
        Assert.NotSame(client2, client3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BindsOptionsAndInvokesCallback(bool useKeyed)
    {
        var networkTimeout = TimeSpan.FromSeconds(123);
        var applicationId = "application_id";

        var key = useKeyed ? ":openai" : "";

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:openai", ConnectionString),
            new KeyValuePair<string, string?>($"Aspire:Azure:AI:OpenAI{key}:ClientOptions:UserAgentApplicationId", applicationId),
            // Ensure the callback wins over configuration
            new KeyValuePair<string, string?>($"Aspire:Azure:AI:OpenAI{key}:ClientOptions:NetworkTimeout", "00:00:02")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedAzureOpenAIClient("openai", configureClientBuilder: BuildConfiguration);
        }
        else
        {
            builder.AddAzureOpenAIClient("openai", configureClientBuilder: BuildConfiguration);
        }

        void BuildConfiguration(IAzureClientBuilder<AzureOpenAIClient, AzureOpenAIClientOptions> builder)
        {
            builder.ConfigureOptions(options =>
            {
                options.NetworkTimeout = networkTimeout;
            });
        }

        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptionsMonitor<AzureOpenAIClientOptions>>().Get(useKeyed ? "openai" : "Default");

        Assert.NotNull(options);
        Assert.Equal(applicationId, options.UserAgentApplicationId);
        Assert.Equal(networkTimeout, options.NetworkTimeout);
    }

    [Fact]
    public void AddAzureOpenAIClient_WithConnectionNameAndSettings_AppliesConnectionSpecificSettings()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "openaitest";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            [$"Aspire:Azure:AI:OpenAI:{connectionName}:DisableMetrics"] = "true",
            [$"Aspire:Azure:AI:OpenAI:{connectionName}:DisableTracing"] = "true"
        });

        AzureOpenAISettings? capturedSettings = null;
        builder.AddAzureOpenAIClient(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.True(capturedSettings.DisableMetrics);
        Assert.True(capturedSettings.DisableTracing);
    }

    [Fact]
    public void AddAzureOpenAIClient_WithConnectionSpecific_FavorsConnectionSpecificSettings()
    {
        // Arrange
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var connectionName = "openaitest";

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = ConnectionString,
            // General settings
            [$"Aspire:Azure:AI:OpenAI:DisableTracing"] = "false",
            // Connection-specific settings
            [$"Aspire:Azure:AI:OpenAI:{connectionName}:DisableTracing"] = "true",
        });

        AzureOpenAISettings? capturedSettings = null;
        builder.AddAzureOpenAIClient(connectionName, settings =>
        {
            capturedSettings = settings;
        });

        Assert.NotNull(capturedSettings);
        Assert.True(capturedSettings.DisableTracing);
    }
}
