// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Instrumentation.ConfluentKafka;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Metrics;

/// <summary>
/// Extension methods to simplify registering of Kafka instrumentation.
/// </summary>
internal static partial class MeterProviderBuilderExtensions
{
    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder)
        => AddKafkaConsumerInstrumentation<TKey, TValue>(builder, name: null, consumerBuilder: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <param name="name">The name of the instrumentation.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder, string? name)
        => AddKafkaConsumerInstrumentation<TKey, TValue>(builder, name: name, consumerBuilder: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <param name="consumerBuilder"><see cref="InstrumentedConsumerBuilder{TKey,TValue}"/> to instrument.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder,
        InstrumentedConsumerBuilder<TKey, TValue> consumerBuilder)
    {
        Guard.ThrowIfNull(consumerBuilder);

        return AddKafkaConsumerInstrumentation(builder, name: null, consumerBuilder);
    }

    /// <summary>
    /// Enables the incoming requests automatic data collection for ASP.NET.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <param name="name">The name of the instrumentation.</param>
    /// <param name="consumerBuilder"><see cref="InstrumentedConsumerBuilder{TKey,TValue}"/> to instrument.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder,
        string? name,
        InstrumentedConsumerBuilder<TKey, TValue>? consumerBuilder)
    {
        Guard.ThrowIfNull(builder);

        return builder
            .AddMeter(ConfluentKafkaCommon.Meter.Name)
            .AddInstrumentation(sp =>
            {
                if (name == null)
                {
                    consumerBuilder ??= sp.GetRequiredService<InstrumentedConsumerBuilder<TKey, TValue>>();
                }
                else
                {
                    consumerBuilder ??= sp.GetRequiredKeyedService<InstrumentedConsumerBuilder<TKey, TValue>>(name);
                }

                consumerBuilder.EnableMetrics = true;
                return new ConfluentKafkaConsumerInstrumentation<TKey, TValue>(consumerBuilder);
            });
    }
}
