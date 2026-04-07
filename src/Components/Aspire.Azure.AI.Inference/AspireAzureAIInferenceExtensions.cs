// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.AI.Inference;
using Aspire.Azure.Common;
using Azure;
using Azure.AI.Inference;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Core.Pipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for adding Azure AI Inference services to an Aspire application.
/// </summary>
public static class AspireAzureAIInferenceExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Azure:AI:Inference";

    /// <summary>
    /// Adds a <see cref="ChatCompletionsClient"/> to the application and configures it with the specified settings.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to add the client to.</param>
    /// <param name="connectionName">The name of the client. This is used to retrieve the connection string from configuration.</param>
    /// <param name="configureSettings">An optional callback to configure the <see cref="ChatCompletionsClientSettings"/>.</param>
    /// <param name="configureClientBuilder">An optional callback to configure the <see cref="IAzureClientBuilder{TClient, TOptions}"/> for the client.</param>
    /// <returns>An <see cref="AspireChatCompletionsClientBuilder"/> that can be used to further configure the client.</returns>
    /// <exception cref="InvalidOperationException">Thrown when endpoint is missing from settings.</exception>
    /// <remarks>
    /// <para>
    /// The client is registered as a singleton.
    /// </para>
    /// <para>
    /// Configuration is loaded from the "Aspire:Azure:AI:Inference" section, and can be supplemented with a connection string named after the <paramref name="connectionName"/> parameter.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to register a <see cref="ChatCompletionsClient"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddAzureChatCompletionsClient("chat");
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/chat", async (ChatCompletionsClient client) =>
    /// {
    ///     var response = await client.CompleteAsync("Hello, how are you?");
    ///     return response.Value.Content;
    /// });
    /// </code>
    /// </example>
    public static AspireChatCompletionsClientBuilder AddAzureChatCompletionsClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<ChatCompletionsClientSettings>? configureSettings = null,
        Action<IAzureClientBuilder<ChatCompletionsClient, AzureAIInferenceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new ChatCompletionsClientServiceComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            connectionName,
            serviceKey: null);

        return new AspireChatCompletionsClientBuilder(builder, serviceKey: null, settings.DeploymentName, settings.DisableTracing, settings.EnableSensitiveTelemetryData);
    }

    /// <summary>
    /// Adds a <see cref="ChatCompletionsClient"/> to the application and configures it with the specified settings.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to add the client to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional callback to configure the <see cref="ChatCompletionsClientSettings"/>.</param>
    /// <param name="configureClientBuilder">An optional callback to configure the <see cref="IAzureClientBuilder{TClient, TOptions}"/> for the client.</param>
    /// <returns>An <see cref="AspireChatCompletionsClientBuilder"/> that can be used to further configure the client.</returns>
    /// <exception cref="InvalidOperationException">Thrown when endpoint is missing from settings.</exception>
    /// <remarks>
    /// <para>
    /// The client is registered as a singleton with a keyed service.
    /// </para>
    /// <para>
    /// Configuration is loaded from the "Aspire:Azure:AI:Inference" section, and can be supplemented with a connection string named after the <paramref name="name"/> parameter.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to register a keyed <see cref="ChatCompletionsClient"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddKeyedAzureChatCompletionsClient("chat");
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/chat", async ([FromKeyedServices("chat")] ChatCompletionsClient client) =>
    /// {
    ///     var response = await client.CompleteAsync("Hello, how are you?");
    ///     return response.Value.Content;
    /// });
    /// </code>
    /// </example>
    public static AspireChatCompletionsClientBuilder AddKeyedAzureChatCompletionsClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<ChatCompletionsClientSettings>? configureSettings = null,
        Action<IAzureClientBuilder<ChatCompletionsClient, AzureAIInferenceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var settings = new ChatCompletionsClientServiceComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            name,
            serviceKey: name);

        return new AspireChatCompletionsClientBuilder(builder, serviceKey: name, settings.DeploymentName, settings.DisableTracing, settings.EnableSensitiveTelemetryData);
    }

    private sealed class ChatCompletionsClientServiceComponent : AzureComponent<ChatCompletionsClientSettings, ChatCompletionsClient, AzureAIInferenceClientOptions>
    {
        // GenAI telemetry isn't stable so MEAI currently has source name of "Experimental.Microsoft.Extensions.AI".
        // Listen to both names to ensure we capture telemetry from both stable and experimental versions.
        // When MEAI removes experimental from the source name, Aspire will continue to work without changes.
        protected override string[] ActivitySourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];
        protected override string[] MetricSourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];

        protected override IAzureClientBuilder<ChatCompletionsClient, AzureAIInferenceClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder,
            ChatCompletionsClientSettings settings,
            string connectionName, string
            configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<ChatCompletionsClient, AzureAIInferenceClientOptions>((options, _, _) =>
            {
                if (settings.Endpoint is null)
                {
                    throw new InvalidOperationException($"A ChatCompletionsClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a '{nameof(ChatCompletionsClientSettings.Endpoint)}' and optionally a '{nameof(ChatCompletionsClientSettings.Key)}' in the '{configurationSectionName}' configuration section.");
                }
                else
                {
                    var endpoint = settings.Endpoint;

                    // Connect to Azure AI Foundry using key auth
                    if (!string.IsNullOrEmpty(settings.Key))
                    {
                        var credential = new AzureKeyCredential(settings.Key);
                        return new ChatCompletionsClient(endpoint, credential, options);
                    }
                    else
                    {
                        var credential = settings.TokenCredential ?? AzureCredentialHelper.CreateDefaultAzureCredential();

                        // Defines the scopes used for authorization when connecting to Azure AI Inference services.
                        // Use the default one (ml.azure.com) and add the public one required for Azure Foundry AI.
                        // If users want to use a different scope they can configure the option using the client builder.
                        // c.f. https://github.com/Azure/azure-sdk-for-net/issues/50872

                        BearerTokenAuthenticationPolicy tokenPolicy = new(credential, ["https://cognitiveservices.azure.com/.default"]);
                        options.AddPolicy(tokenPolicy, HttpPipelinePosition.PerRetry);

                        return new ChatCompletionsClient(endpoint, credential, options);
                    }
                }
            });
        }

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<ChatCompletionsClient, AzureAIInferenceClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected override void BindSettingsToConfiguration(ChatCompletionsClientSettings settings, IConfiguration configuration)
            => configuration.Bind(settings);

        protected override IHealthCheck CreateHealthCheck(ChatCompletionsClient client, ChatCompletionsClientSettings settings)
            => throw new NotImplementedException();

        protected override bool GetHealthCheckEnabled(ChatCompletionsClientSettings settings)
            => false;

        protected override bool GetMetricsEnabled(ChatCompletionsClientSettings settings)
            => !settings.DisableMetrics;

        protected override TokenCredential? GetTokenCredential(ChatCompletionsClientSettings settings)
            => settings.TokenCredential;

        protected override bool GetTracingEnabled(ChatCompletionsClientSettings settings)
            => !settings.DisableTracing;
    }

    /// <summary>
    /// Creates a <see cref="IChatClient"/> from the <see cref="ChatCompletionsClient"/> registered in the service collection.
    /// </summary>
    /// <param name="builder">An <see cref="AspireChatCompletionsClientBuilder" />.</param>
    /// <param name="deploymentName">Optionally specifies which model deployment to use. If not specified, a value will be taken from the connection string.</param>
    /// <returns>A <see cref="ChatClientBuilder"/>.</returns>
    /// <example>
    /// The following example shows how to register an <see cref="IChatClient"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddAzureChatCompletionsClient("chat").AddChatClient();
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/chat", async (IChatClient client) =>
    /// {
    ///     var response = await client.GetResponseAsync("Hello, how are you?");
    ///     return response.Text;
    /// });
    /// </code>
    /// </example>
    public static ChatClientBuilder AddChatClient(this AspireChatCompletionsClientBuilder builder, string? deploymentName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.HostBuilder.Services.AddChatClient(
            services => CreateInnerChatClient(builder, services, deploymentName));
    }

    /// <summary>
    /// Creates a <see cref="IChatClient"/> from the <see cref="ChatCompletionsClient"/> registered in the service collection.
    /// </summary>
    /// <param name="builder">An <see cref="AspireChatCompletionsClientBuilder" />.</param>
    /// <param name="serviceKey">The service key with which the <see cref="IChatClient"/> will be registered.</param>
    /// <param name="deploymentName">Optionally specifies which model deployment to use. If not specified, a value will be taken from the connection string.</param>
    /// <returns>A <see cref="ChatClientBuilder"/>.</returns>
    /// <example>
    /// The following example shows how to register a keyed <see cref="IChatClient"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddKeyedAzureChatCompletionsClient("chat").AddKeyedChatClient("chat");
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/chat", async ([FromKeyedServices("chat")] IChatClient client) =>
    /// {
    ///     var response = await client.GetResponseAsync("Hello, how are you?");
    ///     return response.Text;
    /// });
    /// </code>
    /// </example>
    public static ChatClientBuilder AddKeyedChatClient(this AspireChatCompletionsClientBuilder builder, string serviceKey, string? deploymentName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ArgumentException.ThrowIfNullOrEmpty(serviceKey);

        return builder.HostBuilder.Services.AddKeyedChatClient(
            serviceKey,
            services => CreateInnerChatClient(builder, services, deploymentName));
    }

    private static IChatClient CreateInnerChatClient(AspireChatCompletionsClientBuilder builder, IServiceProvider services, string? deploymentName)
    {
        var chatCompletionsClient = string.IsNullOrEmpty(builder.ServiceKey) ?
                        services.GetRequiredService<ChatCompletionsClient>() :
                        services.GetRequiredKeyedService<ChatCompletionsClient>(builder.ServiceKey);

        var result = chatCompletionsClient.AsIChatClient(deploymentName ?? builder.DeploymentName);

        if (builder.DisableTracing)
        {
            return result;
        }

        var loggerFactory = services.GetService<ILoggerFactory>();
        return new OpenTelemetryChatClient(result, loggerFactory?.CreateLogger(typeof(OpenTelemetryChatClient)))
        {
            EnableSensitiveData = builder.EnableSensitiveTelemetryData
        };
    }

    /// <summary>
    /// Adds a <see cref="EmbeddingsClient"/> to the application and configures it with the specified settings.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to add the client to.</param>
    /// <param name="connectionName">The name of the client. This is used to retrieve the connection string from configuration.</param>
    /// <param name="configureSettings">An optional callback to configure the <see cref="ChatCompletionsClientSettings"/>.</param>
    /// <param name="configureClientBuilder">An optional callback to configure the <see cref="IAzureClientBuilder{TClient, TOptions}"/> for the client.</param>
    /// <returns>An <see cref="AspireEmbeddingsClientBuilder"/> that can be used to further configure the client.</returns>
    /// <exception cref="InvalidOperationException">Thrown when endpoint is missing from settings.</exception>
    /// <remarks>
    /// <para>
    /// The client is registered as a singleton.
    /// </para>
    /// <para>
    /// Configuration is loaded from the "Aspire:Azure:AI:Inference" section, and can be supplemented with a connection string named after the <paramref name="connectionName"/> parameter.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to register an <see cref="EmbeddingsClient"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddAzureEmbeddingsClient("embeddings");
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/embed", async (EmbeddingsClient client) =>
    /// {
    ///     var response = await client.EmbedAsync("Hello, world!");
    ///     return response.Value.Data;
    /// });
    /// </code>
    /// </example>
    public static AspireEmbeddingsClientBuilder AddAzureEmbeddingsClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<ChatCompletionsClientSettings>? configureSettings = null,
        Action<IAzureClientBuilder<EmbeddingsClient, AzureAIInferenceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new EmbeddingsClientServiceComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            connectionName,
            serviceKey: null);

        return new AspireEmbeddingsClientBuilder(builder, serviceKey: null, settings.DeploymentName, settings.DisableTracing, settings.EnableSensitiveTelemetryData);
    }

    /// <summary>
    /// Adds a <see cref="EmbeddingsClient"/> to the application and configures it with the specified settings.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to add the client to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional callback to configure the <see cref="ChatCompletionsClientSettings"/>.</param>
    /// <param name="configureClientBuilder">An optional callback to configure the <see cref="IAzureClientBuilder{TClient, TOptions}"/> for the client.</param>
    /// <returns>An <see cref="AspireEmbeddingsClientBuilder"/> that can be used to further configure the client.</returns>
    /// <exception cref="InvalidOperationException">Thrown when endpoint is missing from settings.</exception>
    /// <remarks>
    /// <para>
    /// The client is registered as a singleton with a keyed service.
    /// </para>
    /// <para>
    /// Configuration is loaded from the "Aspire:Azure:AI:Inference" section, and can be supplemented with a connection string named after the <paramref name="name"/> parameter.
    /// </para>
    /// </remarks>
    /// <example>
    /// The following example shows how to register a keyed <see cref="EmbeddingsClient"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddKeyedAzureEmbeddingsClient("embeddings");
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/embed", async ([FromKeyedServices("embeddings")] EmbeddingsClient client) =>
    /// {
    ///     var response = await client.EmbedAsync("Hello, world!");
    ///     return response.Value.Data;
    /// });
    /// </code>
    /// </example>
    public static AspireEmbeddingsClientBuilder AddKeyedAzureEmbeddingsClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<ChatCompletionsClientSettings>? configureSettings = null,
        Action<IAzureClientBuilder<EmbeddingsClient, AzureAIInferenceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var settings = new EmbeddingsClientServiceComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            name,
            serviceKey: name);

        return new AspireEmbeddingsClientBuilder(builder, serviceKey: name, settings.DeploymentName, settings.DisableTracing, settings.EnableSensitiveTelemetryData);
    }

    private sealed class EmbeddingsClientServiceComponent : AzureComponent<ChatCompletionsClientSettings, EmbeddingsClient, AzureAIInferenceClientOptions>
    {
        // GenAI telemetry isn't stable so MEAI currently has source name of "Experimental.Microsoft.Extensions.AI".
        // Listen to both names to ensure we capture telemetry from both stable and experimental versions.
        // When MEAI removes experimental from the source name, Aspire will continue to work without changes.
        protected override string[] ActivitySourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];
        protected override string[] MetricSourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];

        protected override IAzureClientBuilder<EmbeddingsClient, AzureAIInferenceClientOptions> AddClient(AzureClientFactoryBuilder azureFactoryBuilder, ChatCompletionsClientSettings settings,
            string connectionName, string configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<EmbeddingsClient, AzureAIInferenceClientOptions>((options, _, _) =>
            {
                if (settings.Endpoint is null)
                {
                    throw new InvalidOperationException($"A EmbeddingsClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a '{nameof(ChatCompletionsClientSettings.Endpoint)}' and optionally a '{nameof(ChatCompletionsClientSettings.Key)}' in the '{configurationSectionName}' configuration section.");
                }
                else
                {
                    var endpoint = settings.Endpoint;

                    // Connect to Azure AI Foundry using key auth
                    if (!string.IsNullOrEmpty(settings.Key))
                    {
                        var credential = new AzureKeyCredential(settings.Key);
                        return new EmbeddingsClient(endpoint, credential, options);
                    }
                    else
                    {
                        var credential = settings.TokenCredential ?? AzureCredentialHelper.CreateDefaultAzureCredential();

                        // Defines the scopes used for authorization when connecting to Azure AI Inference services.
                        // Use the default one (ml.azure.com) and add the public one required for Azure Foundry AI.
                        // If users want to use a different scope they can configure the option using the client builder.
                        // c.f. https://github.com/Azure/azure-sdk-for-net/issues/50872

                        BearerTokenAuthenticationPolicy tokenPolicy = new(credential, ["https://cognitiveservices.azure.com/.default"]);
                        options.AddPolicy(tokenPolicy, HttpPipelinePosition.PerRetry);

                        return new EmbeddingsClient(endpoint, credential, options);
                    }
                }
            });
        }

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<EmbeddingsClient, AzureAIInferenceClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected override void BindSettingsToConfiguration(ChatCompletionsClientSettings settings, IConfiguration configuration)
            => configuration.Bind(settings);

        protected override IHealthCheck CreateHealthCheck(EmbeddingsClient client, ChatCompletionsClientSettings settings)
            => throw new NotImplementedException();

        protected override bool GetHealthCheckEnabled(ChatCompletionsClientSettings settings)
            => false;

        protected override bool GetMetricsEnabled(ChatCompletionsClientSettings settings)
            => !settings.DisableMetrics;

        protected override TokenCredential? GetTokenCredential(ChatCompletionsClientSettings settings)
            => settings.TokenCredential;

        protected override bool GetTracingEnabled(ChatCompletionsClientSettings settings)
            => !settings.DisableTracing;
    }

    /// <summary>
    /// Creates a <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> from the <see cref="EmbeddingsClient"/> registered in the service collection.
    /// </summary>
    /// <param name="builder">An <see cref="AspireEmbeddingsClientBuilder" />.</param>
    /// <param name="deploymentName">Optionally specifies which model deployment to use. If not specified, a value will be taken from the connection string.</param>
    /// <returns>An <see cref="EmbeddingGeneratorBuilder{TInput, TEmbedding}"/>.</returns>
    /// <example>
    /// The following example shows how to register an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddAzureEmbeddingsClient("embeddings").AddEmbeddingGenerator();
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/embed", async (IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; generator) =>
    /// {
    ///     var embedding = await generator.GenerateEmbeddingAsync("Hello, world!");
    ///     return embedding.Vector.ToArray();
    /// });
    /// </code>
    /// </example>
    public static EmbeddingGeneratorBuilder<string, Embedding<float>> AddEmbeddingGenerator(this AspireEmbeddingsClientBuilder builder, string? deploymentName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.HostBuilder.Services.AddEmbeddingGenerator(
            services => CreateInnerEmbeddingGenerator(builder, services, deploymentName));
    }

    /// <summary>
    /// Creates a <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> from the <see cref="EmbeddingsClient"/> registered in the service collection.
    /// </summary>
    /// <param name="builder">An <see cref="AspireEmbeddingsClientBuilder" />.</param>
    /// <param name="serviceKey">The service key with which the <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> will be registered.</param>
    /// <param name="deploymentName">Optionally specifies which model deployment to use. If not specified, a value will be taken from the connection string.</param>
    /// <returns>An <see cref="EmbeddingGeneratorBuilder{TInput, TEmbedding}"/>.</returns>
    /// <example>
    /// The following example shows how to register a keyed <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> and use it in a minimal API endpoint:
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddKeyedAzureEmbeddingsClient("embeddings").AddKeyedEmbeddingGenerator("embeddings");
    /// 
    /// var app = builder.Build();
    /// app.MapGet("/embed", async ([FromKeyedServices("embeddings")] IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; generator) =>
    /// {
    ///     var embedding = await generator.GenerateEmbeddingAsync("Hello, world!");
    ///     return embedding.Vector.ToArray();
    /// });
    /// </code>
    /// </example>
    public static EmbeddingGeneratorBuilder<string, Embedding<float>> AddKeyedEmbeddingGenerator(this AspireEmbeddingsClientBuilder builder, string serviceKey, string? deploymentName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ArgumentException.ThrowIfNullOrEmpty(serviceKey);

        return builder.HostBuilder.Services.AddKeyedEmbeddingGenerator(
            serviceKey,
            services => CreateInnerEmbeddingGenerator(builder, services, deploymentName));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateInnerEmbeddingGenerator(AspireEmbeddingsClientBuilder builder, IServiceProvider services, string? deploymentName)
    {
        var embeddingsClient = string.IsNullOrEmpty(builder.ServiceKey) ?
            services.GetRequiredService<EmbeddingsClient>() :
            services.GetRequiredKeyedService<EmbeddingsClient>(builder.ServiceKey);

        var result = embeddingsClient.AsIEmbeddingGenerator(deploymentName ?? builder.DeploymentName);

        if (builder.DisableTracing)
        {
            return result;
        }

        var loggerFactory = services.GetService<ILoggerFactory>();
        return new OpenTelemetryEmbeddingGenerator<string, Embedding<float>>(result, loggerFactory?.CreateLogger(typeof(OpenTelemetryEmbeddingGenerator<string, Embedding<float>>)))
        {
            EnableSensitiveData = builder.EnableSensitiveTelemetryData
        };
    }
}
