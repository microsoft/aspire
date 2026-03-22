// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Hosting.Azure.CosmosDB;
using Aspire.Microsoft.EntityFrameworkCore.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Cosmos.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring EntityFrameworkCore DbContext to Azure Cosmos DB
/// </summary>
public static class AspireAzureEFCoreCosmosExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Microsoft:EntityFrameworkCore:Cosmos";
    private const DynamicallyAccessedMemberTypes RequiredByEF = DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>
    /// Registers the given <see cref="DbContext" /> as a service in the services provided by the <paramref name="builder"/>.
    /// Derives the name of the database from the connection string.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be registered.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing settings. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureDbContextOptions">An optional delegate to configure the <see cref="DbContextOptions"/> for the context.</param>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="EntityFrameworkCoreCosmosSettings.ConnectionString"/> is not provided.</exception>
    public static void AddCosmosDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<EntityFrameworkCoreCosmosSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        string? databaseName = null;
        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            var cosmosConnectionInfo = CosmosUtils.ParseConnectionString(connectionString);
            databaseName = cosmosConnectionInfo.DatabaseName;
        }

        if (databaseName is null)
        {
            throw new InvalidOperationException(
                "A DbContext could not be configured with this AddCosmosDbContext overload. "
                + $"Ensure the connection string '{connectionName}' contains a database name or use the overload that takes a database name as a parameter.");
        }

        AddCosmosDbContext<TContext>(
            builder,
            connectionName,
            databaseName,
            configureSettings,
            configureDbContextOptions);
    }

    /// <summary>
    /// Registers the given <see cref="DbContext" /> as a service in the services provided by the <paramref name="builder"/>.
    /// Enables db context pooling, logging and telemetry.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be registered.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="databaseName">The name of the database to use within the Azure Cosmos DB account.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing settings. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureDbContextOptions">An optional delegate to configure the <see cref="DbContextOptions"/> for the context.</param>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="EntityFrameworkCoreCosmosSettings.ConnectionString"/> is not provided.</exception>
    public static void AddCosmosDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        string databaseName,
        Action<EntityFrameworkCoreCosmosSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        builder.EnsureDbContextNotRegistered<TContext>();

        var settings = builder.GetDbContextSettings<TContext, EntityFrameworkCoreCosmosSettings>(
            DefaultConfigSectionName,
            connectionName,
            (settings, section) => section.Bind(settings)
        );

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            var cosmosConnectionInfo = CosmosUtils.ParseConnectionString(connectionString);

            if (cosmosConnectionInfo.AccountEndpoint is not null)
            {
                settings.AccountEndpoint = cosmosConnectionInfo.AccountEndpoint;
            }
            else
            {
                settings.ConnectionString = cosmosConnectionInfo.ConnectionString;
            }
            if (cosmosConnectionInfo.DatabaseName is not null)
            {
                settings.DatabaseName = cosmosConnectionInfo.DatabaseName;
            }
        }

        // Favor explicitly provided database name over the one resolved in the
        // connection string.
        if (settings.DatabaseName != databaseName)
        {
            settings.DatabaseName = databaseName;
        }

        configureSettings?.Invoke(settings);

        builder.Services.AddDbContextPool<TContext>(ConfigureDbContext);

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureDbContext(DbContextOptionsBuilder dbContextOptionsBuilder)
        {
            if (!string.IsNullOrEmpty(settings.ConnectionString))
            {
                dbContextOptionsBuilder.UseCosmos(settings.ConnectionString, settings.DatabaseName, UseCosmosBody);
            }
            else if (settings.AccountEndpoint is not null)
            {
                var credential = settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential();
                dbContextOptionsBuilder.UseCosmos(settings.AccountEndpoint.OriginalString, credential, settings.DatabaseName, UseCosmosBody);
            }
            else
            {
                throw new InvalidOperationException(
                  $"A DbContext could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or either " +
                  $"{nameof(settings.ConnectionString)} or {nameof(settings.AccountEndpoint)} must be provided " +
                  $"in the '{DefaultConfigSectionName}' or '{DefaultConfigSectionName}:{typeof(TContext).Name}' configuration section.");
            }

            configureDbContextOptions?.Invoke(dbContextOptionsBuilder);
        }

        void UseCosmosBody(CosmosDbContextOptionsBuilder builder)
        {
            // We don't register logger factory, because there is no need to:
            // https://learn.microsoft.com/dotnet/api/microsoft.entityframeworkcore.dbcontextoptionsbuilder.useloggerfactory?view=efcore-7.0#remarks
            if (settings.Region is not null)
            {
                builder.Region(settings.Region);
            }

            if (CosmosUtils.IsEmulatorConnectionString(settings.ConnectionString))
            {
                builder.ConnectionMode(ConnectionMode.Gateway);
                builder.LimitToEndpoint(true);
            }

            if (settings.RequestTimeout.HasValue)
            {
                builder.RequestTimeout(settings.RequestTimeout.Value);
            }
        }
    }

    /// <summary>
    /// Configures logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="DbContext"/> is not registered in DI.</exception>
    public static void EnrichCosmosDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
            this IHostApplicationBuilder builder,
            Action<EntityFrameworkCoreCosmosSettings>? configureSettings = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = builder.GetDbContextSettings<TContext, EntityFrameworkCoreCosmosSettings>(
            DefaultConfigSectionName,
            null,
            (settings, section) => section.Bind(settings)
        );

        configureSettings?.Invoke(settings);

        if (settings.RequestTimeout.HasValue)
        {
            builder.CheckDbContextRegistered<TContext>();

#if NET9_0_OR_GREATER
            builder.Services.ConfigureDbContext<TContext>(optionsBuilder =>
            {
                ConfigureRequestTimeout<TContext>(optionsBuilder, settings);
            });
#else
            builder.PatchServiceDescriptor<TContext>(optionsBuilder =>
            {
                ConfigureRequestTimeout<TContext>(optionsBuilder, settings);
            });
#endif
        }
        else
        {
            builder.PatchServiceDescriptor<TContext>();
        }

        ConfigureInstrumentation<TContext>(builder, settings);
    }

    private static void ConfigureRequestTimeout<TContext>(DbContextOptionsBuilder builder, EntityFrameworkCoreCosmosSettings settings)
    {
#pragma warning disable EF1001 // Internal EF Core API usage.
        var extension = builder.Options.FindExtension<CosmosOptionsExtension>();

        if (extension != null &&
            extension.RequestTimeout.HasValue &&
            extension.RequestTimeout != settings.RequestTimeout)
        {
            throw new InvalidOperationException($"Conflicting values for 'RequestTimeout' were found in {nameof(EntityFrameworkCoreCosmosSettings)} and set in DbContextOptions<{typeof(TContext).Name}>.");
        }

        extension?.WithRequestTimeout(settings.RequestTimeout);
#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    private static void ConfigureInstrumentation<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(IHostApplicationBuilder builder, EntityFrameworkCoreCosmosSettings settings) where TContext : DbContext
    {
        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder.AddSource("Azure.Cosmos.Operation");
            });
        }
    }
}
