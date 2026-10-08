// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.AI.Extensions.OpenAI;
using Aspire.Azure.Common;
using Azure.AI.Extensions.OpenAI;
using Azure.Core;
using Azure.Core.Extensions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAI;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Registers Azure AI Project OpenAI clients with configuration and Microsoft.Extensions.AI telemetry.
/// </summary>
public static class AspireAzureProjectOpenAIExtensions
{
    internal const string DefaultConfigSectionName = "Aspire:Azure:AI:Extensions:OpenAI";

    /// <summary>
    /// Registers <see cref="ProjectOpenAIClient"/> and its <see cref="OpenAIClient"/> alias as singletons.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="connectionName">The project connection-string name.</param>
    /// <param name="configureSettings">Customizes settings after configuration and connection-string binding.</param>
    /// <param name="configureClientBuilder">Customizes the Azure client registration and options.</param>
    /// <returns>A builder for registering model clients and Microsoft.Extensions.AI services.</returns>
    /// <remarks>Reads configuration from <c>Aspire:Azure:AI:Extensions:OpenAI</c> and its connection-specific subsection.</remarks>
    /// <example>
    /// <code>
    /// builder.AddAzureProjectOpenAIClient("project").AddChatClient("chat");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionName"/> is null or empty, or the connection string is invalid.</exception>
    public static AspireAzureProjectOpenAIClientBuilder AddAzureProjectOpenAIClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureProjectOpenAISettings>? configureSettings = null,
        Action<IAzureClientBuilder<ProjectOpenAIClient, ProjectOpenAIClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new ProjectOpenAIComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
        builder.Services.TryAddSingleton<OpenAIClient>(static sp => sp.GetRequiredService<ProjectOpenAIClient>());

        return new(builder, connectionName, null, settings.DisableTracing, settings.EnableSensitiveTelemetryData);
    }

    /// <summary>
    /// Registers <see cref="ProjectOpenAIClient"/> and its <see cref="OpenAIClient"/> alias as keyed singletons.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The service key and project connection-string name.</param>
    /// <param name="configureSettings">Customizes settings after configuration and connection-string binding.</param>
    /// <param name="configureClientBuilder">Customizes the Azure client registration and options.</param>
    /// <returns>A builder for registering model clients and Microsoft.Extensions.AI services.</returns>
    /// <remarks>Uses <paramref name="name"/> for both configuration lookup and keyed service resolution.</remarks>
    /// <example>
    /// <code>
    /// builder.AddKeyedAzureProjectOpenAIClient("project").AddKeyedChatClient("chat-client", "chat");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty, or the connection string is invalid.</exception>
    public static AspireAzureProjectOpenAIClientBuilder AddKeyedAzureProjectOpenAIClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureProjectOpenAISettings>? configureSettings = null,
        Action<IAzureClientBuilder<ProjectOpenAIClient, ProjectOpenAIClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var settings = new ProjectOpenAIComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, name, serviceKey: name);
        builder.Services.TryAddKeyedSingleton<OpenAIClient>(name, static (sp, key) => sp.GetRequiredKeyedService<ProjectOpenAIClient>(key));

        return new(builder, name, name, settings.DisableTracing, settings.EnableSensitiveTelemetryData);
    }

    private sealed class ProjectOpenAIComponent : AzureComponent<AzureProjectOpenAISettings, ProjectOpenAIClient, ProjectOpenAIClientOptions>
    {
        // MEAI currently uses the experimental name; retain the stable name for future SDK versions.
        protected override string[] ActivitySourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];
        protected override string[] MetricSourceNames => ["Experimental.Microsoft.Extensions.AI", "Microsoft.Extensions.AI"];

        protected override IAzureClientBuilder<ProjectOpenAIClient, ProjectOpenAIClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder, AzureProjectOpenAISettings settings, string connectionName, string configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<ProjectOpenAIClient, ProjectOpenAIClientOptions>((options, credential, _) =>
            {
                if (settings.Endpoint is null)
                {
                    throw new InvalidOperationException($"A ProjectOpenAIClient could not be configured. Provide 'ConnectionStrings:{connectionName}' or an Endpoint in '{configurationSectionName}'.");
                }

                return new ProjectOpenAIClient(AzureAIProjectConnectionString.ValidateEndpoint(settings.Endpoint), credential, options);
            });
        }

        protected override void BindSettingsToConfiguration(AzureProjectOpenAISettings settings, IConfiguration configuration)
            => configuration.Bind(settings);

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<ProjectOpenAIClient, ProjectOpenAIClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // The configuration binding source generator requires a lambda.
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        // Do not issue billable inference requests or require model-list permissions for health checks.
        protected override bool GetHealthCheckEnabled(AzureProjectOpenAISettings settings) => false;

        protected override IHealthCheck CreateHealthCheck(ProjectOpenAIClient client, AzureProjectOpenAISettings settings)
            => throw new NotSupportedException("Health checks are not supported for ProjectOpenAIClient.");

        protected override bool GetMetricsEnabled(AzureProjectOpenAISettings settings) => !settings.DisableMetrics;

        protected override bool GetTracingEnabled(AzureProjectOpenAISettings settings) => !settings.DisableTracing;

        protected override TokenCredential? GetTokenCredential(AzureProjectOpenAISettings settings)
            => settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential();
    }
}
