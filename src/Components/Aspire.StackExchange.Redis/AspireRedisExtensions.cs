// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
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
        configurationOptionsSection.Bind(new BindableConfigurationOptions(options));

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

        // StackExchange.Redis 3 prefers RESP3 and falls back to RESP2 when the server doesn't support it.
        public override RedisProtocol? Protocol => RedisProtocol.Resp3;
    }

    /// <summary>
    /// Limits source-generated configuration binding to supported Redis options.
    /// </summary>
    /// <remarks>
    /// StackExchange.Redis 3.2 retains several properties that are marked as compile-time errors or experimental.
    /// A forwarding type lets the configuration binder remain AOT-compatible without generating references to
    /// those properties.
    /// </remarks>
    internal sealed class BindableConfigurationOptions(ConfigurationOptions options)
    {
        private readonly ConfigurationOptions _options = options;

        public bool AbortOnConnectFail
        {
            get => _options.AbortOnConnectFail;
            set => _options.AbortOnConnectFail = value;
        }

        public bool AllowAdmin
        {
            get => _options.AllowAdmin;
            set => _options.AllowAdmin = value;
        }

        public int AsyncTimeout
        {
            get => _options.AsyncTimeout;
            set => _options.AsyncTimeout = value;
        }

        public BacklogPolicy BacklogPolicy
        {
            get => _options.BacklogPolicy;
            set => _options.BacklogPolicy = value;
        }

        public Action<EndPoint, ConnectionType, Socket>? BeforeSocketConnect
        {
            get => _options.BeforeSocketConnect;
            set => _options.BeforeSocketConnect = value;
        }

        public RedisChannel ChannelPrefix
        {
            get => _options.ChannelPrefix;
            set => _options.ChannelPrefix = value;
        }

        public bool CheckCertificateRevocation
        {
            get => _options.CheckCertificateRevocation;
            set => _options.CheckCertificateRevocation = value;
        }

        public string? ClientName
        {
            get => _options.ClientName;
            set => _options.ClientName = value;
        }

        public CommandMap CommandMap
        {
            get => _options.CommandMap;
            set => _options.CommandMap = value;
        }

        public int ConfigCheckSeconds
        {
            get => _options.ConfigCheckSeconds;
            set => _options.ConfigCheckSeconds = value;
        }

        public string ConfigurationChannel
        {
            get => _options.ConfigurationChannel;
            set => _options.ConfigurationChannel = value;
        }

        public int ConnectRetry
        {
            get => _options.ConnectRetry;
            set => _options.ConnectRetry = value;
        }

        public int ConnectTimeout
        {
            get => _options.ConnectTimeout;
            set => _options.ConnectTimeout = value;
        }

        public int? DefaultDatabase
        {
            get => _options.DefaultDatabase;
            set => _options.DefaultDatabase = value;
        }

        public DefaultOptionsProvider Defaults
        {
            get => _options.Defaults;
            set => _options.Defaults = value;
        }

        public Version DefaultVersion
        {
            get => _options.DefaultVersion;
            set => _options.DefaultVersion = value;
        }

        public EndPointCollection EndPoints => _options.EndPoints;

        public bool HeartbeatConsistencyChecks
        {
            get => _options.HeartbeatConsistencyChecks;
            set => _options.HeartbeatConsistencyChecks = value;
        }

        public TimeSpan HeartbeatInterval
        {
            get => _options.HeartbeatInterval;
            set => _options.HeartbeatInterval = value;
        }

        public bool HighIntegrity
        {
            get => _options.HighIntegrity;
            set => _options.HighIntegrity = value;
        }

        public bool IncludeDetailInExceptions
        {
            get => _options.IncludeDetailInExceptions;
            set => _options.IncludeDetailInExceptions = value;
        }

        public bool IncludePerformanceCountersInExceptions
        {
            get => _options.IncludePerformanceCountersInExceptions;
            set => _options.IncludePerformanceCountersInExceptions = value;
        }

        public int KeepAlive
        {
            get => _options.KeepAlive;
            set => _options.KeepAlive = value;
        }

        public string? LibraryName
        {
            get => _options.LibraryName;
            set => _options.LibraryName = value;
        }

        public ILoggerFactory? LoggerFactory
        {
            get => _options.LoggerFactory;
            set => _options.LoggerFactory = value;
        }

        public string? Password
        {
            get => _options.Password;
            set => _options.Password = value;
        }

        public RedisProtocol? Protocol
        {
            get => _options.Protocol;
            set => _options.Protocol = value;
        }

        public Proxy Proxy
        {
            get => _options.Proxy;
            set => _options.Proxy = value;
        }

        public MemoryPool<byte>? RequestBufferPool
        {
            get => _options.RequestBufferPool;
            set => _options.RequestBufferPool = value;
        }

        public bool ResolveDns
        {
            get => _options.ResolveDns;
            set => _options.ResolveDns = value;
        }

        public MemoryPool<byte>? ResponseBufferPool
        {
            get => _options.ResponseBufferPool;
            set => _options.ResponseBufferPool = value;
        }

        public string? SentinelPassword
        {
            get => _options.SentinelPassword;
            set => _options.SentinelPassword = value;
        }

        public string? SentinelUser
        {
            get => _options.SentinelUser;
            set => _options.SentinelUser = value;
        }

        public string? ServiceName
        {
            get => _options.ServiceName;
            set => _options.ServiceName = value;
        }

        public bool SetClientLibrary
        {
            get => _options.SetClientLibrary;
            set => _options.SetClientLibrary = value;
        }

        public bool Ssl
        {
            get => _options.Ssl;
            set => _options.Ssl = value;
        }

        public Func<string, SslClientAuthenticationOptions>? SslClientAuthenticationOptions
        {
            get => _options.SslClientAuthenticationOptions;
            set => _options.SslClientAuthenticationOptions = value;
        }

        public string? SslHost
        {
            get => _options.SslHost;
            set => _options.SslHost = value;
        }

        public SslProtocols? SslProtocols
        {
            get => _options.SslProtocols;
            set => _options.SslProtocols = value;
        }

        public int SyncTimeout
        {
            get => _options.SyncTimeout;
            set => _options.SyncTimeout = value;
        }

        public bool TcpKeepAlive
        {
            get => _options.TcpKeepAlive;
            set => _options.TcpKeepAlive = value;
        }

        public string TieBreaker
        {
            get => _options.TieBreaker;
            set => _options.TieBreaker = value;
        }

        public Tunnel? Tunnel
        {
            get => _options.Tunnel;
            set => _options.Tunnel = value;
        }

        public string? User
        {
            get => _options.User;
            set => _options.User = value;
        }
    }
}
