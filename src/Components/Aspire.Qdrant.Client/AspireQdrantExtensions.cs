// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Qdrant.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Qdrant.Client;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering Qdrant-related services in an <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireQdrantExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Qdrant:Client";

    /// <summary>
    /// Registers <see cref="QdrantClient" /> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Configures logging for the <see cref="QdrantClient" />.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="QdrantClientSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:Qdrant:Client" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddQdrantClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<QdrantClientSettings>? configureSettings = null)
        => AddQdrant(builder, configureSettings, connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="QdrantClient" /> as a keyed singleton for the given <paramref name="name" /> in the services provided by the <paramref name="builder"/>.
    /// Configures logging for the <see cref="QdrantClient" />.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="QdrantClientSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:Qdrant:Client" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddKeyedQdrantClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<QdrantClientSettings>? configureSettings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddQdrant(builder, configureSettings, connectionName: name, serviceKey: name);
    }

    private static void AddQdrant(
        this IHostApplicationBuilder builder,
        Action<QdrantClientSettings>? configureSettings,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new QdrantClientSettings();
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
            builder.Services.AddSingleton(ConfigureQdrant);
        }
        else
        {
            builder.Services.AddKeyedSingleton(serviceKey, (sp, key) => ConfigureQdrant(sp));
        }

        if (!settings.DisableHealthChecks)
        {
            var healthCheckName = serviceKey is null ? "Qdrant.Client" : $"Qdrant.Client_{connectionName}";

            builder.TryAddHealthCheck(new HealthCheckRegistration(
                healthCheckName,
                sp => new QdrantHealthCheck(serviceKey is null ?
                    sp.GetRequiredService<QdrantClient>() :
                    sp.GetRequiredKeyedService<QdrantClient>(serviceKey)),
                failureStatus: null,
                tags: null,
                timeout: settings.HealthCheckTimeout
                ));
        }

        QdrantClient ConfigureQdrant(IServiceProvider serviceProvider)
        {
            if (settings.Endpoint is not null)
            {
                return new QdrantClient(settings.Endpoint, apiKey: settings.Key, loggerFactory: serviceProvider.GetRequiredService<ILoggerFactory>());
            }
            else
            {
                throw new InvalidOperationException(
                        $"A QdrantClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or either " +
                        $"{nameof(settings.Endpoint)} must be provided " +
                        $"in the '{DefaultConfigSectionName}' or '{DefaultConfigSectionName}:{connectionName}' configuration section.");
            }
        }
    }
}
