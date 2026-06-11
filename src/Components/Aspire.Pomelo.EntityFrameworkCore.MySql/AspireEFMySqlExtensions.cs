// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Pomelo.EntityFrameworkCore.MySql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using MySqlConnector.Logging;
using Polly;
using Polly.Registry;
using Polly.Retry;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Pomelo.EntityFrameworkCore.MySql.Storage.Internal;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering a MySQL database context in an Aspire application.
/// </summary>
public static partial class AspireEFMySqlExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Pomelo:EntityFrameworkCore:MySql";
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
    /// Reads the configuration from "Aspire:Pomelo:EntityFrameworkCore:MySql:{typeof(TContext).Name}" config section, or "Aspire:Pomelo:EntityFrameworkCore:MySql" if former does not exist.
    /// </para>
    /// <para>
    /// The <see cref="DbContext.OnConfiguring" /> method can then be overridden to configure <see cref="DbContext" /> options.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="PomeloEntityFrameworkCoreMySqlSettings.ConnectionString"/> is not provided.</exception>
    public static void AddMySqlDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<PomeloEntityFrameworkCoreMySqlSettings>? configureSettings = null,
        Action<DbContextOptionsBuilder>? configureDbContextOptions = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        builder.EnsureDbContextNotRegistered<TContext>();

        var settings = builder.GetDbContextSettings<TContext, PomeloEntityFrameworkCoreMySqlSettings>(
            DefaultConfigSectionName,
            connectionName,
            (settings, section) => section.Bind(settings)
        );

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        builder.Services.AddDbContextPool<TContext>(ConfigureDbContext);

        const string resilienceKey = "Microsoft.Extensions.Hosting.AspireEFMySqlExtensions.ServerVersion";
        builder.Services.AddResiliencePipeline(resilienceKey, static builder =>
        {
            // Values are taken from MySqlRetryingExecutionStrategy.MaxRetryCount and MaxRetryDelay.
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = static args => args.Outcome is { Exception: MySqlException { IsTransient: true } }
                    ? PredicateResult.True()
                    : PredicateResult.False(),
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 6,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30),
            });
        });

        ConfigureInstrumentation<TContext>(builder, settings);

        void ConfigureDbContext(IServiceProvider serviceProvider, DbContextOptionsBuilder dbContextOptionsBuilder)
        {
            // MySqlConnectorLogManager.Provider is the only way to wire MySqlConnector's internal logging
            // categories (e.g. MySqlConnector.ConnectionPool) into ILoggerFactory when using Pomelo,
            // because Pomelo doesn't use MySqlDataSource. The API is marked obsolete but there is no
            // non-obsolete alternative for this scenario.
            if (serviceProvider.GetService<ILoggerFactory>() is { } loggerFactory)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                MySqlConnectorLogManager.Provider = new MicrosoftExtensionsLoggingLoggerProvider(loggerFactory);
#pragma warning restore CS0618
            }

            var connectionString = settings.ConnectionString ?? string.Empty;

            ServerVersion serverVersion;
            if (settings.ServerVersion is null)
            {
                ConnectionStringValidation.ValidateConnectionString(settings.ConnectionString, connectionName, DefaultConfigSectionName, $"{DefaultConfigSectionName}:{typeof(TContext).Name}", isEfDesignTime: EF.IsDesignTime);

                var resiliencePipelineProvider = serviceProvider.GetRequiredService<ResiliencePipelineProvider<string>>();
                var resiliencePipeline = resiliencePipelineProvider.GetPipeline(resilienceKey);
                serverVersion = resiliencePipeline.Execute(static cs => ServerVersion.AutoDetect(cs), connectionString);
            }
            else
            {
                serverVersion = ServerVersion.Parse(settings.ServerVersion);
            }

            var builder = dbContextOptionsBuilder.UseMySql(connectionString, serverVersion, builder =>
            {
                // delay validating the ConnectionString until the DbContext is configured. This ensures an exception doesn't happen until a Logger is established.
                ConnectionStringValidation.ValidateConnectionString(settings.ConnectionString, connectionName, DefaultConfigSectionName, $"{DefaultConfigSectionName}:{typeof(TContext).Name}", isEfDesignTime: EF.IsDesignTime);

                // Resiliency:
                // 1. Connection resiliency automatically retries failed database commands: https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql/wiki/Configuration-Options#enableretryonfailure
                if (!settings.DisableRetry)
                {
                    builder.EnableRetryOnFailure();
                }
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
    public static void EnrichMySqlDbContext<[DynamicallyAccessedMembers(RequiredByEF)] TContext>(
            this IHostApplicationBuilder builder,
            Action<PomeloEntityFrameworkCoreMySqlSettings>? configureSettings = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = builder.GetDbContextSettings<TContext, PomeloEntityFrameworkCoreMySqlSettings>(
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
                    if (optionsBuilder.Options.FindExtension<MySqlOptionsExtension>() is not MySqlOptionsExtension extension ||
                        extension.ServerVersion is not ServerVersion serverVersion)
                    {
                        throw new InvalidOperationException($"A DbContextOptions<{typeof(TContext).Name}> was not found. Please ensure 'ServerVersion' was configured.");
                    }

                    optionsBuilder.UseMySql(serverVersion, options =>
                    {
                        var extension = optionsBuilder.Options.FindExtension<MySqlOptionsExtension>();

                        if (!settings.DisableRetry)
                        {
                            var executionStrategy = extension?.ExecutionStrategyFactory?.Invoke(new ExecutionStrategyDependencies(null!, optionsBuilder.Options, null!));

                            if (executionStrategy != null)
                            {
                                if (executionStrategy is MySqlRetryingExecutionStrategy)
                                {
                                    // Keep custom Retry strategy.
                                    // Any sub-class of MySqlRetryingExecutionStrategy is a valid retry strategy
                                    // which shouldn't be replaced even with DisableRetry == false
                                }
                                else if (executionStrategy.GetType() != typeof(MySqlExecutionStrategy))
                                {
                                    // Check MySqlExecutionStrategy specifically (no 'is'), any sub-class is treated as a custom strategy.

                                    throw new InvalidOperationException($"{nameof(PomeloEntityFrameworkCoreMySqlSettings)}.{nameof(PomeloEntityFrameworkCoreMySqlSettings.DisableRetry)} needs to be set when a custom Execution Strategy is configured.");
                                }
                                else
                                {
                                    options.EnableRetryOnFailure();
                                }
                            }
                            else
                            {
                                options.EnableRetryOnFailure();
                            }
                        }

                        if (settings.CommandTimeout.HasValue)
                        {
                            if (extension != null &&
                                extension.CommandTimeout.HasValue &&
                                extension.CommandTimeout != settings.CommandTimeout)
                            {
                                throw new InvalidOperationException($"Conflicting values for 'CommandTimeout' were found in {nameof(PomeloEntityFrameworkCoreMySqlSettings)} and set in DbContextOptions<{typeof(TContext).Name}>.");
                            }

                            options.CommandTimeout(settings.CommandTimeout);
                        }

                    });
                }
            }
#pragma warning restore EF1001 // Internal EF Core API usage.
        }
    }

    private static void ConfigureInstrumentation<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(IHostApplicationBuilder builder, PomeloEntityFrameworkCoreMySqlSettings settings) where TContext : DbContext
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
                    // add tracing from the underlying MySqlConnector ADO.NET library
                    tracerProviderBuilder.AddSource("MySqlConnector");
                });
        }

        if (!settings.DisableMetrics)
        {
            builder.Services.AddOpenTelemetry()
                .WithMetrics(meterProviderBuilder =>
                {
                    // add metrics from the underlying MySqlConnector ADO.NET library
                    meterProviderBuilder.AddMeter("MySqlConnector");
                });
        }
    }
}
