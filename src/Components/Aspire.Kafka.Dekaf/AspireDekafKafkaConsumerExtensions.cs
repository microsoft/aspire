// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Kafka.Dekaf;
using Dekaf;
using Dekaf.Consumer;
using Dekaf.Extensions.DependencyInjection;
using Dekaf.Extensions.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting to a Kafka broker.
/// </summary>
public static class AspireDekafKafkaConsumerExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Kafka:Dekaf:Consumer";

    /// <inheritdoc cref="AddDekafKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName)
        => AddDekafKafkaConsumerInternal<TKey, TValue>(builder, null, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaConsumerSettings>? configureSettings)
        => AddDekafKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaConsumerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaConsumerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaConsumer{TKey,TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider,ConsumerBuilder{TKey,TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaConsumerSettings>? configureSettings, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="IKafkaConsumer{TKey,TValue}"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method for customizing the <see cref="KafkaConsumerSettings"/>.</param>
    /// <param name="configureBuilder">An optional method used for customizing the <see cref="ConsumerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>
    /// Reads configuration from <c>Aspire:Kafka:Dekaf:Consumer</c> and its named subsection.
    /// Native <c>Config</c> options are applied before the builder callback. The host initializes the client at startup.
    /// <code>builder.AddDekafKafkaConsumer&lt;string, string&gt;("messaging");</code>
    /// </remarks>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaConsumerSettings>? configureSettings, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKeyedDekafKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaConsumerInternal<TKey, TValue>(builder, null, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaConsumerSettings>? configureSettings)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaConsumerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaConsumerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaConsumer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaConsumerSettings}?, Action{IServiceProvider, ConsumerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaConsumerSettings>? configureSettings, Action<ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="IKafkaConsumer{TKey,TValue}"/> as a keyed singleton for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method for customizing the <see cref="KafkaConsumerSettings"/>.</param>
    /// <param name="configureBuilder">An optional method used for customizing the <see cref="ConsumerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>
    /// Reads configuration from <c>Aspire:Kafka:Dekaf:Consumer</c> and its named subsection.
    /// <code>builder.AddKeyedDekafKafkaConsumer&lt;string, string&gt;("messaging");</code>
    /// </remarks>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaConsumer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaConsumerSettings>? configureSettings, Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaConsumerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName: name, serviceKey: name);
    }

    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    private static void AddDekafKafkaConsumerInternal<TKey, TValue>(
        IHostApplicationBuilder builder,
        Action<KafkaConsumerSettings>? configureSettings,
        Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? configureBuilder,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var configuration = DekafKafkaCommon.GetConfiguration(builder, DefaultConfigSectionName, connectionName);
        var settings = configuration.Get<KafkaConsumerSettings>() ?? new();
        settings.ConnectionString = builder.Configuration.GetConnectionString(connectionName) ?? settings.ConnectionString;
        configureSettings?.Invoke(settings);

        builder.Services.AddDekaf(dekaf =>
        {
            void Configure(IServiceProvider services, ConsumerBuilder<TKey, TValue> client)
            {
                if (settings.ConnectionString is not null)
                {
                    client.WithBootstrapServers(settings.ConnectionString);
                }

                configureBuilder?.Invoke(services, client);
            }

            if (serviceKey is null)
            {
                dekaf.AddConsumer(configuration.GetSection("Config"), (Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>)Configure);
            }
            else
            {
                dekaf.AddConsumer(serviceKey, configuration.GetSection("Config"), (Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>)Configure);
            }
        });

        DekafKafkaCommon.AddTelemetry(builder, settings.DisableMetrics, settings.DisableTracing);

        if (!settings.DisableHealthChecks)
        {
            var healthCheckName = DekafKafkaCommon.GetHealthCheckName("consumer", serviceKey);
            builder.TryAddHealthCheck(new HealthCheckRegistration(healthCheckName,
                services => new DekafConsumerHealthCheck<TKey, TValue>(
                    serviceKey is null
                        ? services.GetRequiredService<IKafkaConsumer<TKey, TValue>>()
                        : services.GetRequiredKeyedService<IKafkaConsumer<TKey, TValue>>(serviceKey),
                    settings.HealthCheck),
                failureStatus: default, tags: default));
        }
    }

    private static Action<IServiceProvider, ConsumerBuilder<TKey, TValue>>? Wrap<TKey, TValue>(Action<ConsumerBuilder<TKey, TValue>>? action)
        => action is null ? null : (_, builder) => action(builder);
}
