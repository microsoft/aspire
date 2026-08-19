// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.StackExchange.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering Redis-related services in an <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireRedisExtensions
{
    private const string DefaultConfigSectionName = "Aspire:StackExchange:Redis";

    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging, and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="StackExchangeRedisSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional method that can be used for customizing the <see cref="ConfigurationOptions"/>. It's invoked after the options are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:StackExchange:Redis" section.</remarks>
    public static void AddRedisClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<StackExchangeRedisSettings>? configureSettings = null,
        Action<ConfigurationOptions>? configureOptions = null)
        => AddRedisClientBuilder(builder, connectionName, configureSettings, configureOptions);

    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging, and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="StackExchangeRedisSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional method that can be used for customizing the <see cref="ConfigurationOptions"/>. It's invoked after the options are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:StackExchange:Redis" section.</remarks>
    public static AspireRedisClientBuilder AddRedisClientBuilder(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<StackExchangeRedisSettings>? configureSettings = null,
        Action<ConfigurationOptions>? configureOptions = null)
        => AddRedisClient(builder, configureSettings, configureOptions, connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> as a keyed singleton for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging, and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="StackExchangeRedisSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional method that can be used for customizing the <see cref="ConfigurationOptions"/>. It's invoked after the options are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:StackExchange:Redis:{name}" section.</remarks>
    public static void AddKeyedRedisClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<StackExchangeRedisSettings>? configureSettings = null,
        Action<ConfigurationOptions>? configureOptions = null)
        => AddKeyedRedisClientBuilder(builder, name, configureSettings, configureOptions);

    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> as a keyed singleton for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging, and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="StackExchangeRedisSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional method that can be used for customizing the <see cref="ConfigurationOptions"/>. It's invoked after the options are read from the configuration.</param>
    /// <remarks>Reads the configuration from "Aspire:StackExchange:Redis:{name}" section.</remarks>
    public static AspireRedisClientBuilder AddKeyedRedisClientBuilder(
        this IHostApplicationBuilder builder,
        string name,
        Action<StackExchangeRedisSettings>? configureSettings = null,
        Action<ConfigurationOptions>? configureOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddRedisClient(builder, configureSettings, configureOptions, connectionName: name, serviceKey: name);
    }

    private static AspireRedisClientBuilder AddRedisClient(
        IHostApplicationBuilder builder,
        Action<StackExchangeRedisSettings>? configureSettings,
        Action<ConfigurationOptions>? configureOptions,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);

        StackExchangeRedisSettings settings = new();
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        var optionsName = serviceKey is null ? Options.Options.DefaultName : connectionName;

        // see comments on ConfigurationOptionsFactory for why a factory is used here
        builder.Services.AddKeyedTransient(optionsName, (sp, _) => new RedisSettingsAdapterService { Settings = settings });
        builder.Services.TryAddTransient<IOptionsFactory<ConfigurationOptions>, ConfigurationOptionsFactory>();

        builder.Services.Configure<ConfigurationOptions>(
            optionsName,
            configurationOptions =>
            {
                BindToConfiguration(configurationOptions, configSection);
                BindToConfiguration(configurationOptions, namedConfigSection);

                configureOptions?.Invoke(configurationOptions);
            });

        if (serviceKey is null)
        {
            builder.Services.AddSingleton<IConnectionMultiplexer>(sp => CreateConnection(sp, connectionName, DefaultConfigSectionName, optionsName));

            if (!settings.DisableAutoActivation)
            {
                builder.Services.ActivateSingleton<IConnectionMultiplexer>();
            }
        }
        else
        {
            builder.Services.AddKeyedSingleton<IConnectionMultiplexer>(serviceKey, (sp, _) => CreateConnection(sp, connectionName, DefaultConfigSectionName, optionsName));

            if (!settings.DisableAutoActivation)
            {
                builder.Services.ActivateKeyedSingleton<IConnectionMultiplexer>(serviceKey);
            }
        }

        if (!settings.DisableTracing)
        {
            // Supports distributed tracing
            // We don't call AddRedisInstrumentation() here as it results in the TelemetryHostedService trying to resolve & connect to IConnectionMultiplexer
            // via DI on startup which, if Redis is unavailable, can result in an app crash. Instead we add the ActivitySource manually and call
            // ConfigureRedisInstrumentation() and AddInstrumentation() to ensure the Redis instrumentation services are registered. Then when creating the
            // IConnectionMultiplexer, we register the connection with the StackExchangeRedisInstrumentation object.
            builder.Services.AddOpenTelemetry()
                .WithTracing(t =>
                {
                    t.AddSource(StackExchangeRedisConnectionInstrumentation.ActivitySource.Name);
                    // This ensures the core Redis instrumentation services from OpenTelemetry.Instrumentation.StackExchangeRedis are added
                    t.ConfigureRedisInstrumentation(_ => { });
                    // This ensures that any logic performed by the AddInstrumentation method is executed (this is usually called by AddRedisInstrumentation())
                    t.AddInstrumentation(sp => sp.GetRequiredService<StackExchangeRedisInstrumentation>());
                });
        }

        if (!settings.DisableHealthChecks)
        {
            var healthCheckName = serviceKey is null ? "StackExchange.Redis" : $"StackExchange.Redis_{connectionName}";

            builder.TryAddHealthCheck(
                healthCheckName,
                hcBuilder => hcBuilder.AddRedis(
                    // The connection factory tries to open the connection and throws when it fails.
                    // That is why we don't invoke it here, but capture the state (in a closure)
                    // and let the health check invoke it and handle the exception (if any).
                    connectionMultiplexerFactory: sp => serviceKey is null ? sp.GetRequiredService<IConnectionMultiplexer>() : sp.GetRequiredKeyedService<IConnectionMultiplexer>(serviceKey),
                    healthCheckName));
        }

        return new AspireRedisClientBuilder(builder, settings, serviceKey);
    }

    private static ConnectionMultiplexer CreateConnection(IServiceProvider serviceProvider, string connectionName, string configurationSectionName, string optionsName)
    {
        var connection = ConnectionMultiplexer.Connect(GetConfigurationOptions(serviceProvider, connectionName, configurationSectionName, optionsName));

        // Add the connection to instrumentation
        var instrumentation = serviceProvider.GetService<StackExchangeRedisInstrumentation>();
        instrumentation?.AddConnection(connection);

        return connection;
    }

    private static ConfigurationOptions GetConfigurationOptions(IServiceProvider serviceProvider, string connectionName, string configurationSectionName, string optionsName)
    {
        var configurationOptions = string.IsNullOrEmpty(optionsName) ?
            serviceProvider.GetRequiredService<IOptions<ConfigurationOptions>>().Value :
            serviceProvider.GetRequiredService<IOptionsMonitor<ConfigurationOptions>>().Get(optionsName);

        if (configurationOptions is null || configurationOptions.EndPoints.Count == 0)
        {
            throw new InvalidOperationException($"No endpoints specified. Ensure a valid connection string was provided in 'ConnectionStrings:{connectionName}' or for the '{configurationSectionName}:ConnectionString' configuration key.");
        }

        // ensure the LoggerFactory is initialized if someone hasn't already set it.
        configurationOptions.LoggerFactory ??= serviceProvider.GetService<ILoggerFactory>();

        return configurationOptions;
    }

    private static ConfigurationOptions BindToConfiguration(ConfigurationOptions options, IConfiguration configuration)
    {
        var configurationOptionsSection = configuration.GetSection("ConfigurationOptions");
        configurationOptionsSection.Bind(options);

        return options;
    }

    /// <summary>
    /// Used to pass StackExchangeRedisSettings instances to the ConfigurationOptionsFactory.
    /// </summary>
    /// <remarks>Not using StackExchangeRedisSettings itself because it is a public type that someone else could register in DI.</remarks>
    private sealed class RedisSettingsAdapterService
    {
        public required StackExchangeRedisSettings Settings { get; init; }
    }

    /// <summary>
    /// ConfigurationOptionsFactory parses a ConfigurationOptions options object from Configuration.
    /// </summary>
    /// <remarks>
    /// Using an OptionsFactory to create the object allows parsing the ConfigurationOptions IOptions object from a connection string.
    /// ConfigurationOptions.Parse(string) returns the ConfigurationOptions and doesn't support parsing to an existing object.
    /// Using a normal Configure callback isn't feasible since that only works with an existing object. Using an OptionsFactory
    /// allows us to create the initial object ourselves.
    ///
    /// This still allows for others to Configure/PostConfigure/Validate the ConfigurationOptions since it just overrides <see cref="CreateInstance(string)"/>.
    /// </remarks>
    private sealed class ConfigurationOptionsFactory : OptionsFactory<ConfigurationOptions>
    {
        private readonly IServiceProvider _serviceProvider;

        public ConfigurationOptionsFactory(IServiceProvider serviceProvider, IEnumerable<IConfigureOptions<ConfigurationOptions>> setups, IEnumerable<IPostConfigureOptions<ConfigurationOptions>> postConfigures, IEnumerable<IValidateOptions<ConfigurationOptions>> validations)
            : base(setups, postConfigures, validations)
        {
            _serviceProvider = serviceProvider;
        }

        protected override ConfigurationOptions CreateInstance(string name)
        {
            // Don't fail if the options name isn't found. Just return a blank ConfigurationOptions to be consistent
            // with the regular OptionsFactory.
            var settings = _serviceProvider.GetKeyedService<RedisSettingsAdapterService>(name);
            var connectionString = settings?.Settings.ConnectionString;

            var options = connectionString is not null ?
                ConfigurationOptions.Parse(connectionString) :
                base.CreateInstance(name);

            if (options.Defaults.GetType() == typeof(DefaultOptionsProvider))
            {
                options.Defaults = new AspireDefaultOptionsProvider();
            }

            return options;
        }
    }

    /// <summary>
    /// A Redis DefaultOptionsProvider for Aspire specific defaults.
    /// </summary>
    private sealed class AspireDefaultOptionsProvider : DefaultOptionsProvider
    {
        // Disable aborting on connect fail since we want to retry, even in local development.
        public override bool AbortOnConnectFail => false;
    }
}
