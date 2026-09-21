// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Kafka.Dekaf;
using Dekaf;
using Dekaf.Producer;
using Dekaf.Extensions.DependencyInjection;
using Dekaf.Extensions.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting to a Kafka broker.
/// </summary>
public static class AspireDekafKafkaProducerExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Kafka:Dekaf:Producer";

    /// <inheritdoc cref="AddDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName)
        => AddDekafKafkaProducerInternal<TKey, TValue>(builder, null, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaProducerSettings>? configureSettings)
        => AddDekafKafkaProducerInternal<TKey, TValue>(builder, configureSettings, null, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaProducerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaProducerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaProducerSettings>? configureSettings, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
        => AddDekafKafkaProducerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName, serviceKey: null);

    /// <summary>
    /// Registers <see cref="IKafkaProducer{TKey,TValue}"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method used for customizing the <see cref="KafkaProducerSettings"/>.</param>
    /// <param name="configureBuilder">A method used for customizing the <see cref="ProducerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>
    /// Reads configuration from <c>Aspire:Kafka:Dekaf:Producer</c> and its named subsection.
    /// Native <c>Config</c> options are applied before the builder callback. The host initializes the client at startup.
    /// <code>builder.AddDekafKafkaProducer&lt;string, string&gt;("messaging");</code>
    /// </remarks>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string connectionName, Action<KafkaProducerSettings>? configureSettings, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
       => AddDekafKafkaProducerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName, serviceKey: null);

    /// <inheritdoc cref="AddKeyedDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaProducerInternal<TKey, TValue>(builder, null, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaProducerSettings>? configureSettings)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaProducerInternal<TKey, TValue>(builder, configureSettings, null, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaProducerInternal<TKey, TValue>(builder, null, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaProducerInternal<TKey, TValue>(builder, null, configureBuilder, connectionName: name, serviceKey: name);
    }

    /// <inheritdoc cref="AddKeyedDekafKafkaProducer{TKey, TValue}(IHostApplicationBuilder, string, Action{KafkaProducerSettings}?, Action{IServiceProvider, ProducerBuilder{TKey, TValue}}?)"/>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaProducerSettings>? configureSettings, Action<ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaProducerInternal<TKey, TValue>(builder, configureSettings, Wrap(configureBuilder), connectionName: name, serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="IKafkaProducer{TKey,TValue}"/> as a keyed singleton for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method used for customizing the <see cref="KafkaProducerSettings"/>.</param>
    /// <param name="configureBuilder">An optional method used for customizing the <see cref="ProducerBuilder{TKey,TValue}"/>.</param>
    /// <remarks>
    /// Reads configuration from <c>Aspire:Kafka:Dekaf:Producer</c> and its named subsection.
    /// <code>builder.AddKeyedDekafKafkaProducer&lt;string, string&gt;("messaging");</code>
    /// </remarks>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafKafkaProducer<TKey, TValue>(this IHostApplicationBuilder builder, string name, Action<KafkaProducerSettings>? configureSettings, Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddDekafKafkaProducerInternal<TKey, TValue>(builder, configureSettings, configureBuilder, connectionName: name, serviceKey: name);
    }

    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    private static void AddDekafKafkaProducerInternal<TKey, TValue>(
        IHostApplicationBuilder builder,
        Action<KafkaProducerSettings>? configureSettings,
        Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? configureBuilder,
        string connectionName,
        string? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var configuration = DekafKafkaCommon.GetConfiguration(builder, DefaultConfigSectionName, connectionName);
        var settings = configuration.Get<KafkaProducerSettings>() ?? new();
        settings.ConnectionString = builder.Configuration.GetConnectionString(connectionName) ?? settings.ConnectionString;
        configureSettings?.Invoke(settings);

        builder.Services.AddDekaf(dekaf =>
        {
            void Configure(IServiceProvider services, ProducerBuilder<TKey, TValue> client)
            {
                if (settings.ConnectionString is not null)
                {
                    client.WithBootstrapServers(settings.ConnectionString);
                }

                configureBuilder?.Invoke(services, client);
            }

            if (serviceKey is null)
            {
                dekaf.AddProducer(configuration.GetSection("Config"), (Action<IServiceProvider, ProducerBuilder<TKey, TValue>>)Configure);
            }
            else
            {
                dekaf.AddProducer(serviceKey, configuration.GetSection("Config"), (Action<IServiceProvider, ProducerBuilder<TKey, TValue>>)Configure);
            }
        });

        DekafKafkaCommon.AddTelemetry(builder, settings.DisableMetrics, settings.DisableTracing);

        if (!settings.DisableHealthChecks)
        {
            var healthCheckName = DekafKafkaCommon.GetHealthCheckName("producer", serviceKey);
            builder.TryAddHealthCheck(new HealthCheckRegistration(healthCheckName,
                services => new DekafProducerHealthCheck<TKey, TValue>(
                    serviceKey is null
                        ? services.GetRequiredService<IKafkaProducer<TKey, TValue>>()
                        : services.GetRequiredKeyedService<IKafkaProducer<TKey, TValue>>(serviceKey),
                    settings.HealthCheck),
                failureStatus: default, tags: default));
        }
    }

    private static Action<IServiceProvider, ProducerBuilder<TKey, TValue>>? Wrap<TKey, TValue>(Action<ProducerBuilder<TKey, TValue>>? action)
        => action is null ? null : (_, builder) => action(builder);
}
