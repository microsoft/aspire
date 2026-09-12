// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.MySqlConnector;
using HealthChecks.MySql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting MySQL database with MySqlConnector client
/// </summary>
public static class AspireMySqlConnectorExtensions
{
    private const string DefaultConfigSectionName = "Aspire:MySqlConnector";

    /// <summary>
    /// Registers <see cref="MySqlDataSource"/> service for connecting MySQL database with MySqlConnector client.
    /// Configures health check, logging and telemetry for the MySqlConnector client.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:MySqlConnector" section.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="MySqlConnectorSettings.ConnectionString"/> is not provided.</exception>
    public static void AddMySqlDataSource(this IHostApplicationBuilder builder, string connectionName, Action<MySqlConnectorSettings>? configureSettings = null)
        => AddMySqlDataSource(builder, configureSettings, connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="MySqlDataSource"/> as a keyed service for given <paramref name="name"/> for connecting MySQL database with MySqlConnector client.
    /// Configures health check, logging and telemetry for the MySqlConnector client.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:MySqlConnector:{name}" section.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if mandatory <paramref name="name"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="MySqlConnectorSettings.ConnectionString"/> is not provided.</exception>
    public static void AddKeyedMySqlDataSource(this IHostApplicationBuilder builder, string name, Action<MySqlConnectorSettings>? configureSettings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddMySqlDataSource(builder, configureSettings, connectionName: name, serviceKey: name);
    }

    private static void AddMySqlDataSource(
        IHostApplicationBuilder builder,
        Action<MySqlConnectorSettings>? configureSettings,
        string connectionName,
        object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        MySqlConnectorSettings settings = new();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        builder.RegisterMySqlServices(settings, connectionName, serviceKey);

        // Same as SqlClient connection pooling is on by default and can be handled with connection string
        // https://mysqlconnector.net/connection-options/#Pooling
        if (!settings.DisableHealthChecks)
        {
            builder.TryAddHealthCheck(new HealthCheckRegistration(
                serviceKey is null ? "MySql" : $"MySql_{connectionName}",
                sp => new MySqlHealthCheck(
                    new MySqlHealthCheckOptions(serviceKey is null
                        ? sp.GetRequiredService<MySqlDataSource>()
                        : sp.GetRequiredKeyedService<MySqlDataSource>(serviceKey))),
                failureStatus: default,
                tags: default,
                timeout: default));
        }

        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracerProviderBuilder =>
                {
                    tracerProviderBuilder.AddSource("MySqlConnector");
                });
        }

        if (!settings.DisableMetrics)
        {
            builder.Services.AddOpenTelemetry()
                .WithMetrics(meterProviderBuilder =>
                {
                    meterProviderBuilder.AddMeter("MySqlConnector");
                });
        }
    }

    private static void RegisterMySqlServices(this IHostApplicationBuilder builder, MySqlConnectorSettings settings, string connectionName, object? serviceKey)
    {
        if (serviceKey is null)
        {
            builder.Services.AddMySqlDataSource(settings.ConnectionString ?? string.Empty, dataSourceBuilder =>
            {
                ValidateConnection();
            });
        }
        else
        {
            builder.Services.AddKeyedMySqlDataSource(serviceKey, settings.ConnectionString ?? string.Empty, dataSourceBuilder =>
            {
                ValidateConnection();
            });
        }

        // delay validating the ConnectionString until the DataSource is requested. This ensures an exception doesn't happen until a Logger is established.
        void ValidateConnection()
        {
            ConnectionStringValidation.ValidateConnectionString(settings.ConnectionString, connectionName, DefaultConfigSectionName);
        }
    }
}
