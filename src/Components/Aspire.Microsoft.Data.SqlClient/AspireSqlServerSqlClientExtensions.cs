// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.Data.SqlClient;
using HealthChecks.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring SqlClient connection to Azure SQL, MS SQL server
/// </summary>
public static class AspireSqlServerSqlClientExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Microsoft:Data:SqlClient";

    /// <summary>
    /// Registers 'Scoped' <see cref="SqlConnection" /> factory for connecting Azure SQL, MS SQL database using Microsoft.Data.SqlClient.
    /// Configures health check, logging and telemetry for the SqlClient.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Data:SqlClient" section.</remarks>
    /// <exception cref="InvalidOperationException">If required <see cref="MicrosoftDataSqlClientSettings.ConnectionString"/>  is not provided in configuration section.</exception>
    public static void AddSqlServerClient(this IHostApplicationBuilder builder, string connectionName, Action<MicrosoftDataSqlClientSettings>? configureSettings = null)
        => AddSqlClient(builder, configureSettings, connectionName, serviceKey: null);

    /// <summary>
    /// Registers 'Scoped' <see cref="SqlConnection" /> factory for given <paramref name="name"/> for connecting Azure SQL, MsSQL database using Microsoft.Data.SqlClient.
    /// Configures health check, logging and telemetry for the SqlClient.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Data:SqlClient:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">If required <see cref="MicrosoftDataSqlClientSettings.ConnectionString"/> is not provided in configuration section.</exception>
    public static void AddKeyedSqlServerClient(this IHostApplicationBuilder builder, string name, Action<MicrosoftDataSqlClientSettings>? configureSettings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddSqlClient(builder, configureSettings, connectionName: name, serviceKey: name);
    }

    private static void AddSqlClient(
        IHostApplicationBuilder builder,
        Action<MicrosoftDataSqlClientSettings>? configure,
        string connectionName,
        object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        MicrosoftDataSqlClientSettings settings = new();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            settings.ConnectionString = connectionString;
        }

        configure?.Invoke(settings);

        // delay validating the ConnectionString until the SqlConnection is requested. This ensures an exception doesn't happen until a Logger is established.
        string GetConnectionString()
        {
            var connectionString = settings.ConnectionString;
            ConnectionStringValidation.ValidateConnectionString(connectionString, connectionName, DefaultConfigSectionName);
            return connectionString!;
        }

        if (serviceKey is null)
        {
            builder.Services.AddScoped(_ => new SqlConnection(GetConnectionString()));
        }
        else
        {
            builder.Services.AddKeyedScoped(serviceKey, (_, __) => new SqlConnection(GetConnectionString()));
        }

        // SqlClient Data Provider (Microsoft.Data.SqlClient) handles connection pooling automatically and it's on by default
        // https://learn.microsoft.com/sql/connect/ado-net/sql-server-connection-pooling
        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder.AddSqlClientInstrumentation();
            });
        }

        if (!settings.DisableHealthChecks)
        {
            builder.TryAddHealthCheck(new HealthCheckRegistration(
                serviceKey is null ? "SqlServer" : $"SqlServer_{connectionName}",
                sp => new SqlServerHealthCheck(new SqlServerHealthCheckOptions()
                {
                    ConnectionString = settings.ConnectionString ?? string.Empty
                }),
                failureStatus: default,
                tags: default,
                timeout: default));
        }
    }
}
