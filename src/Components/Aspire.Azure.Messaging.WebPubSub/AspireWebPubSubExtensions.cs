// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Aspire.Azure.Messaging.WebPubSub;
using Azure;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="WebPubSubServiceClient"/> as a singleton in the services provided by the <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireWebPubSubExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Azure:Messaging:WebPubSub";

    /// <summary>
    /// Registers <see cref="WebPubSubServiceClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureMessagingWebPubSubSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{WebPubSubServiceClient, WebPubSubServiceClientOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire.Azure.Messaging.WebPubSub" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when neither <see cref="AzureMessagingWebPubSubSettings.ConnectionString"/> nor <see cref="AzureMessagingWebPubSubSettings.Endpoint"/> is provided.</exception>
    public static void AddAzureWebPubSubServiceClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureMessagingWebPubSubSettings>? configureSettings = null,
        Action<IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new WebPubSubComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="WebPubSubServiceClient"/> as a singleton for given <paramref name="connectionName"/> and <paramref name="serviceKey"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The name of the component to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="serviceKey">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service, as well as the hub name is hub name is not set in the settings</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureMessagingWebPubSubSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{WebPubSubServiceClient, WebPubSubServiceClientOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire.Azure.Messaging.WebPubSub:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when neither <see cref="AzureMessagingWebPubSubSettings.ConnectionString"/> nor <see cref="AzureMessagingWebPubSubSettings.Endpoint"/> is provided.</exception>
    public static void AddKeyedAzureWebPubSubServiceClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        string serviceKey,
        Action<AzureMessagingWebPubSubSettings>? configureSettings = null,
        Action<IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);
        ArgumentException.ThrowIfNullOrEmpty(serviceKey);

        var configureWithServiceKeyAsDefaultHubName = (AzureMessagingWebPubSubSettings settings) =>
        {
            configureSettings?.Invoke(settings);
            if (string.IsNullOrEmpty(settings.HubName))
            {
                settings.HubName = serviceKey;
            }
        };
        new WebPubSubComponent().AddClient(builder, DefaultConfigSectionName, configureWithServiceKeyAsDefaultHubName, configureClientBuilder, connectionName: connectionName, serviceKey: serviceKey);
    }

    /// <summary>
    /// Registers <see cref="WebPubSubServiceClient"/> as a singleton for given <paramref name="connectionName"/> in the services provided by the <paramref name="builder"/>. This
    /// overload does not require a service key and uses the connection name as the service key
    /// to support scenarios where multiple Hubs are referenced in the same application.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The name of the component to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureMessagingWebPubSubSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{WebPubSubServiceClient, WebPubSubServiceClientOptions}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire.Azure.Messaging.WebPubSub:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">Thrown when neither <see cref="AzureMessagingWebPubSubSettings.ConnectionString"/> nor <see cref="AzureMessagingWebPubSubSettings.Endpoint"/> is provided.</exception>
    public static void AddKeyedAzureWebPubSubServiceClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureMessagingWebPubSubSettings>? configureSettings = null,
        Action<IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new WebPubSubComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: connectionName, serviceKey: connectionName);
    }

    private sealed class WebPubSubComponent : AzureComponent<AzureMessagingWebPubSubSettings, WebPubSubServiceClient, WebPubSubServiceClientOptions>
    {
        protected override IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions> AddClient
            (AzureClientFactoryBuilder azureFactoryBuilder, AzureMessagingWebPubSubSettings settings, string connectionName, string configurationSectionName)
        {
            return ((IAzureClientFactoryBuilderWithCredential)azureFactoryBuilder).RegisterClientFactory<WebPubSubServiceClient, WebPubSubServiceClientOptions>((options, cred) =>
            {
                var connectionString = settings.ConnectionString;
                if (string.IsNullOrEmpty(connectionString) && settings.Endpoint == null)
                {
                    throw new InvalidOperationException($"A WebPubSubServiceClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a 'ConnectionString' or 'Endpoint' in the '{configurationSectionName}' configuration section.");
                }

                // if HubName is missing, throw
                var hubName = settings.HubName;
                if (string.IsNullOrEmpty(hubName))
                {
                    throw new InvalidOperationException(
                        $"A WebPubSubServiceClient could not be configured. Ensure a valid HubName was configured or provided in " +
                        $"the '{configurationSectionName}' configuration section.");
                }

                return !string.IsNullOrEmpty(connectionString) ?
                    new WebPubSubServiceClient(connectionString, hubName, options) :
                    new WebPubSubServiceClient(settings.Endpoint!, hubName, cred, options);
            }, requiresCredential: false);
        }

        protected override IHealthCheck CreateHealthCheck(WebPubSubServiceClient client, AzureMessagingWebPubSubSettings settings)
            => new HealthCheck(client);

        protected override void BindClientOptionsToConfiguration(IAzureClientBuilder<WebPubSubServiceClient, WebPubSubServiceClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected override void BindSettingsToConfiguration(AzureMessagingWebPubSubSettings settings, IConfiguration config)
        {
            config.Bind(settings);
        }

        protected override TokenCredential? GetTokenCredential(AzureMessagingWebPubSubSettings settings)
            => settings.Credential;

        protected override bool GetMetricsEnabled(AzureMessagingWebPubSubSettings settings)
            => false;

        protected override bool GetTracingEnabled(AzureMessagingWebPubSubSettings settings)
            => !settings.DisableTracing;

        protected override bool GetHealthCheckEnabled(AzureMessagingWebPubSubSettings settings)
            => !settings.DisableHealthChecks;
    }

    private sealed class HealthCheck : IHealthCheck
    {
        private readonly WebPubSubServiceClient _client;

        public HealthCheck(WebPubSubServiceClient client)
        {
            _client = client;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await _client.ConnectionExistsAsync("0", new RequestContext() { CancellationToken = cancellationToken }).ConfigureAwait(false);

                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, "Failed to connect to Azure Web PubSub service.", ex);
            }
        }
    }
}
