// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.NATS.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting NATS server with NATS client
/// </summary>
public static class AspireNatsClientExtensions
{
    private const string DefaultConfigSectionName = "Aspire:NATS:Net";
    private const string ActivityNameSource = "NATS.Net";

    /// <inheritdoc cref="AddNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddNatsClient(this IHostApplicationBuilder builder, string connectionName)
        => AddNatsClientInternal(builder, connectionName: connectionName, serviceKey: null, configureSettings: null, configureOptions: null);

    /// <inheritdoc cref="AddNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddNatsClient(this IHostApplicationBuilder builder, string connectionName, Action<NatsClientSettings>? configureSettings)
        => AddNatsClientInternal(builder, connectionName: connectionName, serviceKey: null, configureSettings: configureSettings, configureOptions: null);

    /// <inheritdoc cref="AddNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddNatsClient(this IHostApplicationBuilder builder, string connectionName, Func<NatsOpts, NatsOpts>? configureOptions)
        => AddNatsClientInternal(builder, connectionName: connectionName, serviceKey: null, configureSettings: null, configureOptions: Wrap(configureOptions));

    /// <inheritdoc cref="AddNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddNatsClient(this IHostApplicationBuilder builder, string connectionName, Func<IServiceProvider, NatsOpts, NatsOpts>? configureOptions)
        => AddNatsClientInternal(builder, connectionName: connectionName, serviceKey: null, configureSettings: null, configureOptions: configureOptions);

    /// <inheritdoc cref="AddNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddNatsClient(this IHostApplicationBuilder builder, string connectionName, Action<NatsClientSettings>? configureSettings, Func<NatsOpts, NatsOpts>? configureOptions)
        => AddNatsClientInternal(builder, connectionName: connectionName, serviceKey: null, configureSettings: configureSettings, configureOptions: Wrap(configureOptions));

    /// <summary>
    /// Registers <see cref="INatsConnection"/> service for connecting NATS server with NATS client.
    /// Configures health check and logging for the NATS client.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional delegate that can be used for customizing NATS options that aren't exposed as standard configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="NatsClientSettings.ConnectionString"/> is not provided.</exception>
    public static void AddNatsClient(this IHostApplicationBuilder builder, string connectionName, Action<NatsClientSettings>? configureSettings, Func<IServiceProvider, NatsOpts, NatsOpts>? configureOptions)
    {
        AddNatsClientInternal(builder, connectionName: connectionName, serviceKey: null, configureSettings: configureSettings, configureOptions: configureOptions);
    }

    /// <inheritdoc cref="AddKeyedNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddKeyedNatsClient(this IHostApplicationBuilder builder, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddNatsClientInternal(builder, connectionName: name, serviceKey: name, configureSettings: null, configureOptions: null);
    }

    /// <inheritdoc cref="AddKeyedNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddKeyedNatsClient(this IHostApplicationBuilder builder, string name, Action<NatsClientSettings>? configureSettings)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddNatsClientInternal(builder, connectionName: name, serviceKey: name, configureSettings: configureSettings, configureOptions: null);
    }

    /// <inheritdoc cref="AddKeyedNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddKeyedNatsClient(this IHostApplicationBuilder builder, string name, Func<NatsOpts, NatsOpts>? configureOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddNatsClientInternal(builder, connectionName: name, serviceKey: name, configureSettings: null, configureOptions: Wrap(configureOptions));
    }

    /// <inheritdoc cref="AddKeyedNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddKeyedNatsClient(this IHostApplicationBuilder builder, string name, Func<IServiceProvider, NatsOpts, NatsOpts>? configureOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddNatsClientInternal(builder, connectionName: name, serviceKey: name, configureSettings: null, configureOptions: configureOptions);
    }

    /// <inheritdoc cref="AddKeyedNatsClient(IHostApplicationBuilder, string, Action{NatsClientSettings}?, Func{IServiceProvider,NatsOpts,NatsOpts}?)"/>
    public static void AddKeyedNatsClient(this IHostApplicationBuilder builder, string name, Action<NatsClientSettings>? configureSettings, Func<NatsOpts, NatsOpts>? configureOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddNatsClientInternal(builder, connectionName: name, serviceKey: name, configureSettings: configureSettings, configureOptions: Wrap(configureOptions));
    }

    /// <summary>
    /// Registers <see cref="INatsConnection"/> as a keyed service for given <paramref name="name"/> for connecting NATS server with NATS client.
    /// Configures health check and logging for the NATS client.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional delegate that can be used for customizing NATS options that aren't exposed as standard configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if mandatory <paramref name="name"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when mandatory <see cref="NatsClientSettings.ConnectionString"/> is not provided.</exception>
    public static void AddKeyedNatsClient(this IHostApplicationBuilder builder, string name, Action<NatsClientSettings>? configureSettings, Func<IServiceProvider, NatsOpts, NatsOpts>? configureOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddNatsClientInternal(builder, connectionName: name, serviceKey: name, configureSettings: configureSettings, configureOptions: configureOptions);
    }

    private static void AddNatsClientInternal(this IHostApplicationBuilder builder, string connectionName, object? serviceKey, Action<NatsClientSettings>? configureSettings, Func<IServiceProvider, NatsOpts, NatsOpts>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        NatsClientSettings settings = new();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        NatsConnection Factory(IServiceProvider provider)
        {
            var options = NatsOpts.Default with
            {
                LoggerFactory = provider.GetRequiredService<ILoggerFactory>(),
                SerializerRegistry = NatsClientDefaultSerializerRegistry.Default,
            };

            if (configureOptions != null)
            {
                options = configureOptions(provider, options);
            }

            if (settings.ConnectionString == null)
            {
                throw new InvalidOperationException($"NATS connection string not found: {connectionName}");
            }

            options = options with { Url = settings.ConnectionString };

            return new NatsConnection(options);
        }

        if (serviceKey == null)
        {
            builder.Services.TryAddSingleton(Factory);
            builder.Services.TryAddSingleton<INatsConnection>(static provider => provider.GetRequiredService<NatsConnection>());
            builder.Services.TryAddSingleton<INatsClient>(static provider => provider.GetRequiredService<NatsConnection>());
        }
        else
        {
            builder.Services.TryAddKeyedSingleton<NatsConnection>(serviceKey, (provider, _) => Factory(provider));
            builder.Services.TryAddKeyedSingleton<INatsConnection>(serviceKey, static (provider, key) => provider.GetRequiredKeyedService<NatsConnection>(key));
            builder.Services.TryAddKeyedSingleton<INatsClient>(serviceKey, static (provider, key) => provider.GetRequiredKeyedService<NatsConnection>(key));
        }

        if (!settings.DisableHealthChecks)
        {
            builder.TryAddHealthCheck(new HealthCheckRegistration(
                serviceKey is null ? "NATS" : $"NATS_{connectionName}",
                sp => new NatsHealthCheck(serviceKey is null
                    ? sp.GetRequiredService<INatsConnection>()
                    : sp.GetRequiredKeyedService<INatsConnection>(serviceKey)),
                failureStatus: default,
                tags: default,
                timeout: default));
        }

        if (!settings.DisableTracing)
        {
            builder.Services
                .AddOpenTelemetry()
                .WithTracing(tracer =>
                {
                    tracer.AddSource(ActivityNameSource);
                });
        }
    }

    /// <summary>
    /// Registers <see cref="INatsJSContext"/> service for NATS JetStream operations.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <exception cref="ArgumentNullException">Thrown if mandatory <paramref name="builder"/> is null.</exception>
    public static void AddNatsJetStream(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<INatsJSContext>(static provider =>
        {
            return new NatsJSContextFactory().CreateContext(provider.GetRequiredService<INatsConnection>());
        });
    }

    private static Func<IServiceProvider, NatsOpts, NatsOpts>? Wrap(Func<NatsOpts, NatsOpts>? func)
    {
        if (func is null)
        {
            return null;
        }

        return (_, options) => func(options);
    }
}
