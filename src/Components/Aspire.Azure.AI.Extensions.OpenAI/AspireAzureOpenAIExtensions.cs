// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel;
using System.ClientModel.Primitives;
using Aspire;
using Aspire.Azure.AI.Extensions.OpenAI;
using Aspire.Azure.Common;
using Azure.Core;
using Azure.Core.Extensions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAI;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers standard OpenAI SDK clients for Azure OpenAI accounts with Microsoft Entra authentication and telemetry.
/// </summary>
public static class AspireAzureOpenAIExtensions
{
    internal const string DefaultConfigSectionName = "Aspire:Azure:AI:OpenAI";

    /// <summary>
    /// Registers an <see cref="OpenAIClient"/> singleton for an Azure OpenAI account's v1 API.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="connectionName">The account connection-string name.</param>
    /// <param name="configureSettings">Customizes settings after configuration and connection-string binding.</param>
    /// <param name="configureClientBuilder">Customizes the Azure client registration, credential, and OpenAI SDK options.</param>
    /// <returns>A builder for registering chat, embedding, and Microsoft.Extensions.AI services.</returns>
    /// <remarks>
    /// Reads <c>Aspire:Azure:AI:OpenAI</c> and its connection-specific subsection.
    /// Account endpoints are normalized to <c>/openai/v1/</c>. Uses Microsoft Entra authentication unless an API key is supplied.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddAzureOpenAIClient("chat").AddChatClient();
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionName"/> is null or empty, or the connection string is invalid.</exception>
    public static AspireAzureOpenAIClientBuilder AddAzureOpenAIClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureOpenAISettings>? configureSettings = null,
        Action<IAzureClientBuilder<OpenAIClient, OpenAIClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new OpenAIComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
        return new(builder, connectionName, null, settings.DisableTracing, settings.EnableSensitiveTelemetryData)
        {
            DisableMetrics = settings.DisableMetrics
        };
    }

    /// <summary>
    /// Registers a keyed <see cref="OpenAIClient"/> singleton for an Azure OpenAI account's v1 API.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The service key and account connection-string name.</param>
    /// <param name="configureSettings">Customizes settings after configuration and connection-string binding.</param>
    /// <param name="configureClientBuilder">Customizes the Azure client registration, credential, and OpenAI SDK options.</param>
    /// <returns>A builder for registering chat, embedding, and Microsoft.Extensions.AI services.</returns>
    /// <remarks>Uses <paramref name="name"/> for configuration lookup and keyed resolution. Account endpoints use the v1 API.</remarks>
    /// <example>
    /// <code>
    /// builder.AddKeyedAzureOpenAIClient("chat").AddKeyedChatClient("chat-client", "deployment");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty, or the connection string is invalid.</exception>
    public static AspireAzureOpenAIClientBuilder AddKeyedAzureOpenAIClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureOpenAISettings>? configureSettings = null,
        Action<IAzureClientBuilder<OpenAIClient, OpenAIClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var settings = new OpenAIComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, name, serviceKey: name);
        return new(builder, name, name, settings.DisableTracing, settings.EnableSensitiveTelemetryData)
        {
            DisableMetrics = settings.DisableMetrics
        };
    }

    private sealed class OpenAIComponent : AzureComponent<AzureOpenAISettings, OpenAIClient, OpenAIClientOptions>
    {
        protected override string[] ActivitySourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];
        protected override string[] MetricSourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];

        protected override IAzureClientBuilder<OpenAIClient, OpenAIClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder, AzureOpenAISettings settings, string connectionName, string configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<OpenAIClient, OpenAIClientOptions>((options, credential, _) =>
            {
                var endpoint = options.Endpoint ?? settings.Endpoint;
                if (endpoint is null)
                {
                    throw new InvalidOperationException($"An OpenAIClient could not be configured. Provide 'ConnectionStrings:{connectionName}' or an Endpoint in '{configurationSectionName}'.");
                }

                options.Endpoint = AzureOpenAISettings.GetV1Endpoint(endpoint);
                if (!string.IsNullOrEmpty(settings.Key))
                {
                    return new OpenAIClient(new ApiKeyCredential(settings.Key), options);
                }

                ArgumentException.ThrowIfNullOrEmpty(settings.TokenScope);
                // Use the SDK's token policy to refresh Entra tokens rather than treating a token as a static API key.
                // https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/openai/Azure.AI.OpenAI/migration-guidance.md
                return new OpenAIClient(new BearerTokenPolicy(credential, settings.TokenScope), options);
            });
        }

        protected override void BindSettingsToConfiguration(AzureOpenAISettings settings, IConfiguration configuration)
            => configuration.Bind(settings);

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<OpenAIClient, OpenAIClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // The configuration binding source generator requires a lambda.
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected override bool GetHealthCheckEnabled(AzureOpenAISettings settings) => false;

        protected override IHealthCheck CreateHealthCheck(OpenAIClient client, AzureOpenAISettings settings)
            => throw new NotSupportedException("Health checks are not supported for Azure OpenAI account clients.");

        protected override bool GetMetricsEnabled(AzureOpenAISettings settings) => !settings.DisableMetrics;
        protected override bool GetTracingEnabled(AzureOpenAISettings settings) => !settings.DisableTracing;

        protected override TokenCredential? GetTokenCredential(AzureOpenAISettings settings)
            => string.IsNullOrEmpty(settings.Key) ? settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential() : null;
    }
}
