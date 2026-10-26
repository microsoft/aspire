// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.AI.Projects;
using Aspire.Azure.Common;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Core.Extensions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering Azure AI Projects clients with configuration and tracing.
/// </summary>
public static class AspireAzureAIProjectsExtensions
{
    internal const string DefaultConfigSectionName = "Aspire:Azure:AI:Projects";

    /// <summary>
    /// Registers <see cref="AIProjectClient"/> as a singleton using Microsoft Entra authentication.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="connectionName">The name of the project connection string.</param>
    /// <param name="configureSettings">Customizes settings after configuration and connection-string binding.</param>
    /// <param name="configureClientBuilder">Customizes the Azure client registration and options.</param>
    /// <remarks>Reads configuration from <c>Aspire:Azure:AI:Projects</c> and its connection-specific subsection.</remarks>
    /// <example>
    /// <code>
    /// builder.AddAzureAIProjectClient("project");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionName"/> is null or empty, or the connection string is invalid.</exception>
    public static void AddAzureAIProjectClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureAIProjectsSettings>? configureSettings = null,
        Action<IAzureClientBuilder<AIProjectClient, AIProjectClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new ProjectsComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="AIProjectClient"/> as a keyed singleton using Microsoft Entra authentication.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The service key and project connection-string name.</param>
    /// <param name="configureSettings">Customizes settings after configuration and connection-string binding.</param>
    /// <param name="configureClientBuilder">Customizes the Azure client registration and options.</param>
    /// <remarks>Uses <paramref name="name"/> for both configuration lookup and keyed service resolution.</remarks>
    /// <example>
    /// <code>
    /// builder.AddKeyedAzureAIProjectClient("project");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty, or the connection string is invalid.</exception>
    public static void AddKeyedAzureAIProjectClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureAIProjectsSettings>? configureSettings = null,
        Action<IAzureClientBuilder<AIProjectClient, AIProjectClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new ProjectsComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, name, serviceKey: name);
    }

    private sealed class ProjectsComponent : AzureComponent<AzureAIProjectsSettings, AIProjectClient, AIProjectClientOptions>
    {
        protected override IAzureClientBuilder<AIProjectClient, AIProjectClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder, AzureAIProjectsSettings settings, string connectionName, string configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<AIProjectClient, AIProjectClientOptions>((options, credential, _) =>
            {
                if (settings.Endpoint is null)
                {
                    throw new InvalidOperationException($"An AIProjectClient could not be configured. Provide 'ConnectionStrings:{connectionName}' or an Endpoint in '{configurationSectionName}'.");
                }

                return new AIProjectClient(AzureAIProjectConnectionString.ValidateEndpoint(settings.Endpoint), credential, options);
            });
        }

        protected override void BindSettingsToConfiguration(AzureAIProjectsSettings settings, IConfiguration configuration)
            => configuration.Bind(settings);

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<AIProjectClient, AIProjectClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // The configuration binding source generator requires a lambda.
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        // No permission-independent, inference-free health endpoint is available for project clients.
        protected override bool GetHealthCheckEnabled(AzureAIProjectsSettings settings) => false;

        protected override IHealthCheck CreateHealthCheck(AIProjectClient client, AzureAIProjectsSettings settings)
            => throw new NotSupportedException("Health checks are not supported for AIProjectClient.");

        protected override bool GetMetricsEnabled(AzureAIProjectsSettings settings) => false;

        protected override bool GetTracingEnabled(AzureAIProjectsSettings settings) => !settings.DisableTracing;

        protected override TokenCredential? GetTokenCredential(AzureAIProjectsSettings settings)
            => settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential();
    }
}
