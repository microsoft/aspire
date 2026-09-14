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
public static class AspireKafkaConsumerExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Confluent:Kafka:Consumer";

    /// <inheritdoc cref="AddKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    public static void AddKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName)
        => AddKafkaConsumerInternal<TKey, TValue>(builder, null, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    public static void AddKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaConsumerSettings>? configureSettings)
        => AddKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    public static void AddKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaConsumerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    public static void AddKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaConsumerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    public static void AddKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaConsumerSettings>? configureSettings, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="IConsumer{TKey,TValue}"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method for customizing the <see cref="KafkaConsumerSettings"/>.</param>
    /// <param name="configureBuilder">An optional method used for customizing the <see cref="ConsumerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Kafka:Consumer" section.</remarks>
    public static void AddKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaConsumerSettings>? configureSettings, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKeyedKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaConsumerInternal<TKey, TValue>(builder, null, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaConsumerSettings>? configureSettings)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaConsumerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaConsumerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    public static void AddKeyedKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaConsumerSettings>? configureSettings, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="IConsumer{TKey,TValue}"/> as a keyed singleton for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method for customizing the <see cref="KafkaConsumerSettings"/>.</param>
    /// <param name="configureBuilder">An optional method used for customizing the <see cref="ConsumerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Kafka:Consumer:{name}" section.</remarks>
    public static void AddKeyedKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaConsumerSettings>? configureSettings, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName: name, serviceKey: name);
    }

    private static void AddKafkaConsumerInternal<TKey, TValue>(
        IHostApplicationBuilder builder,
        Action<KafkaConsumerSettings>? configureSettings,
        Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = BuildConsumerSettings(builder, configureSettings, connectionName);

        if (serviceKey is null)
        {
            builder.Services.AddSingleton<InstrumentedConsumerBuilder<TKey, TValue>>(sp => CreateConsumerBuilder(sp, configureBuilder, settings));
            builder.Services.AddSingleton<ConsumerConnectionFactory<TKey, TValue>>(sp => CreateConsumerConnectionFactory<TKey, TValue>(sp, settings));
            builder.Services.AddSingleton<IConsumer<TKey, TValue>>(sp => sp.GetRequiredService<ConsumerConnectionFactory<TKey, TValue>>().Create());
        }
        else
        {
            builder.Services.AddKeyedSingleton<InstrumentedConsumerBuilder<TKey, TValue>>(serviceKey, (sp, key) => CreateConsumerBuilder(sp, configureBuilder, settings));
            builder.Services.AddKeyedSingleton<ConsumerConnectionFactory<TKey, TValue>>(serviceKey, (sp, key) => CreateConsumerConnectionFactory<TKey, TValue>(sp, settings, key as string));
            builder.Services.AddKeyedSingleton<IConsumer<TKey, TValue>>(serviceKey, (sp, key) => sp.GetRequiredKeyedService<ConsumerConnectionFactory<TKey, TValue>>(key).Create());
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

                metricBuilderProvider.AddKafkaConsumerInstrumentation<TKey, TValue>(name: serviceKey);
            });
        }

        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracing =>
            {
                tracing.AddKafkaConsumerInstrumentation<TKey, TValue>(name: serviceKey);
            });
        }

        if (!settings.DisableHealthChecks)
        {
            string healthCheckName = serviceKey is null
                ? ConfluentKafkaCommon.ConsumerHealthCheckName
                : string.Concat(ConfluentKafkaCommon.KeyedConsumerHealthCheckName, connectionName);

            builder.Services.TryAddKeyedSingleton<KafkaHealthCheck>(healthCheckName,
                (sp, _) =>
                {
                    var connectionFactory = serviceKey is null
                        ? sp.GetRequiredService<ConsumerConnectionFactory<TKey, TValue>>()
                        : sp.GetRequiredKeyedService<ConsumerConnectionFactory<TKey, TValue>>(serviceKey);

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

    private static ConsumerConnectionFactory<TKey, TValue> CreateConsumerConnectionFactory<TKey, TValue>(
        IServiceProvider serviceProvider, KafkaConsumerSettings settings, string? key = null)
    {
        return key is null
            ? new(serviceProvider.GetRequiredService<InstrumentedConsumerBuilder<TKey, TValue>>(), settings.Config)
            : new(serviceProvider.GetRequiredKeyedService<InstrumentedConsumerBuilder<TKey, TValue>>(key),
                settings.Config);
    }

    private static InstrumentedConsumerBuilder<TKey, TValue> CreateConsumerBuilder<TKey, TValue>(IServiceProvider serviceProvider, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder, KafkaConsumerSettings settings)
    {
        settings.Validate();

        InstrumentedConsumerBuilder<TKey, TValue> builder = new(settings.Config);
        ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(ConfluentKafkaCommon.LogCategoryName);
        configureBuilder?.Invoke(serviceProvider, builder);

        try
        {
            void OnLog(IConsumer<TKey, TValue> _, LogMessage logMessage) =>
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
            void OnStatistics(IConsumer<TKey, TValue> _, string json)
            {
                if (string.IsNullOrEmpty(json))
                {
                    return;
                }

                // StatisticsHandler is called on the consume thread, so we need to offload the processing
                // to avoid slowing the consumer down.
                channel.Writer.TryWrite(json);
            }

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

    private static KafkaConsumerSettings BuildConsumerSettings(IHostApplicationBuilder builder, Action<KafkaConsumerSettings>? configureSettings, string connectionName)
    {
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        KafkaConsumerSettings settings = new();
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        // Manually bind the ConsumerConfig until https://github.com/dotnet/runtime/issues/96652 is fixed
        configSection.GetSection(nameof(KafkaConsumerSettings.Config)).Bind(settings.Config);
        namedConfigSection.GetSection(nameof(KafkaConsumerSettings.Config)).Bind(settings.Config);

        if (builder.Configuration.TryGetConnectionString(connectionName, out var connectionString))
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        settings.Consolidate();
        return settings;
    }

    private static Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? Wrap<TKey, TValue>(Action<ConsumerBuilder<TKey, TValue>>? action)
    {
        if (action is null)
        {
            return null;
        }

        return (_, builder) => action(builder);
    }
}
