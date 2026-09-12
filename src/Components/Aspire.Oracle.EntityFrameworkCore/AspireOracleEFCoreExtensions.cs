// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Oracle.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Oracle.EntityFrameworkCore;
using Oracle.EntityFrameworkCore.Infrastructure.Internal;
using Oracle.EntityFrameworkCore.Storage.Internal;
using Oracle.ManagedDataAccess.OpenTelemetry;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring EntityFrameworkCore DbContext to Oracle database
/// </summary>
public static class AspireOracleEFCoreExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Oracle:EntityFrameworkCore";
    private const DynamicallyAccessedMemberTypes RequiredByEF = DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>
    /// Registers the given <see cref="DbContext" /> as a service in the services provided by the <paramref name="builder"/>.
    /// Enables db context pooling, retries, health check, logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <typeparam name="TContext">The <see cref="DbContext" /> that needs to be registered.</typeparam>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureDbContextOptions">An optional delegate to configure the <see cref="DbContextOptions"/> for the context.</param>
    /// <remarks>Reads the configuration from "Aspire:Oracle:EntityFrameworkCore:{typeof(TContext).Name}" config section, or "Aspire:Oracle:EntityFrameworkCore" if former does not exist.</remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="OracleEntityFrameworkCoreSettings.ConnectionString"/> is not provided.</exception>
    public static void AddOracleDatabaseDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<OracleEntityFrameworkCoreSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        builder.EnsureDbContextNotRegistered<TContext>();

        var settings = builder.GetDbContextSettings<TContext, OracleEntityFrameworkCoreSettings>(
            DefaultConfigSectionName,
            connectionName,
            (settings, section) => section.Bind(settings)
        );

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        builder.Services.AddDbContextPool<TContext>(ConfigureDbContext);

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureDbContext(DbContextOptionsBuilder dbContextOptionsBuilder)
        {
            ConnectionStringValidation.ValidateConnectionString(settings.ConnectionString, connectionName, DefaultConfigSectionName, $"{DefaultConfigSectionName}:{typeof(TContext).Name}", isEfDesignTime: EF.IsDesignTime);

            dbContextOptionsBuilder.UseOracle(settings.ConnectionString, builder =>
            {
                // Resiliency:
                // Connection resiliency automatically retries failed database commands
                if (!settings.DisableRetry)
                {
                    builder.ExecutionStrategy(context => new OracleRetryingExecutionStrategy(context));
                }

                // The time in seconds to wait for the command to execute.
                if (settings.CommandTimeout.HasValue)
                {
                    builder.CommandTimeout(settings.CommandTimeout);
                }
            });

            configureDbContextOptions?.Invoke(dbContextOptionsBuilder);
        }
    }

    /// <summary>
    /// Configures retries, health check, logging and telemetry for the <see cref="DbContext" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="DbContext"/> is not registered in DI.</exception>
    public static void EnrichOracleDatabaseDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
            this IHostApplicationBuilder builder,
            Action<OracleEntityFrameworkCoreSettings>? configureSettings = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = builder.GetDbContextSettings<TContext, OracleEntityFrameworkCoreSettings>(
            DefaultConfigSectionName,
            null,
            (settings, section) => section.Bind(settings)
        );

        configureSettings?.Invoke(settings);

        ConfigureRetry();

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureRetry()
        {
#pragma warning disable EF1001 // Internal EF Core API usage.
            if (!settings.DisableRetry || settings.CommandTimeout.HasValue)
            {
                builder.CheckDbContextRegistered<TContext>();

#if NET9_0_OR_GREATER
                builder.Services.ConfigureDbContext<TContext>(ConfigureRetryAndTimeout);
#else
                builder.PatchServiceDescriptor<TContext>(ConfigureRetryAndTimeout);
#endif

                void ConfigureRetryAndTimeout(DbContextOptionsBuilder optionsBuilder)
                {
                    optionsBuilder.UseOracle(options =>
                    {
                        var extension = optionsBuilder.Options.FindExtension<OracleOptionsExtension>();

                        if (!settings.DisableRetry)
                        {
                            var executionStrategy = extension?.ExecutionStrategyFactory?.Invoke(new ExecutionStrategyDependencies(null!, optionsBuilder.Options, null!));

                            if (executionStrategy != null)
                            {
                                if (executionStrategy is OracleRetryingExecutionStrategy)
                                {
                                    // Keep custom Retry strategy.
                                    // Any sub-class of OracleRetryingExecutionStrategy is a valid retry strategy
                                    // which shouldn't be replaced even with DisableRetry == false
                                }
                                else if (executionStrategy.GetType() != typeof(OracleExecutionStrategy))
                                {
                                    // Check OracleExecutionStrategy specifically (no 'is'), any sub-class is treated as a custom strategy.

                                    throw new InvalidOperationException($"{nameof(OracleEntityFrameworkCoreSettings)}.{nameof(OracleEntityFrameworkCoreSettings.DisableRetry)} needs to be set when a custom Execution Strategy is configured.");
                                }
                                else
                                {
                                    options.ExecutionStrategy(context => new OracleRetryingExecutionStrategy(context));
                                }
                            }
                            else
                            {
                                options.ExecutionStrategy(context => new OracleRetryingExecutionStrategy(context));
                            }
                        }

                        if (settings.CommandTimeout.HasValue)
                        {
                            if (extension != null &&
                                extension.CommandTimeout.HasValue &&
                                extension.CommandTimeout != settings.CommandTimeout)
                            {
                                throw new InvalidOperationException($"Conflicting values for 'CommandTimeout' were found in {nameof(OracleEntityFrameworkCoreSettings)} and set in DbContextOptions<{typeof(TContext).Name}>.");
                            }

                            options.CommandTimeout(settings.CommandTimeout);
                        }
                    });
                }
            }
#pragma warning restore EF1001 // Internal EF Core API usage.
        }
    }

    private static void ConfigureInstrumentation<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(IHostApplicationBuilder builder, OracleEntityFrameworkCoreSettings settings) where TContext : DbContext
    {
        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder.AddOracleDataProviderInstrumentation(settings.InstrumentationOptions);
            });
        }

        if (!settings.DisableHealthChecks)
        {
            builder.TryAddHealthCheck(
                name: typeof(TContext).Name,
                static hcBuilder => hcBuilder.AddDbContextCheck<TContext>());
        }
    }
}
