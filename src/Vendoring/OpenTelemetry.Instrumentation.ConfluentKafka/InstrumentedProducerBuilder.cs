// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Instrumentation.ConfluentKafka;

namespace Confluent.Kafka;

/// <summary>
/// A builder of <see cref="IProducer{TKey,TValue}"/> with support for instrumentation.
/// </summary>
/// <typeparam name="TKey">Type of the key.</typeparam>
/// <typeparam name="TValue">Type of value.</typeparam>
internal sealed class InstrumentedProducerBuilder<TKey, TValue> : ProducerBuilder<TKey, TValue>
{
    private readonly ConfluentKafkaProducerInstrumentationOptions<TKey, TValue> options = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="InstrumentedProducerBuilder{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="config"> A collection of librdkafka configuration parameters (refer to https://github.com/edenhill/librdkafka/blob/master/CONFIGURATION.md) and parameters specific to this client (refer to: <see cref="ConfigPropertyNames" />). At a minimum, 'bootstrap.servers' must be specified.</param>
    public InstrumentedProducerBuilder(IEnumerable<KeyValuePair<string, string>> config)
        : base(config)
    {
    }

    internal bool EnableMetrics
    {
        get => this.options.Metrics;
        set => this.options.Metrics = value;
    }

    internal bool EnableTraces
    {
        get => this.options.Traces;
        set => this.options.Traces = value;
    }

    /// <summary>
    /// Build a new IProducer instance.
    /// </summary>
    /// <returns>an <see cref="IProducer{TKey,TValue}"/>.</returns>
    public override IProducer<TKey, TValue> Build()
    {
        return new InstrumentedProducer<TKey, TValue>(base.Build(), this.options, ((ClientConfig)this.Config).BootstrapServers);
    }
}
