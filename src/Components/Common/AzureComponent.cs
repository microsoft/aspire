// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Azure.Core;
using Azure.Core.Extensions;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Aspire.Azure.Common;

internal abstract class AzureComponent<TSettings, TClient, TClientOptions>
    where TSettings : class, new()
    where TClient : class
    where TClientOptions : class
{
    protected virtual string[] ActivitySourceNames => [$"{typeof(TClient).Namespace}.*"];

    protected virtual string[] MetricSourceNames => [$"{typeof(TClient).Namespace}.*"];

    // There would be no need for Get* methods if TSettings had a common base type or if it was implementing a shared interface.
    // TSettings is a public type and we don't have a shared package yet, but we may reconsider the approach in near future.
    protected abstract bool GetHealthCheckEnabled(TSettings settings);

    protected abstract bool GetMetricsEnabled(TSettings settings);

    protected abstract bool GetTracingEnabled(TSettings settings);

    protected abstract TokenCredential? GetTokenCredential(TSettings settings);

    protected abstract void BindSettingsToConfiguration(TSettings settings, IConfiguration configuration);

    protected abstract void BindClientOptionsToConfiguration(IAzureClientBuilder<TClient, TClientOptions> clientBuilder, IConfiguration configuration);

    protected abstract IAzureClientBuilder<TClient, TClientOptions> AddClient(
        AzureClientFactoryBuilder azureFactoryBuilder, TSettings settings, string connectionName,
        string configurationSectionName);

    protected abstract IHealthCheck CreateHealthCheck(TClient client, TSettings settings);

    internal TSettings AddClient(
        IHostApplicationBuilder builder,
        string configurationSectionName,
        Action<TSettings>? configureSettings,
        Action<IAzureClientBuilder<TClient, TClientOptions>>? configureClientBuilder,
        string connectionName,
        string? serviceKey)
    {
        var configSection = builder.Configuration.GetSection(configurationSectionName);

        var settings = new TSettings();
        // Bind both top-level and named configuration sections to the settings object
        // to allow connection-specific settings.
        BindSettingsToConfiguration(settings, configSection);
        BindSettingsToConfiguration(settings, configSection.GetSection(connectionName));
        // Support service key-based binding for clients that support it (e.g. WebPubSubServiceClient).
        var serviceKeySection = configSection.GetSection($"{connectionName}:{serviceKey}");
        if (serviceKeySection.Exists())
        {
            BindSettingsToConfiguration(settings, serviceKeySection);
        }

        Debug.Assert(settings is IConnectionStringSettings, $"The settings object should implement {nameof(IConnectionStringSettings)}.");
        if (settings is IConnectionStringSettings csSettings &&
            builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            csSettings.ParseConnectionString(connectionString);
        }

        configureSettings?.Invoke(settings);

        if (!string.IsNullOrEmpty(serviceKey))
        {
            // When named client registration is used (.WithName), Microsoft.Extensions.Azure
            // TRIES to register a factory for given client type and later
            // a call to serviceProvider.GetService<TClient> throws InvalidOperationException:
            // "Unable to find client registration with type 'SecretClient' and name 'Default'."
            // It's not desired, as Microsoft.Extensions.DependencyInjection keyed services
            // factory methods just return null in such cases.
            // To align the behavior across the Components, a null factory is registered up-front.
            builder.Services.AddSingleton<TClient>(static _ => null!);
        }

        builder.Services.AddAzureClients(azureFactoryBuilder =>
        {
            var clientBuilder = AddClient(azureFactoryBuilder, settings, connectionName, configurationSectionName);

            if (GetTokenCredential(settings) is { } credential)
            {
                clientBuilder.WithCredential(credential);
            }

            BindClientOptionsToConfiguration(clientBuilder, configSection.GetSection("ClientOptions"));
            BindClientOptionsToConfiguration(clientBuilder, configSection.GetSection($"{connectionName}:ClientOptions"));

            configureClientBuilder?.Invoke(clientBuilder);

            if (!string.IsNullOrEmpty(serviceKey))
            {
                // Set the name for the client registration.
                clientBuilder.WithName(serviceKey);

                // To resolve named clients IAzureClientFactory{TClient}.CreateClient needs to be used.
                builder.Services.AddKeyedSingleton(serviceKey,
                    static (serviceProvider, serviceKey) => serviceProvider.GetRequiredService<IAzureClientFactory<TClient>>().CreateClient((string)serviceKey!));
            }
        });

        if (GetHealthCheckEnabled(settings))
        {
            var namePrefix = $"Azure_{typeof(TClient).Name}";

            builder.TryAddHealthCheck(new HealthCheckRegistration(
                serviceKey is null ? namePrefix : $"{namePrefix}_{serviceKey}",
                serviceProvider =>
                {
                    // From https://devblogs.microsoft.com/azure-sdk/lifetime-management-and-thread-safety-guarantees-of-azure-sdk-net-clients/:
                    // "The main rule of Azure SDK client lifetime management is: treat clients as singletons".
                    // So it's fine to root the client via the health check.
                    var client = serviceKey is null
                        ? serviceProvider.GetRequiredService<TClient>()
                        : serviceProvider.GetRequiredKeyedService<TClient>(serviceKey);

                    return CreateHealthCheck(client, settings);
                },
                failureStatus: default,
                tags: default,
                timeout: default));
        }

        if (GetMetricsEnabled(settings))
        {
            builder.Services.AddOpenTelemetry()
                .WithMetrics(meterBuilder => meterBuilder.AddMeter(MetricSourceNames));
        }

        if (GetTracingEnabled(settings))
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(traceBuilder => traceBuilder.AddSource(ActivitySourceNames));
        }

        return settings;
    }
}

internal interface IConnectionStringSettings
{
    void ParseConnectionString(string? connectionString);
}
