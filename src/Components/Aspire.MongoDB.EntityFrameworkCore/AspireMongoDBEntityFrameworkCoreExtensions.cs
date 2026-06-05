// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.MongoDB.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring EntityFrameworkCore DbContext to a MongoDB database
/// </summary>
public static class AspireMongoDBEntityFrameworkCoreExtensions
{
    private const string DefaultConfigSectionName = "Aspire:MongoDB:EntityFrameworkCore";
    private const DynamicallyAccessedMemberTypes RequiredByEF = DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties;
    private const string ActivityNameSource = "MongoDB.Driver.Core.Extensions.DiagnosticSources";

    /// <summary>
    /// Registers the given <see cref="DbContext" /> as a service in the services provided by the <paramref name="builder"/>.
    /// Enables db context pooling, retries, health check, logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be registered.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="databaseName">The name of the database. If not provided, it will be extracted from the connection string or settings.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureDbContextOptions">An optional delegate to configure the <see cref="DbContextOptions"/> for the context.</param>
    /// <remarks>Reads the configuration from "Aspire:MongoDB:EntityFrameworkCore:{typeof(TContext).Name}" config section, or "Aspire:MongoDB:EntityFrameworkCore" if former does not exist.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="MongoDBEntityFrameworkCoreSettings.ConnectionString"/> is not provided.</exception>
    public static void AddMongoDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        string? databaseName = null,
        Action<MongoDBEntityFrameworkCoreSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        builder.EnsureDbContextNotRegistered<TContext>();

        var settings = builder.GetDbContextSettings<TContext, MongoDBEntityFrameworkCoreSettings>(
            DefaultConfigSectionName,
            connectionName,
            (settings, section) => section.Bind(settings)
        );

        if (builder.Configuration.GetConnectionString(connectionName) is { } connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        // Database name priority: explicit parameter > settings > connection string
        if (!string.IsNullOrEmpty(databaseName))
        {
            settings.DatabaseName = databaseName;
        }
        else if (string.IsNullOrEmpty(settings.DatabaseName) && !string.IsNullOrEmpty(settings.ConnectionString))
        {
            // Try to extract database name from connection string
            var mongoUrl = MongoUrl.Create(settings.ConnectionString);
            if (!string.IsNullOrEmpty(mongoUrl.DatabaseName))
            {
                settings.DatabaseName = mongoUrl.DatabaseName;
            }
        }

        configureSettings?.Invoke(settings);

        builder.Services.AddDbContextPool<TContext>(ConfigureDbContext);

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureDbContext(DbContextOptionsBuilder dbContextOptionsBuilder)
        {
            ConnectionStringValidation.ValidateConnectionString(settings.ConnectionString, connectionName, DefaultConfigSectionName, $"{DefaultConfigSectionName}:{typeof(TContext).Name}", isEfDesignTime: EF.IsDesignTime);

            if (string.IsNullOrEmpty(settings.DatabaseName))
            {
                throw new InvalidOperationException($"A database name is required but was not provided. Specify it via the '{nameof(databaseName)}' parameter, the '{nameof(MongoDBEntityFrameworkCoreSettings.DatabaseName)}' setting, or include it in the connection string.");
            }

            dbContextOptionsBuilder.UseMongoDB(settings.ConnectionString!, settings.DatabaseName);

            configureDbContextOptions?.Invoke(dbContextOptionsBuilder);
        }
    }

    /// <summary>
    /// Configures logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be configured.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:MongoDB:EntityFrameworkCore:{typeof(TContext).Name}" config section, or "Aspire:MongoDB:EntityFrameworkCore" if former does not exist.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="DbContext"/> is not registered in DI.</exception>
    public static void EnrichMongoDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
            this IHostApplicationBuilder builder,
            Action<MongoDBEntityFrameworkCoreSettings>? configureSettings = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = builder.GetDbContextSettings<TContext, MongoDBEntityFrameworkCoreSettings>(
            DefaultConfigSectionName,
            null,
            (settings, section) => section.Bind(settings)
        );

        configureSettings?.Invoke(settings);

        // This call validates that the DbContext is registered in DI.
        // For net8.0, it also patches the service descriptor if needed.
        // For net9.0+, it's a no-op when no configuration action is provided.
        builder.PatchServiceDescriptor<TContext>();

        ConfigureInstrumentation<TContext>(builder, settings);
    }

    private static void ConfigureInstrumentation<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(IHostApplicationBuilder builder, MongoDBEntityFrameworkCoreSettings settings) where TContext : DbContext
    {
        if (!settings.DisableHealthChecks)
        {
            // calling MapHealthChecks is the responsibility of the app, not Component
            builder.TryAddHealthCheck(
                name: typeof(TContext).Name,
                static hcBuilder => hcBuilder.AddDbContextCheck<TContext>());
        }

        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(tracerProviderBuilder =>
                {
                    tracerProviderBuilder.AddSource(ActivityNameSource);
                });
        }
    }
}
