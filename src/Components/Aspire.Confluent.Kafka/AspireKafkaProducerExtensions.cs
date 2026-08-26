// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Confluent.Kafka;
using Confluent.Kafka;
using HealthChecks.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting to a Kafka broker.
/// </summary>
public static class AspireKafkaProducerExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Confluent:Kafka:Producer";

    /// <inheritdoc cref="AddKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName)
        => AddKafkaProducerInternal<TKey, TValue>(builder, null, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaProducerSettings>? configureSettings)
        => AddKafkaProducerInternal<TKey, TValue>(builder, configureSettings, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaProducerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaProducerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaProducerSettings>? configureSettings, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaProducerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="IProducer{TKey,TValue}"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method used for customizing the <see cref="KafkaProducerSettings"/>.</param>
    /// <param name="configureBuilder">A method used for customizing the <see cref="ProducerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Kafka:Producer" section.</remarks>
    public static void AddKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaProducerSettings>? configureSettings, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
       => AddKafkaProducerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKeyedKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaProducerInternal<TKey, TValue>(builder, null, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaProducerSettings>? configureSettings)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaProducerInternal<TKey, TValue>(builder, configureSettings, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaProducerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaProducerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaProducerSettings>? configureSettings, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaProducerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="IProducer{TKey,TValue}"/> as a keyed singleton for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method used for customizing the <see cref="KafkaProducerSettings"/>.</param>
    /// <param name="configureBuilder">An optional method used for customizing the <see cref="ProducerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Kafka:Producer:{name}" section.</remarks>
    public static void AddKeyedKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaProducerSettings>? configureSettings, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaProducerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName: name, serviceKey: name);
    }

    private static void AddKafkaProducerInternal<TKey, TValue>(
        IHostApplicationBuilder builder,
        Action<KafkaProducerSettings>? configureSettings,
        Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = BuildProducerSettings(builder, configureSettings, connectionName);

        if (serviceKey is null)
        {
            builder.Services.AddSingleton<InstrumentedProducerBuilder<TKey, TValue>>(sp => CreateProducerBuilder(sp, configureBuilder, settings));
            builder.Services.AddSingleton<ProducerConnectionFactory<TKey, TValue>>(sp => CreateProducerConnectionFactory<TKey, TValue>(sp, settings));
            builder.Services.AddSingleton<IProducer<TKey, TValue>>(sp => sp.GetRequiredService<ProducerConnectionFactory<TKey, TValue>>().Create());
        }
        else
        {
            builder.Services.AddKeyedSingleton<InstrumentedProducerBuilder<TKey, TValue>>(serviceKey, (sp, key) => CreateProducerBuilder(sp, configureBuilder, settings));
            builder.Services.AddKeyedSingleton<ProducerConnectionFactory<TKey, TValue>>(serviceKey, (sp, key) => CreateProducerConnectionFactory<TKey, TValue>(sp, settings, key as string));
            builder.Services.AddKeyedSingleton<IProducer<TKey, TValue>>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<ProducerConnectionFactory<TKey, TValue>>(key).Create());
        }

        if (!settings.DisableMetrics)
        {
            if (ConfluentKafkaCommon.IsAspire8ConfluentKafkaMetricsEnabled)
            {
                builder.Services.TryAddSingleton<MetricsChannel>();
                builder.Services.AddHostedService<MetricsService>();
                builder.Services.TryAddSingleton<ConfluentKafkaMetrics>();
            }

            builder.Services.AddOpenTelemetry().WithMetrics(metricBuilderProvider =>
            {
                if (ConfluentKafkaCommon.IsAspire8ConfluentKafkaMetricsEnabled)
                {
                    metricBuilderProvider.AddMeter(ConfluentKafkaCommon.MeterName);
                }

                metricBuilderProvider.AddKafkaProducerInstrumentation<TKey, TValue>(name: serviceKey);
            });
        }

        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracing =>
            {
                tracing.AddKafkaProducerInstrumentation<TKey, TValue>(name: serviceKey);
            });
        }

        if (!settings.DisableHealthChecks)
        {
            string healthCheckName = serviceKey is null
                ? ConfluentKafkaCommon.ProducerHealthCheckName
                : string.Concat(ConfluentKafkaCommon.KeyedProducerHealthCheckName, connectionName);

            builder.Services.TryAddKeyedSingleton<KafkaHealthCheck>(healthCheckName,
                (sp, _) =>
                {
                    var connectionFactory = serviceKey is null
                        ? sp.GetRequiredService<ProducerConnectionFactory<TKey, TValue>>()
                        : sp.GetRequiredKeyedService<ProducerConnectionFactory<TKey, TValue>>(serviceKey);

                    var options = new KafkaHealthCheckOptions();
                    options.Configuration = new ProducerConfig(connectionFactory.Config.ToDictionary());
                    options.Configuration.SocketTimeoutMs = 1000;
                    options.Configuration.MessageTimeoutMs = 1000;
                    options.Configuration.StatisticsIntervalMs = 0;
                    return new KafkaHealthCheck(options);
                });

            builder.TryAddHealthCheck(new HealthCheckRegistration(healthCheckName,
                sp => sp.GetRequiredKeyedService<KafkaHealthCheck>(healthCheckName),
                failureStatus: default,
                tags: default));
        }
    }

    private static ProducerConnectionFactory<TKey, TValue> CreateProducerConnectionFactory<TKey, TValue>(
        IServiceProvider serviceProvider, KafkaProducerSettings settings, string? key = null)
    {
        return key is null
            ? new(serviceProvider.GetRequiredService<InstrumentedProducerBuilder<TKey, TValue>>(), settings.Config)
            : new(serviceProvider.GetRequiredKeyedService<InstrumentedProducerBuilder<TKey, TValue>>(key),
                settings.Config);
    }

    private static InstrumentedProducerBuilder<TKey, TValue> CreateProducerBuilder<TKey, TValue>(IServiceProvider serviceProvider, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder, KafkaProducerSettings settings)
    {
        settings.Validate();

        InstrumentedProducerBuilder<TKey, TValue> builder = new(settings.Config);
        ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(ConfluentKafkaCommon.LogCategoryName);
        configureBuilder?.Invoke(serviceProvider, builder);

        try
        {
            void OnLog(IProducer<TKey, TValue> _, LogMessage logMessage) =>
               logger.Log((LogLevel)logMessage.LevelAs(LogLevelType.MicrosoftExtensionsLogging), logMessage.Facility?.GetHashCode() ?? 0, logMessage.Message, null, static (value, ex) => value);

            builder.SetLogHandler(OnLog);
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning("LogHandler is already set. Skipping... No logs will be written.");
        }

        if (!settings.DisableMetrics && ConfluentKafkaCommon.IsAspire8ConfluentKafkaMetricsEnabled)
        {
            MetricsChannel channel = serviceProvider.GetRequiredService<MetricsChannel>();
            void OnStatistics(IProducer<TKey, TValue> _, string json)
            {
                if (string.IsNullOrEmpty(json))
                {
                    return;
                }

                // StatisticsHandler is called on the producer poll thread, we need to offload the processing
                // to avoid slowing the producer down.
                channel.Writer.TryWrite(json);
            }
            ;

            try
            {
                builder.SetStatisticsHandler(OnStatistics);
            }
            catch (InvalidOperationException)
            {
                logger.LogWarning("StatisticsHandler is already set. Skipping... No metrics will be exposed.");
            }
        }
        return builder;
    }

    private static KafkaProducerSettings BuildProducerSettings(IHostApplicationBuilder builder, Action<KafkaProducerSettings>? configureSettings, string connectionName)
    {
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        KafkaProducerSettings settings = new();
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        // Manually bind the ProducerConfig until https://github.com/dotnet/runtime/issues/96652 is fixed
        configSection.GetSection(nameof(KafkaProducerSettings.Config)).Bind(settings.Config);
        namedConfigSection.GetSection(nameof(KafkaProducerSettings.Config)).Bind(settings.Config);

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        settings.Consolidate();
        return settings;
    }

    private static Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? Wrap<TKey, TValue>(Action<ProducerBuilder<TKey, TValue>>? action)
    {
        if (action is null)
        {
            return null;
        }

        return (_, builder) => action(builder);
    }
}
