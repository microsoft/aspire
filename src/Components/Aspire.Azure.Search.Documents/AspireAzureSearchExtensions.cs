// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Azure.Common;
using Aspire.Azure.Search.Documents;
using Azure;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="SearchIndexClient"/> as a singleton in the services provided by the <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireAzureSearchExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Azure:Search:Documents";

    /// <summary>
    /// Registers <see cref="SearchIndexClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSearchSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{SearchIndexClient, SearchClientOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Search:Documents" section.</remarks>
    public static void AddAzureSearchClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureSearchSettings>? configureSettings = null,
        Action<IAzureClientBuilder<SearchIndexClient, SearchClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new AzureSearchComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="SearchIndexClient"/> as a singleton for given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureSearchSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{SearchIndexClient, SearchClientOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Search:Documents:{name}" section.</remarks>
    public static void AddKeyedAzureSearchClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureSearchSettings>? configureSettings = null,
        Action<IAzureClientBuilder<SearchIndexClient, SearchClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new AzureSearchComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);
    }

    private sealed class AzureSearchComponent : AzureComponent<AzureSearchSettings, SearchIndexClient, SearchClientOptions>
    {
        // `SearchIndexClient` is in the Azure.Search.Documents.Indexes namespace
        // but uses `SearchClientOptions` which is in the Azure.Search.Documents namespace
        // https://github.com/Azure/azure-sdk-for-net/blob/bed506dee05319ff2de27ca98500daa10573fe7d/sdk/search/Azure.Search.Documents/src/Indexes/SearchIndexClient.cs#L92
        protected override string[] ActivitySourceNames => ["Azure.Search.Documents.*"];

        protected override IAzureClientBuilder<SearchIndexClient, SearchClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder, AzureSearchSettings settings, string connectionName,
            string configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<SearchIndexClient, SearchClientOptions>((options, _, _) =>
            {
                if (settings.Endpoint is null)
                {
                    throw new InvalidOperationException($"A SearchIndexClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify an '{nameof(AzureSearchSettings.Endpoint)}' in the '{configurationSectionName}' configuration section.");
                }

                if (!string.IsNullOrWhiteSpace(settings.Key))
                {
                    return new SearchIndexClient(settings.Endpoint, new AzureKeyCredential(settings.Key), options);
                }
                else
                {
                    return new SearchIndexClient(settings.Endpoint, settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential(), options);
                }
            });
        }

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<SearchIndexClient, SearchClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected override void BindSettingsToConfiguration(AzureSearchSettings settings, IConfiguration config)
        {
            config.Bind(settings);
        }

        protected override IHealthCheck CreateHealthCheck(SearchIndexClient client, AzureSearchSettings settings)
            => new AzureSearchIndexHealthCheck(client);

        protected override bool GetHealthCheckEnabled(AzureSearchSettings settings)
            => !settings.DisableHealthChecks;

        protected override TokenCredential? GetTokenCredential(AzureSearchSettings settings)
            => settings.Credential;

        protected override bool GetMetricsEnabled(AzureSearchSettings settings)
            => false;

        protected override bool GetTracingEnabled(AzureSearchSettings settings)
            => !settings.DisableTracing;
    }
}
