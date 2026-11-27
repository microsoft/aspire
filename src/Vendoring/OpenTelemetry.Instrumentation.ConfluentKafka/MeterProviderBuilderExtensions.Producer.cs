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
    public static MeterProviderBuilder AddKafkaProducerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder)
        => AddKafkaProducerInstrumentation<TKey, TValue>(builder, name: null, producerBuilder: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <param name="name">The name of the instrumentation.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaProducerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder, string? name)
        => AddKafkaProducerInstrumentation<TKey, TValue>(builder, name: name, producerBuilder: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <param name="producerBuilder"><see cref="InstrumentedProducerBuilder{TKey,TValue}"/> to instrument.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaProducerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder,
        InstrumentedProducerBuilder<TKey, TValue> producerBuilder)
    {
        Guard.ThrowIfNull(producerBuilder);

        return AddKafkaProducerInstrumentation(builder, name: null, producerBuilder);
    }

    /// <summary>
    /// Enables the incoming requests automatic data collection for ASP.NET.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="MeterProviderBuilder"/> being configured.</param>
    /// <param name="name">The name of the instrumentation.</param>
    /// <param name="producerBuilder"><see cref="InstrumentedProducerBuilder{TKey,TValue}"/> to instrument.</param>
    /// <returns>The instance of <see cref="MeterProviderBuilder"/> to chain the calls.</returns>
    public static MeterProviderBuilder AddKafkaProducerInstrumentation<TKey, TValue>(
        this MeterProviderBuilder builder,
        string? name,
        InstrumentedProducerBuilder<TKey, TValue>? producerBuilder)
    {
        Guard.ThrowIfNull(builder);

        return builder
            .AddMeter(ConfluentKafkaCommon.Meter.Name)
            .AddInstrumentation(sp =>
            {
                if (name == null)
                {
                    producerBuilder ??= sp.GetRequiredService<InstrumentedProducerBuilder<TKey, TValue>>();
                }
                else
                {
                    producerBuilder ??= sp.GetRequiredKeyedService<InstrumentedProducerBuilder<TKey, TValue>>(name);
                }

                producerBuilder.EnableMetrics = true;
                return new ConfluentKafkaProducerInstrumentation<TKey, TValue>(producerBuilder);
            });
    }
}
