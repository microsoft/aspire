// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Instrumentation.ConfluentKafka;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Trace;

/// <summary>
/// Extension methods to simplify registering of dependency instrumentation.
/// </summary>
internal static partial class TracerProviderBuilderExtensions
{
    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this TracerProviderBuilder builder)
        => AddKafkaConsumerInstrumentation<TKey, TValue>(builder, name: null, consumerBuilder: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="name">The name of the instrumentation.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this TracerProviderBuilder builder, string? name)
        => AddKafkaConsumerInstrumentation<TKey, TValue>(builder, name: name, consumerBuilder: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="consumerBuilder"><see cref="InstrumentedConsumerBuilder{TKey,TValue}"/> to instrument.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this TracerProviderBuilder builder,
        InstrumentedConsumerBuilder<TKey, TValue> consumerBuilder)
    {
        Guard.ThrowIfNull(consumerBuilder);

        return AddKafkaConsumerInstrumentation(builder, name: null, consumerBuilder);
    }

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Kafka.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="name">The name of the instrumentation.</param>
    /// <param name="consumerBuilder">Optional <see cref="InstrumentedConsumerBuilder{TKey, TValue}"/> to instrument.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddKafkaConsumerInstrumentation<TKey, TValue>(
        this TracerProviderBuilder builder,
        string? name,
        InstrumentedConsumerBuilder<TKey, TValue>? consumerBuilder)
    {
        Guard.ThrowIfNull(builder);

        return builder
            .AddSource(ConfluentKafkaCommon.ActivitySource.Name)
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

                consumerBuilder.EnableTraces = true;
                return new ConfluentKafkaConsumerInstrumentation<TKey, TValue>(consumerBuilder);
            });
    }
}
