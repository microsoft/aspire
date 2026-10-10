// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text.Json;
using Aspire.Components.TestUtilities;
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

[Collection("OpenAI telemetry")]
public class AspireAzureOpenAIExtensionsTests
{
    private const string Endpoint = "https://test.openai.azure.com";
    private const string Section = "Aspire:Azure:AI:OpenAI";
    private const string ChatResponse = """
        {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"chat",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    [Fact]
    public void ValidatesArguments()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        Assert.Throws<ArgumentNullException>(() => AspireAzureOpenAIExtensions.AddAzureOpenAIClient(null!, "chat"));
        Assert.Throws<ArgumentNullException>(() => AspireAzureOpenAIExtensions.AddKeyedAzureOpenAIClient(null!, "chat"));
        Assert.Throws<ArgumentException>(() => builder.AddAzureOpenAIClient(""));
        Assert.Throws<ArgumentException>(() => builder.AddKeyedAzureOpenAIClient(""));
    }

    [Theory]
    [InlineData("""{"Endpoint":"https://test.openai.azure.com","TokenScope":"https://ai.azure.com/.default","DisableMetrics":false,"ClientOptions":{"NetworkTimeout":"00:00:30"}}""", true)]
    [InlineData("""{"Endpoint":"relative"}""", false)]
    [InlineData("""{"DisableTracing":"true"}""", false)]
    public void ConfigurationSchemaValidatesSettings(string settings, bool valid)
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json"),
            new BuildOptions { Dialect = Dialect.Draft07, SchemaRegistry = new SchemaRegistry() });
        using var configuration = JsonDocument.Parse("""{"Aspire":{"Azure":{"AI":{"OpenAI":""" + settings + "}}}}");
        Assert.Equal(valid, schema.Evaluate(configuration.RootElement, new EvaluationOptions { RequireFormatValidation = true }).IsValid);
    }

    [Theory]
    [InlineData(false, Endpoint)]
    [InlineData(true, Endpoint)]
    [InlineData(false, "Endpoint=" + Endpoint + ";Deployment=chat")]
    [InlineData(true, "Endpoint=" + Endpoint + ";Deployment=chat")]
    [InlineData(false, Endpoint + "/openai/v1/")]
    [InlineData(true, Endpoint + "/openai/v1")]
    public void RegistersSingletonWithDefaultCredentials(bool keyed, string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:chat"] = connectionString;
        Register(builder, keyed);
        using var host = builder.Build();
        var client = Resolve(host.Services, keyed);
        Assert.Same(client, Resolve(host.Services, keyed));
        Assert.Equal(new Uri(Endpoint + "/openai/v1/"), client.Endpoint);
        if (keyed)
        {
            Assert.Null(host.Services.GetService<OpenAIClient>());
        }
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
        Assert.Contains("ConnectionStrings:chat", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationPrecedenceAndClientCustomization(bool keyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Section}:Endpoint"] = "https://root.openai.azure.com",
            [$"{Section}:chat:Endpoint"] = "https://named.openai.azure.com",
            ["ConnectionStrings:chat"] = $"Endpoint={Endpoint};Deployment=chat",
            [$"{Section}:ClientOptions:NetworkTimeout"] = "00:00:01",
            [$"{Section}:chat:ClientOptions:NetworkTimeout"] = "00:00:02"
        });
        var settingsCredential = new FoundryTestTokenCredential();
        var clientCredential = new FoundryTestTokenCredential();
        using var handler = new FoundryTestHttpMessageHandler(ChatResponse);
        using var httpClient = new HttpClient(handler);

        void ConfigureSettings(AzureOpenAISettings settings)
        {
            Assert.Equal(new Uri(Endpoint), settings.Endpoint);
            settings.Endpoint = new("https://callback.openai.azure.com");
            settings.Credential = settingsCredential;
        }

        void ConfigureClient(IAzureClientBuilder<OpenAIClient, OpenAIClientOptions> client)
        {
            client.WithCredential(clientCredential).ConfigureOptions(options =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), options.NetworkTimeout);
                options.Transport = new HttpClientPipelineTransport(httpClient);
            });
        }

        var clientBuilder = keyed
            ? builder.AddKeyedAzureOpenAIClient("chat", ConfigureSettings, ConfigureClient)
            : builder.AddAzureOpenAIClient("chat", ConfigureSettings, ConfigureClient);
        if (keyed)
        {
            clientBuilder.AddKeyedChatClient("chat-client");
        }
        else
        {
            clientBuilder.AddChatClient();
        }

        using var host = builder.Build();
        var chat = keyed ? host.Services.GetRequiredKeyedService<IChatClient>("chat-client") : host.Services.GetRequiredService<IChatClient>();
        Assert.Equal("chat", chat.GetService<ChatClientMetadata>()!.DefaultModelId);
        Assert.Equal("Hello", (await chat.GetResponseAsync("Hello")).Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://callback.openai.azure.com/openai/v1/chat/completions"), request.Uri);
        Assert.Empty(settingsCredential.RequestedScopes);
        Assert.Equal(["https://ai.azure.com/.default"], Assert.Single(clientCredential.RequestedScopes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SupportsApiKeysAndExplicitTokenScopes(bool keyed, bool apiKey)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:chat"] = $"Endpoint={Endpoint};Deployment=chat" + (apiKey ? ";Key=test-key" : "");
        var credential = new FoundryTestTokenCredential();
        using var handler = new FoundryTestHttpMessageHandler(ChatResponse);
        using var httpClient = new HttpClient(handler);
        void ConfigureSettings(AzureOpenAISettings settings)
        {
            settings.Credential = credential;
            settings.TokenScope = "https://cognitiveservices.azure.us/.default";
        }
        void ConfigureClient(IAzureClientBuilder<OpenAIClient, OpenAIClientOptions> client)
            => client.ConfigureOptions(options => options.Transport = new HttpClientPipelineTransport(httpClient));
        if (keyed)
        {
            builder.AddKeyedAzureOpenAIClient("chat", ConfigureSettings, ConfigureClient);
        }
        else
        {
            builder.AddAzureOpenAIClient("chat", ConfigureSettings, ConfigureClient);
        }

        using var host = builder.Build();
        await Resolve(host.Services, keyed).GetChatClient("chat").CompleteChatAsync("Hello");
        Assert.Single(handler.Requests);
        if (apiKey)
        {
            Assert.Empty(credential.RequestedScopes);
        }
        else
        {
            Assert.Equal(["https://cognitiveservices.azure.us/.default"], Assert.Single(credential.RequestedScopes));
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task ExportsIndependentChatAndEmbeddingTelemetry(bool keyed, bool generateEmbeddings, bool disableTracing, bool disableMetrics)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var credential = new FoundryTestTokenCredential();
        using var handler = new FoundryTestHttpMessageHandler(generateEmbeddings ? """
            {"object":"list","model":"embedding","data":[{"object":"embedding","index":0,"embedding":[1.0,2.0]}],
             "usage":{"prompt_tokens":1,"total_tokens":1}}
            """ : ChatResponse);
        using var httpClient = new HttpClient(handler);
        using var exporter = new FoundryTestActivityExporter();
        using var metricExporter = new FoundryTestMetricExporter();
        void ConfigureSettings(AzureOpenAISettings settings)
        {
            settings.Endpoint = new(Endpoint);
            settings.Credential = credential;
            settings.DisableTracing = disableTracing;
            settings.DisableMetrics = disableMetrics;
        }
        void ConfigureClient(IAzureClientBuilder<OpenAIClient, OpenAIClientOptions> client)
            => client.ConfigureOptions(options => options.Transport = new HttpClientPipelineTransport(httpClient));
        var clientBuilder = keyed
            ? builder.AddKeyedAzureOpenAIClient("chat", ConfigureSettings, ConfigureClient)
            : builder.AddAzureOpenAIClient("chat", ConfigureSettings, ConfigureClient);
        if (keyed)
        {
            clientBuilder.AddKeyedChatClient("chat-client", "chat");
            clientBuilder.AddKeyedEmbeddingGenerator("embeddings", "embedding");
        }
        else
        {
            clientBuilder.AddChatClient("chat");
            clientBuilder.AddEmbeddingGenerator("embedding");
        }
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));
        if (!disableMetrics)
        {
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(new BaseExportingMetricReader(metricExporter)));
        }
        using var host = builder.Build();
        host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetService<MeterProvider>();
        var chat = keyed ? host.Services.GetRequiredKeyedService<IChatClient>("chat-client") : host.Services.GetRequiredService<IChatClient>();
        var embeddings = keyed
            ? host.Services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("embeddings")
            : host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.Equal(disableTracing && disableMetrics, chat.GetService<OpenTelemetryChatClient>() is null);
        Assert.Equal(disableTracing && disableMetrics, embeddings.GetService<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>() is null);
        Assert.Equal("embedding", embeddings.GetService<EmbeddingGeneratorMetadata>()!.DefaultModelId);
        if (generateEmbeddings)
        {
            Assert.Equal(new float[] { 1, 2 }, (await embeddings.GenerateVectorAsync("Hello")).ToArray());
        }
        else
        {
            Assert.Equal("Hello", (await chat.GetResponseAsync("Hello")).Text);
        }
        Assert.Equal("/openai/v1/" + (generateEmbeddings ? "embeddings" : "chat/completions"), Assert.Single(handler.Requests).Uri.AbsolutePath);
        Assert.Equal(["https://ai.azure.com/.default"], Assert.Single(credential.RequestedScopes));
        if (disableTracing)
        {
            Assert.Empty(exporter.Activities);
        }
        else
        {
            Assert.Contains(exporter.Activities, activity => activity.Source.Name is "Experimental.Microsoft.Extensions.AI" or "Microsoft.Extensions.AI");
        }
        if (disableMetrics)
        {
            Assert.Null(meterProvider);
        }
        else
        {
            Assert.True(meterProvider!.ForceFlush());
            Assert.Equal(["gen_ai.client.operation.duration", "gen_ai.client.token.usage"], metricExporter.Metrics.Select(metric => metric.Name).Order(StringComparer.Ordinal));
            var metric = Assert.Single(metricExporter.Metrics, metric => metric.Name == "gen_ai.client.token.usage");
            long measurements = 0;
            foreach (var point in metric.GetMetricPoints())
            {
                measurements += point.GetHistogramCount();
            }
            Assert.True(measurements > 0);
        }
    }

    [Theory]
    [InlineData(false, "http://test.openai.azure.com")]
    [InlineData(true, "http://test.openai.azure.com")]
    [InlineData(false, "Endpoint=http://test.openai.azure.com;Deployment=chat")]
    [InlineData(true, "Endpoint=http://test.openai.azure.com;Deployment=chat")]
    public void RejectsHttpConnectionStrings(bool keyed, string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:chat"] = connectionString;
        Register(builder, keyed);
        using var host = builder.Build();
        Assert.Throws<ArgumentException>(() => Resolve(host.Services, keyed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RejectsHttpFromConfigurationOrClientOptions(bool keyed, bool clientOptions)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var section = keyed ? $"{Section}:chat" : Section;
        builder.Configuration[$"{section}:Endpoint"] = Endpoint;
        if (!clientOptions)
        {
            builder.Configuration[$"{section}:Endpoint"] = "http://test.openai.azure.com";
        }
        void ConfigureClient(IAzureClientBuilder<OpenAIClient, OpenAIClientOptions> client)
        {
            if (clientOptions)
            {
                client.ConfigureOptions(options => options.Endpoint = new("http://test.openai.azure.com"));
            }
        }
        if (keyed)
        {
            builder.AddKeyedAzureOpenAIClient("chat", configureClientBuilder: ConfigureClient);
        }
        else
        {
            builder.AddAzureOpenAIClient("chat", configureClientBuilder: ConfigureClient);
        }
        using var host = builder.Build();
        Assert.Throws<ArgumentException>(() => Resolve(host.Services, keyed));
    }

    [Theory]
    [InlineData("http://test.openai.azure.com")]
    [InlineData("https://test.openai.azure.com/api/projects/project")]
    [InlineData("https://test.openai.azure.com/?api-version=old")]
    [InlineData("https://user@test.openai.azure.com")]
    [InlineData("https://test.openai.azure.com/#fragment")]
    public void RejectsInvalidAccountEndpointsBeforeTokenAcquisition(string endpoint)
    {
        foreach (var keyed in new[] { false, true })
        {
            var builder = Host.CreateEmptyApplicationBuilder(null);
            var credential = new FoundryTestTokenCredential();
            void ConfigureSettings(AzureOpenAISettings settings)
            {
                settings.Endpoint = new(endpoint);
                settings.Credential = credential;
            }
            if (keyed)
            {
                builder.AddKeyedAzureOpenAIClient("chat", ConfigureSettings);
            }
            else
            {
                builder.AddAzureOpenAIClient("chat", ConfigureSettings);
            }
            using var host = builder.Build();
            Assert.Throws<ArgumentException>(() => Resolve(host.Services, keyed));
            Assert.Empty(credential.RequestedScopes);
        }
    }

    [Fact]
    public void KeyedAccountAndProjectClientsCanCoexist()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddKeyedAzureOpenAIClient("account", settings => settings.Endpoint = new(Endpoint));
        builder.AddKeyedAzureProjectOpenAIClient("project", settings => settings.Endpoint = new("https://test.services.ai.azure.com/api/projects/test"));
        using var host = builder.Build();
        Assert.NotSame(host.Services.GetRequiredKeyedService<OpenAIClient>("account"), host.Services.GetRequiredKeyedService<OpenAIClient>("project"));
    }

    private static void Register(HostApplicationBuilder builder, bool keyed)
    {
        if (keyed)
        {
            builder.AddKeyedAzureOpenAIClient("chat");
        }
        else
        {
            builder.AddAzureOpenAIClient("chat");
        }
    }

    private static OpenAIClient Resolve(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<OpenAIClient>("chat") : services.GetRequiredService<OpenAIClient>();
}
