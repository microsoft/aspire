// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Milvus.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Milvus.Client;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering Milvus-related services in an <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireMilvusExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Milvus:Client";

    /// <summary>
    /// Registers <see cref="MilvusClient" /> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Configures logging for the <see cref="MilvusClient" />.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MilvusClientSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:Milvus:Client" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddMilvusClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MilvusClientSettings>? configureSettings = null)
    {
        AddMilvus(builder, configureSettings, connectionName, serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="MilvusClient" /> as a keyed singleton for the given <paramref name="name" /> in the services provided by the <paramref name="builder"/>.
    /// Configures logging for the <see cref="MilvusClient" />.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MilvusClientSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:Milvus:Client" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddKeyedMilvusClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<MilvusClientSettings>? configureSettings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddMilvus(builder, configureSettings, connectionName: name, serviceKey: name);
    }

    private static void AddMilvus(
        this IHostApplicationBuilder builder,
        Action<MilvusClientSettings>? configureSettings,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new MilvusClientSettings();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            settings.ParseConnectionString(connectionString);
        }

        configureSettings?.Invoke(settings);

        if (serviceKey is null)
        {
            builder.Services.AddSingleton(ConfigureMilvus);
        }
        else
        {
            builder.Services.AddKeyedSingleton(serviceKey, (sp, key) => ConfigureMilvus(sp));
        }

        if (!settings.DisableHealthChecks)
        {
            builder.TryAddHealthCheck(new HealthCheckRegistration(
                serviceKey is null ? "Milvus" : $"Milvus_{connectionName}",
                sp => new MilvusHealthCheck(serviceKey is null
                    ? sp.GetRequiredService<MilvusClient>()
                    : sp.GetRequiredKeyedService<MilvusClient>(serviceKey)),
                failureStatus: default,
                tags: default,
                timeout: default));
        }

        MilvusClient ConfigureMilvus(IServiceProvider serviceProvider)
        {
            if (settings.Endpoint is not null && settings.Key is not null)
            {
                return new MilvusClient(settings.Endpoint, apiKey: settings.Key, database: settings.Database, loggerFactory: serviceProvider.GetRequiredService<ILoggerFactory>());
            }
            else
            {
                throw new InvalidOperationException(
                        $"A MilvusClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or either " +
                        $"{nameof(settings.Endpoint)} and {nameof(settings.Key)} must both be provided " +
                        $"in the '{DefaultConfigSectionName}' or '{DefaultConfigSectionName}:{connectionName}' configuration sections.");
            }
        }
    }
}
