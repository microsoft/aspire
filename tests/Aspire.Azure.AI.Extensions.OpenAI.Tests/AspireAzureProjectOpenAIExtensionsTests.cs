// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text.Json;
using Aspire.Components.TestUtilities;
using Azure.AI.Extensions.OpenAI;
using Azure.Core.Extensions;
using Json.Schema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Azure.AI.Extensions.OpenAI.Tests;

public class AspireAzureProjectOpenAIExtensionsTests
{
    private const string Endpoint = "https://test.services.ai.azure.com/api/projects/test";
    private const string Section = "Aspire:Azure:AI:Extensions:OpenAI";

    [Fact]
    public void ValidatesArguments()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        Assert.Throws<ArgumentNullException>(() => AspireAzureProjectOpenAIExtensions.AddAzureProjectOpenAIClient(null!, "project"));
        Assert.Throws<ArgumentNullException>(() => AspireAzureProjectOpenAIExtensions.AddKeyedAzureProjectOpenAIClient(null!, "project"));
        Assert.Throws<ArgumentException>(() => builder.AddAzureProjectOpenAIClient(""));
        Assert.Throws<ArgumentException>(() => builder.AddKeyedAzureProjectOpenAIClient(""));
    }

    [Theory]
    [InlineData("""{"Endpoint":"https://test.services.ai.azure.com/api/projects/test","DisableTracing":false,"DisableMetrics":false,"EnableSensitiveTelemetryData":false,"ClientOptions":{"NetworkTimeout":"00:00:30"}}""", true)]
    [InlineData("""{"Endpoint":"relative"}""", false)]
    [InlineData("""{"DisableMetrics":"true"}""", false)]
    public void ConfigurationSchemaValidatesSettings(string settings, bool valid)
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json"),
            new BuildOptions { Dialect = Dialect.Draft07, SchemaRegistry = new SchemaRegistry() });
        using var configuration = JsonDocument.Parse("""{"Aspire":{"Azure":{"AI":{"Extensions":{"OpenAI":""" + settings + "}}}}}");
        Assert.Equal(valid, schema.Evaluate(configuration.RootElement, new EvaluationOptions { RequireFormatValidation = true }).IsValid);
    }

    [Theory]
    [InlineData(false, false, "root")]
    [InlineData(true, false, "named")]
    [InlineData(false, true, "connection")]
    [InlineData(true, true, "connection")]
    public void NamedSettingsAndConnectionStringsOverrideRoot(bool named, bool connectionString, string expected)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration[$"{Section}:Endpoint"] = "https://root.services.ai.azure.com/api/projects/test";
        if (named)
        {
            builder.Configuration[$"{Section}:project:Endpoint"] = "https://named.services.ai.azure.com/api/projects/test";
        }
        if (connectionString)
        {
            builder.Configuration["ConnectionStrings:project"] = "Endpoint=https://connection.services.ai.azure.com/api/projects/test";
        }

        builder.AddAzureProjectOpenAIClient("project", settings => Assert.Equal($"{expected}.services.ai.azure.com", settings.Endpoint!.Host));
    }

    [Theory]
    [InlineData(false, Endpoint)]
    [InlineData(true, Endpoint)]
    [InlineData(false, "Endpoint=" + Endpoint)]
    [InlineData(true, "Endpoint=" + Endpoint)]
    public void RegistersConcreteClientAndAlias(bool keyed, string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:project"] = connectionString;
        Register(builder, keyed);
        using var host = builder.Build();

        var client = Resolve(host.Services, keyed);
        var alias = keyed ? host.Services.GetRequiredKeyedService<OpenAIClient>("project") : host.Services.GetRequiredService<OpenAIClient>();
        Assert.Same(client, alias);
        Assert.Same(client, Resolve(host.Services, keyed));
        if (keyed)
        {
            Assert.Null(host.Services.GetService<ProjectOpenAIClient>());
            Assert.Null(host.Services.GetService<OpenAIClient>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationPrecedenceAndClientCustomization(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Section}:Endpoint"] = "https://root.services.ai.azure.com/api/projects/root",
            [$"{Section}:project:Endpoint"] = "https://named.services.ai.azure.com/api/projects/named",
            ["ConnectionStrings:project"] = $"Endpoint={Endpoint}",
            [$"{Section}:ClientOptions:NetworkTimeout"] = "00:00:01",
            [$"{Section}:project:ClientOptions:NetworkTimeout"] = "00:00:02"
        });
        var settingsCredential = new FoundryTestTokenCredential("settings-token");
        var clientCredential = new FoundryTestTokenCredential("client-token");
        using var handler = new FoundryTestHttpMessageHandler("""{"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"test","output":[]}""");
        using var httpClient = new HttpClient(handler);

        void ConfigureSettings(AzureProjectOpenAISettings settings)
        {
            Assert.Equal(new Uri(Endpoint), settings.Endpoint);
            settings.Endpoint = new("https://callback.services.ai.azure.com/api/projects/callback");
            settings.Credential = settingsCredential;
        }

        void ConfigureClient(IAzureClientBuilder<ProjectOpenAIClient, ProjectOpenAIClientOptions> clientBuilder)
        {
            clientBuilder.WithCredential(clientCredential).ConfigureOptions(options =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), options.NetworkTimeout);
                options.UserAgentApplicationId = "callback-app";
                options.Transport = new HttpClientPipelineTransport(httpClient);
            });
        }

        if (keyed)
        {
            builder.AddKeyedAzureProjectOpenAIClient("project", ConfigureSettings, ConfigureClient);
        }
        else
        {
            builder.AddAzureProjectOpenAIClient("project", ConfigureSettings, ConfigureClient);
        }

        using var host = builder.Build();
        await Resolve(host.Services, keyed).GetProjectResponsesClientForModel("test").CreateResponseAsync("hello");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/callback/openai/v1/responses", request.Uri.AbsolutePath);
        Assert.Equal("Bearer client-token", request.Authorization);
        Assert.Contains("callback-app", request.UserAgent);
        Assert.Empty(settingsCredential.RequestedScopes);
        Assert.NotEmpty(clientCredential.RequestedScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingEndpointFailsAtResolution(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        Register(builder, keyed);
        using var host = builder.Build();
        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(host.Services, keyed));
        Assert.Contains("ConnectionStrings:project", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsCanSupplyEndpointWithoutConnectionString(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        if (keyed)
        {
            builder.AddKeyedAzureProjectOpenAIClient("project", settings => settings.Endpoint = new(Endpoint));
        }
        else
        {
            builder.AddAzureProjectOpenAIClient("project", settings => settings.Endpoint = new(Endpoint));
        }

        using var host = builder.Build();
        Assert.NotNull(Resolve(host.Services, keyed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistersChatAndEmbeddingClients(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:project"] = $"Endpoint={Endpoint};Deployment=chat-model";
        if (keyed)
        {
            var clientBuilder = builder.AddKeyedAzureProjectOpenAIClient("project");
            clientBuilder.AddKeyedChatClient("chat");
            clientBuilder.AddKeyedEmbeddingGenerator("embeddings", "embedding-model");
        }
        else
        {
            var clientBuilder = builder.AddAzureProjectOpenAIClient("project");
            clientBuilder.AddChatClient();
            clientBuilder.AddEmbeddingGenerator("embedding-model");
        }

        using var host = builder.Build();
        var chatClient = keyed ? host.Services.GetRequiredKeyedService<IChatClient>("chat") : host.Services.GetRequiredService<IChatClient>();
        var embeddings = keyed ? host.Services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("embeddings") : host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.Equal("chat-model", chatClient.GetService<ChatClientMetadata>()!.DefaultModelId);
        Assert.Equal("embedding-model", embeddings.GetService<EmbeddingGeneratorMetadata>()!.DefaultModelId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Endpoint=relative")]
    [InlineData("Endpoint=https://test.services.ai.azure.com/api/projects/test;Key=secret")]
    [InlineData("ftp://test/project")]
    public void RejectsInvalidOrApiKeyConnectionStrings(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:project"] = connectionString;
        Assert.Throws<ArgumentException>(() => builder.AddAzureProjectOpenAIClient("project"));
    }

    [Fact]
    public void KeyedClientsAreIndependent()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddKeyedAzureProjectOpenAIClient("first", settings => settings.Endpoint = new(Endpoint));
        builder.AddKeyedAzureProjectOpenAIClient("second", settings => settings.Endpoint = new(Endpoint));
        using var host = builder.Build();
        var first = host.Services.GetRequiredKeyedService<ProjectOpenAIClient>("first");
        var second = host.Services.GetRequiredKeyedService<ProjectOpenAIClient>("second");

        Assert.NotSame(first, second);
        Assert.Same(first, host.Services.GetRequiredKeyedService<OpenAIClient>("first"));
        Assert.Same(second, host.Services.GetRequiredKeyedService<OpenAIClient>("second"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatClientUsesProjectEndpointAndOptionalTelemetry(bool enabled)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        using var handler = new FoundryTestHttpMessageHandler("""
            {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"chat",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """);
        using var httpClient = new HttpClient(handler);
        using var exporter = new FoundryTestActivityExporter();
        builder.AddAzureProjectOpenAIClient("project", settings =>
        {
            settings.Endpoint = new(Endpoint);
            settings.Credential = new FoundryTestTokenCredential();
            settings.DisableTracing = !enabled;
            settings.DisableMetrics = !enabled;
        }, client => client.ConfigureOptions(options => options.Transport = new HttpClientPipelineTransport(httpClient)))
            .AddChatClient("chat");
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        using var host = builder.Build();
        host.Services.GetRequiredService<TracerProvider>();

        var response = await host.Services.GetRequiredService<IChatClient>().GetResponseAsync("hello");

        Assert.Equal("Hello", response.Text);
        Assert.Equal("/api/projects/test/openai/v1/chat/completions", Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(enabled, host.Services.GetService<MeterProvider>() is not null);
        if (enabled)
        {
            Assert.Contains(exporter.Activities, activity => activity.Source.Name is "Experimental.Microsoft.Extensions.AI" or "Microsoft.Extensions.AI");
        }
        else
        {
            Assert.Empty(exporter.Activities);
        }
    }

    [Fact]
    public async Task ResponsesAdapterSupportsFunctionTools()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        using var handler = new FoundryTestHttpMessageHandler("""
            {"id":"resp_test","object":"response","created_at":1,"status":"completed","model":"chat",
             "output":[{"id":"msg_test","type":"message","status":"completed","role":"assistant",
             "content":[{"type":"output_text","text":"Hello","annotations":[]}]}]}
            """);
        using var httpClient = new HttpClient(handler);
        builder.AddAzureProjectOpenAIClient("project", settings =>
        {
            settings.Endpoint = new(Endpoint);
            settings.Credential = new FoundryTestTokenCredential();
        }, client => client.ConfigureOptions(options => options.Transport = new HttpClientPipelineTransport(httpClient)));
        using var host = builder.Build();
        using var chat = host.Services.GetRequiredService<ProjectOpenAIClient>()
            .GetProjectResponsesClientForModel("chat").AsIChatClient();

        var response = await chat.GetResponseAsync("hello", new ChatOptions
        {
            // The adapter does not infer the model from ProjectResponsesClient's SDK defaults.
            ModelId = "chat",
            Tools = [AIFunctionFactory.Create(() => "sunny", name: "weather")]
        });

        Assert.Equal("Hello", response.Text);
        Assert.Equal("/api/projects/test/openai/v1/responses", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    private static void Register(HostApplicationBuilder builder, bool keyed)
    {
        if (keyed)
        {
            builder.AddKeyedAzureProjectOpenAIClient("project");
        }
        else
        {
            builder.AddAzureProjectOpenAIClient("project");
        }
    }

    private static ProjectOpenAIClient Resolve(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<ProjectOpenAIClient>("project") : services.GetRequiredService<ProjectOpenAIClient>();
}
