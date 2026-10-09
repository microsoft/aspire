// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text.Json;
using Aspire.Components.TestUtilities;
using Aspire.OpenAI;
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
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "Endpoint=relative")]
    [InlineData(true, "Endpoint=relative")]
    [InlineData(false, "Endpoint=https://test.services.ai.azure.com/api/projects/test;Key=secret")]
    [InlineData(true, "Endpoint=https://test.services.ai.azure.com/api/projects/test;Key=secret")]
    [InlineData(false, "ftp://test/project")]
    [InlineData(true, "ftp://test/project")]
    [InlineData(false, "http://test.services.ai.azure.com/api/projects/test")]
    [InlineData(true, "http://test.services.ai.azure.com/api/projects/test")]
    [InlineData(false, "Endpoint=http://test.services.ai.azure.com/api/projects/test")]
    [InlineData(true, "Endpoint=http://test.services.ai.azure.com/api/projects/test")]
    public void RejectsInvalidOrApiKeyConnectionStrings(bool keyed, string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:project"] = connectionString;
        Assert.Throws<ArgumentException>(() => Register(builder, keyed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RejectsHttpEndpointsFromSettings(bool keyed, bool configureSettings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var endpoint = new Uri("http://test.services.ai.azure.com/api/projects/test");
        var credential = new FoundryTestTokenCredential();
        if (!configureSettings)
        {
            builder.Configuration[keyed ? $"{Section}:project:Endpoint" : $"{Section}:Endpoint"] = endpoint.AbsoluteUri;
        }

        void ConfigureSettings(AzureProjectOpenAISettings settings)
        {
            settings.Credential = credential;
            if (configureSettings)
            {
                settings.Endpoint = endpoint;
            }
        }

        if (keyed)
        {
            builder.AddKeyedAzureProjectOpenAIClient("project", ConfigureSettings);
        }
        else
        {
            builder.AddAzureProjectOpenAIClient("project", ConfigureSettings);
        }

        using var host = builder.Build();
        var exception = Assert.Throws<ArgumentException>(() => Resolve(host.Services, keyed));
        Assert.Equal(nameof(endpoint), exception.ParamName);
        Assert.Contains("absolute HTTPS URI", exception.Message);
        Assert.Empty(credential.RequestedScopes);
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
    public async Task ModelClientsUseConfiguredEndpointsAndIndependentTelemetry(bool keyed, bool embeddings, bool disableTracing, bool disableMetrics)
    {
        await AssertModelClientTelemetryAsync(keyed, embeddings, disableTracing, disableMetrics, projectIntegration: true);
        await AssertModelClientTelemetryAsync(keyed, embeddings, disableTracing, disableMetrics, projectIntegration: false);
    }

    private static async Task AssertModelClientTelemetryAsync(bool keyed, bool embeddings, bool disableTracing, bool disableMetrics, bool projectIntegration)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        using var handler = new FoundryTestHttpMessageHandler(embeddings ? """
            {"object":"list","model":"embedding","data":[{"object":"embedding","index":0,"embedding":[1.0,2.0]}],
             "usage":{"prompt_tokens":1,"total_tokens":1}}
            """ : """
            {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"chat",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """);
        using var httpClient = new HttpClient(handler);
        using var exporter = new FoundryTestActivityExporter();
        using var metricExporter = new FoundryTestMetricExporter();

        void ConfigureSettings(AzureProjectOpenAISettings settings)
        {
            settings.Endpoint = new(Endpoint);
            settings.Credential = new FoundryTestTokenCredential();
            settings.DisableTracing = disableTracing;
            settings.DisableMetrics = disableMetrics;
        }

        void ConfigureClient(IAzureClientBuilder<ProjectOpenAIClient, ProjectOpenAIClientOptions> client)
            => client.ConfigureOptions(options => options.Transport = new HttpClientPipelineTransport(httpClient));

        AspireOpenAIClientBuilder clientBuilder;
        if (projectIntegration)
        {
            clientBuilder = keyed
                ? builder.AddKeyedAzureProjectOpenAIClient("project", ConfigureSettings, ConfigureClient)
                : builder.AddAzureProjectOpenAIClient("project", ConfigureSettings, ConfigureClient);
        }
        else
        {
            void ConfigureOpenAISettings(OpenAISettings settings)
            {
                settings.Endpoint = new("https://example.invalid/v1");
                settings.Key = "test-key";
                settings.DisableTracing = disableTracing;
                settings.DisableMetrics = disableMetrics;
            }

            void ConfigureOpenAIOptions(OpenAIClientOptions options)
                => options.Transport = new HttpClientPipelineTransport(httpClient);

            clientBuilder = keyed
                ? builder.AddKeyedOpenAIClient("openai", ConfigureOpenAISettings, ConfigureOpenAIOptions)
                : builder.AddOpenAIClient("openai", ConfigureOpenAISettings, ConfigureOpenAIOptions);
        }

        Assert.Equal(disableTracing, clientBuilder.DisableTracing);
        Assert.Equal(disableMetrics, clientBuilder.DisableMetrics);
        if (keyed)
        {
            clientBuilder.AddKeyedChatClient("chat", "chat");
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
        Assert.Equal(!disableMetrics, meterProvider is not null);

        if (embeddings)
        {
            var generator = keyed
                ? host.Services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>("embeddings")
                : host.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            Assert.Equal(disableTracing && disableMetrics, generator.GetService<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>() is null);
            var vector = await generator.GenerateVectorAsync("hello");
            Assert.Equal(new float[] { 1, 2 }, vector.ToArray());
        }
        else
        {
            var chatClient = keyed
                ? host.Services.GetRequiredKeyedService<IChatClient>("chat")
                : host.Services.GetRequiredService<IChatClient>();
            Assert.Equal(disableTracing && disableMetrics, chatClient.GetService<OpenTelemetryChatClient>() is null);
            var response = await chatClient.GetResponseAsync("hello");
            Assert.Equal("Hello", response.Text);
        }

        var expectedEndpoint = projectIntegration ? "/api/projects/test/openai/v1" : "/v1";
        Assert.Equal($"{expectedEndpoint}/{(embeddings ? "embeddings" : "chat/completions")}", Assert.Single(handler.Requests).Uri.AbsolutePath);
        if (!disableTracing)
        {
            Assert.Contains(exporter.Activities, activity => activity.Source.Name is "Experimental.Microsoft.Extensions.AI" or "Microsoft.Extensions.AI");
        }
        else
        {
            Assert.Empty(exporter.Activities);
        }

        if (!disableMetrics)
        {
            Assert.True(meterProvider!.ForceFlush());
            Assert.Equal(
                ["gen_ai.client.operation.duration", "gen_ai.client.token.usage"],
                metricExporter.Metrics.Select(metric => metric.Name).Order(StringComparer.Ordinal));
            var metric = Assert.Single(metricExporter.Metrics, metric => metric.Name == "gen_ai.client.token.usage");
            long measurements = 0;
            foreach (var point in metric.GetMetricPoints())
            {
                measurements += point.GetHistogramCount();
            }
            Assert.True(measurements > 0);
        }
        else
        {
            Assert.Empty(metricExporter.Metrics);
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
