// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL;
using Aspire.Npgsql.EntityFrameworkCore.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering a PostgreSQL database context in an Aspire application.
/// </summary>
public static partial class AspireAzureEFPostgreSqlExtensions
{
    private const DynamicallyAccessedMemberTypes RequiredByEF = DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>
    /// Registers the given <see cref="DbContext" /> as a service in the services provided by the <paramref name="builder"/>.
    /// Enables db context pooling, retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be registered.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureDbContextOptions">An optional delegate to configure the <see cref="DbContextOptions"/> for the context.</param>
    /// <remarks>
    /// <para>
    /// Reads the configuration from "Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:{typeof(TContext).Name}" config section, or "Aspire:Npgsql:EntityFrameworkCore:PostgreSQL" if former does not exist.
    /// </para>
    /// <para>
    /// The <see cref="DbContext.OnConfiguring" /> method can then be overridden to configure <see cref="DbContext" /> options.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="NpgsqlEntityFrameworkCorePostgreSQLSettings.ConnectionString"/> is not provided.</exception>
    public static void AddAzureNpgsqlDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureNpgsqlEntityFrameworkCorePostgreSQLSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        AzureNpgsqlEntityFrameworkCorePostgreSQLSettings? azureSettings = null;

        builder.AddNpgsqlDbContext<TContext>(connectionName, settings => azureSettings = ConfigureSettings(configureSettings, settings), dbContextOptionsBuilder =>
        {
            Debug.Assert(azureSettings != null);

            ConfigureDbContextOptionsBuilder(azureSettings, dbContextOptionsBuilder);

            configureDbContextOptions?.Invoke(dbContextOptionsBuilder);
        });
    }

    /// <summary>
    /// Configures retries, health check, logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="DbContext"/> is not registered in DI.</exception>
    public static void EnrichAzureNpgsqlDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
            this IHostApplicationBuilder builder,
            Action<AzureNpgsqlEntityFrameworkCorePostgreSQLSettings>? configureSettings = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        AzureNpgsqlEntityFrameworkCorePostgreSQLSettings? azureSettings = null;

        builder.EnrichNpgsqlDbContext<TContext>(settings => azureSettings = ConfigureSettings(configureSettings, settings));

        // Enrich should always call ConfigureSettings
        Debug.Assert(azureSettings != null);

        builder.Services.ConfigureDbContext<TContext>(dbContextOptionsBuilder => ConfigureDbContextOptionsBuilder(azureSettings, dbContextOptionsBuilder));
    }

    private static AzureNpgsqlEntityFrameworkCorePostgreSQLSettings ConfigureSettings(Action<AzureNpgsqlEntityFrameworkCorePostgreSQLSettings>? userConfigureSettings, NpgsqlEntityFrameworkCorePostgreSQLSettings settings)
    {
        var azureSettings = new AzureNpgsqlEntityFrameworkCorePostgreSQLSettings();

        // Copy the values updated by Npgsql integration.
        CopySettings(settings, azureSettings);

        // Invoke the Aspire configuration.
        userConfigureSettings?.Invoke(azureSettings);

        // Copy to the Npgsql integration settings as it needs to get any values set in userConfigureSettings.
        CopySettings(azureSettings, settings);

        return azureSettings;
    }

    private static void CopySettings(NpgsqlEntityFrameworkCorePostgreSQLSettings source, NpgsqlEntityFrameworkCorePostgreSQLSettings destination)
    {
        destination.ConnectionString = source.ConnectionString;
        destination.DisableHealthChecks = source.DisableHealthChecks;
        destination.DisableMetrics = source.DisableMetrics;
        destination.DisableTracing = source.DisableTracing;
        destination.DisableRetry = source.DisableRetry;
        destination.CommandTimeout = source.CommandTimeout;
    }

    private static void ConfigureDbContextOptionsBuilder(AzureNpgsqlEntityFrameworkCorePostgreSQLSettings settings, DbContextOptionsBuilder dbContextOptionsBuilder)
    {
        dbContextOptionsBuilder.UseNpgsql(options =>
        {
            options.ConfigureDataSource(dataSourceBuilder => dataSourceBuilder.ConfigureEntraIdAuthentication(settings.Credential));
        });
    }
}
