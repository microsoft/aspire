// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Throwaway no-op edit to exercise the audit-mode selective-CI test selector (#18127). Do not merge.

using Aspire.Azure.Common;
using Aspire.Azure.Data.Tables;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Data.Tables;
using HealthChecks.Azure.Data.Tables;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="TableServiceClient"/> as a singleton in the services provided by the <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireTablesExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Azure:Data:Tables";

    /// <summary>
    /// Registers <see cref="TableServiceClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureDataTablesSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Data:Tables" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when neither <see cref="AzureDataTablesSettings.ConnectionString"/> nor <see cref="AzureDataTablesSettings.ServiceUri"/> is provided.</exception>
    public static void AddAzureTableServiceClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureDataTablesSettings>? configureSettings = null,
        Action<IAzureClientBuilder<TableServiceClient, TableClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new TableServiceComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <inheritdoc cref="AddAzureTableServiceClient" />
    [Obsolete("Use AddAzureTableServiceClient instead. This method will be removed in a future version.")]
    public static void AddAzureTableClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureDataTablesSettings>? configureSettings = null,
        Action<IAzureClientBuilder<TableServiceClient, TableClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        AddAzureTableServiceClient(builder, connectionName, configureSettings, configureClientBuilder);
    }

    /// <summary>
    /// Registers <see cref="TableServiceClient"/> as a singleton for given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureDataTablesSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Data:Tables:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when neither <see cref="AzureDataTablesSettings.ConnectionString"/> nor <see cref="AzureDataTablesSettings.ServiceUri"/> is provided.</exception>
    public static void AddKeyedAzureTableServiceClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureDataTablesSettings>? configureSettings = null,
        Action<IAzureClientBuilder<TableServiceClient, TableClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new TableServiceComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddAzureTableServiceClient" />
    [Obsolete("Use AddKeyedAzureTableServiceClient instead. This method will be removed in a future version.")]
    public static void AddKeyedAzureTableClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureDataTablesSettings>? configureSettings = null,
        Action<IAzureClientBuilder<TableServiceClient, TableClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new TableServiceComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);
    }

    private sealed class TableServiceComponent : AzureComponent<AzureDataTablesSettings, TableServiceClient, TableClientOptions>
    {
        protected override IAzureClientBuilder<TableServiceClient, TableClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder, AzureDataTablesSettings settings, string connectionName,
            string configurationSectionName)
        {
            return ((IAzureClientFactoryBuilderWithCredential)azureFactoryBuilder).RegisterClientFactory<TableServiceClient, TableClientOptions>((options, cred) =>
            {
                var connectionString = settings.ConnectionString;
                if (string.IsNullOrEmpty(connectionString) && settings.ServiceUri is null)
                {
                    throw new InvalidOperationException($"A TableServiceClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a 'ConnectionString' or 'ServiceUri' in the '{configurationSectionName}' configuration section.");
                }

                return !string.IsNullOrEmpty(connectionString) ? new TableServiceClient(connectionString, options) :
                    cred is not null ? new TableServiceClient(settings.ServiceUri, cred, options) :
                    new TableServiceClient(settings.ServiceUri, options);
            }, requiresCredential: false);
        }

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<TableServiceClient, TableClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected override void BindSettingsToConfiguration(AzureDataTablesSettings settings, IConfiguration configuration)
        {
            configuration.Bind(settings);
        }

        protected override IHealthCheck CreateHealthCheck(TableServiceClient client, AzureDataTablesSettings settings)
            => new AzureTableServiceHealthCheck(client, new AzureTableServiceHealthCheckOptions());

        protected override bool GetHealthCheckEnabled(AzureDataTablesSettings settings)
            => !settings.DisableHealthChecks;

        protected override TokenCredential? GetTokenCredential(AzureDataTablesSettings settings)
            => settings.Credential;

        protected override bool GetMetricsEnabled(AzureDataTablesSettings settings)
            => false;

        protected override bool GetTracingEnabled(AzureDataTablesSettings settings)
            => !settings.DisableTracing;
    }
}
