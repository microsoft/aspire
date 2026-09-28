// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;

public class Aspire8MetricsTests
{
    [Theory]
    [ClassData(typeof(GetExposesStatisticsAsCountersAndGaugeInitializeCountersTestVariations))]
    public async Task ExposesStatisticsAsCountersAndGauge_InitializeCounters(TestVariationData variation)
    {
        var useKeyed = variation.UseKeyed;
        var statistics = variation.Statistics;

        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", CommonHelpers.TestingEndpoint),
            new KeyValuePair<string, string?>(ProducerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaConsumer<string, string>("messaging");
        }
        else
        {
            builder.AddKafkaConsumer<string, string>("messaging");
        }

        using var host = builder.Build();
        await host.StartAsync();

        var metricsChannel = host.Services.GetRequiredService(ReflectionHelpers.MetricsChannelType.Value);
        var writer = GetMetricsChannelWriter(metricsChannel)!;
        var meterFactory = host.Services.GetRequiredService<IMeterFactory>();
        var collectorConsumerQueueMessageCount = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.consumer.queue.message_count");
        var collectorProducerQueueMessageCount = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.producer.queue.message_count");
        var collectorProducerQueueSize = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.producer.queue.size");
        var collectorNetworkTx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.tx");
        var collectorNetworkTransmitted = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.transmitted");
        var collectorNetworkRx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.rx");
        var collectorNetworkReceived = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.received");
        var collectorMessageTx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.client.sent.messages");
        var collectorMessageTransmitted = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.message.transmitted");
        var collectorMessageRx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.client.consumed.messages");
        var collectorMessageReceived = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.message.received");

        foreach (var statistic in statistics)
        {
            writer.TryWrite(statistic);
        }

        await Task.WhenAll(
            collectorNetworkTx.WaitForMeasurementsAsync(statistics.Count),
            collectorNetworkTransmitted.WaitForMeasurementsAsync(statistics.Count),
            collectorNetworkRx.WaitForMeasurementsAsync(statistics.Count),
            collectorNetworkReceived.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageTx.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageTransmitted.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageRx.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageReceived.WaitForMeasurementsAsync(statistics.Count)
        );

        collectorConsumerQueueMessageCount.RecordObservableInstruments();
        collectorProducerQueueMessageCount.RecordObservableInstruments();
        collectorProducerQueueSize.RecordObservableInstruments();

        Assert.Equal(100, collectorProducerQueueMessageCount.LastMeasurement!.Value);
        Assert.Contains(collectorProducerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorProducerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");

        Assert.Equal(100, collectorConsumerQueueMessageCount.LastMeasurement!.Value);
        Assert.Contains(collectorConsumerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorConsumerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");

        Assert.Equal(1638400, collectorProducerQueueSize.LastMeasurement!.Value);
        Assert.Contains(collectorProducerQueueSize.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorProducerQueueSize.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");

        Assert.Equal(5, collectorNetworkTx.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkTx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkTx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkTx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorNetworkTransmitted.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkTransmitted.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkTransmitted.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkTransmitted.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(5, collectorNetworkRx.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkRx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkRx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkRx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorNetworkReceived.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkReceived.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkReceived.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkReceived.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(5, collectorMessageTx.LastMeasurement!.Value);
        Assert.Equal("{message}", collectorMessageTx.Instrument!.Unit);
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "messaging.system" && t.Value!.ToString() == "kafka");
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "messaging.operation.name" && t.Value!.ToString() == "send");
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorMessageTransmitted.LastMeasurement!.Value);
        Assert.Contains(collectorMessageTransmitted.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageTransmitted.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageTransmitted.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(5, collectorMessageRx.LastMeasurement!.Value);
        Assert.Equal("{message}", collectorMessageRx.Instrument!.Unit);
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "messaging.system" && t.Value!.ToString() == "kafka");
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "messaging.operation.name" && t.Value!.ToString() == "poll");
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorMessageReceived.LastMeasurement!.Value);
        Assert.Contains(collectorMessageReceived.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageReceived.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageReceived.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");
    }

    [Theory]
    [ClassData(typeof(GetExposesStatisticsAsCountersAndGaugeAggregateCountersByNameTestVariations))]
    public async Task ExposesStatisticsAsCountersAndGauge_AggregateCountersByName(TestVariationData variation)
    {
        var useKeyed = variation.UseKeyed;
        var statistics = variation.Statistics;

        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "messaging" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:messaging", CommonHelpers.TestingEndpoint),
            new KeyValuePair<string, string?>(ProducerConformanceTests.CreateConfigKey("Aspire:Confluent:Kafka:Consumer", key, "Config:GroupId"), "unused")
        ]);

        if (useKeyed)
        {
            builder.AddKeyedKafkaConsumer<string, string>("messaging");
        }
        else
        {
            builder.AddKafkaConsumer<string, string>("messaging");
        }

        using var host = builder.Build();
        await host.StartAsync();

        var metricsChannel = host.Services.GetRequiredService(ReflectionHelpers.MetricsChannelType.Value);
        var writer = GetMetricsChannelWriter(metricsChannel)!;
        var meterFactory = host.Services.GetRequiredService<IMeterFactory>();
        var collectorConsumerQueueMessageCount = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.consumer.queue.message_count");
        var collectorProducerQueueMessageCount = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.producer.queue.message_count");
        var collectorProducerQueueSize = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.producer.queue.size");
        var collectorNetworkTx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.tx");
        var collectorNetworkTransmitted = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.transmitted");
        var collectorNetworkRx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.rx");
        var collectorNetworkReceived = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.network.received");
        var collectorMessageTx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.client.sent.messages");
        var collectorMessageTransmitted = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.message.transmitted");
        var collectorMessageRx = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.client.consumed.messages");
        var collectorMessageReceived = new MetricCollector<long>(meterFactory, "Aspire.Confluent.Kafka", "messaging.kafka.message.received");

        foreach (var statistic in statistics)
        {
            writer.TryWrite(statistic);
        }

        await Task.WhenAll(
            collectorNetworkTx.WaitForMeasurementsAsync(statistics.Count),
            collectorNetworkTransmitted.WaitForMeasurementsAsync(statistics.Count),
            collectorNetworkRx.WaitForMeasurementsAsync(statistics.Count),
            collectorNetworkReceived.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageTx.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageTransmitted.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageRx.WaitForMeasurementsAsync(statistics.Count),
            collectorMessageReceived.WaitForMeasurementsAsync(statistics.Count)
        );

        collectorConsumerQueueMessageCount.RecordObservableInstruments();
        collectorProducerQueueMessageCount.RecordObservableInstruments();
        collectorProducerQueueSize.RecordObservableInstruments();

        Assert.Equal(200, collectorProducerQueueMessageCount.LastMeasurement!.Value);
        Assert.Contains(collectorProducerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorProducerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");

        Assert.Equal(200, collectorConsumerQueueMessageCount.LastMeasurement!.Value);
        Assert.Contains(collectorConsumerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorConsumerQueueMessageCount.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");

        Assert.Equal(3276800, collectorProducerQueueSize.LastMeasurement!.Value);
        Assert.Contains(collectorProducerQueueSize.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorProducerQueueSize.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");

        Assert.Equal(5, collectorNetworkTx.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkTx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkTx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkTx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorNetworkTransmitted.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkTransmitted.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkTransmitted.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkTransmitted.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(5, collectorNetworkRx.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkRx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkRx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkRx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorNetworkReceived.LastMeasurement!.Value);
        Assert.Contains(collectorNetworkReceived.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorNetworkReceived.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorNetworkReceived.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(5, collectorMessageTx.LastMeasurement!.Value);
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageTx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorMessageTransmitted.LastMeasurement!.Value);
        Assert.Contains(collectorMessageTransmitted.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageTransmitted.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageTransmitted.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(5, collectorMessageRx.LastMeasurement!.Value);
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageRx.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");

        Assert.Equal(1638400, collectorMessageReceived.LastMeasurement!.Value);
        Assert.Contains(collectorMessageReceived.LastMeasurement!.Tags, t => t.Key == "messaging.client.id" && t.Value!.ToString() == "rdkafka");
        Assert.Contains(collectorMessageReceived.LastMeasurement!.Tags, t => t.Key == "name" && t.Value!.ToString() == "rdkafka#producer-1");
        Assert.Contains(collectorMessageReceived.LastMeasurement!.Tags, t => t.Key == "type" && t.Value!.ToString() == "producer");
    }

    private static ChannelWriter<string>? GetMetricsChannelWriter(object o) => ReflectionHelpers.MetricsChannelType.Value.GetProperty("Writer")!.GetValue(o) as ChannelWriter<string>;

    public class GetExposesStatisticsAsCountersAndGaugeInitializeCountersTestVariations : TheoryData<TestVariationData>
    {
        public GetExposesStatisticsAsCountersAndGaugeInitializeCountersTestVariations()
        {
            var s1 = """
                     {
                         "client_id": "rdkafka",
                         "type": "producer",
                         "name": "rdkafka#producer-1",
                         "replyq": 100,
                         "msg_cnt": 100,
                         "msg_size": 1638400,
                         "tx": 5,
                         "tx_bytes": 1638400,
                         "txmsgs": 5,
                         "txmsg_bytes": 1638400,
                         "rx": 5,
                         "rx_bytes": 1638400,
                         "rxmsgs": 5,
                         "rxmsg_bytes": 1638400
                     }
                     """;
            Add(new TestVariationData()
            {
                UseKeyed = true,
                Statistics = [s1]
            });
            Add(new TestVariationData()
            {
                UseKeyed = false,
                Statistics = [s1]
            });
        }
    }

    public class GetExposesStatisticsAsCountersAndGaugeAggregateCountersByNameTestVariations : TheoryData<TestVariationData>
    {
        public GetExposesStatisticsAsCountersAndGaugeAggregateCountersByNameTestVariations()
        {
            var s1 = """
                     {
                         "client_id": "rdkafka",
                         "type": "producer",
                         "name": "rdkafka#producer-1",
                         "replyq": 100,
                         "msg_cnt": 100,
                         "msg_size": 1638400,
                         "tx": 5,
                         "tx_bytes": 1638400,
                         "txmsgs": 5,
                         "txmsg_bytes": 1638400,
                         "rx": 5,
                         "rx_bytes": 1638400,
                         "rxmsgs": 5,
                         "rxmsg_bytes": 1638400
                     }
                     """;
            var s2 = """
                     {
                         "client_id": "rdkafka",
                         "type": "producer",
                         "name": "rdkafka#producer-1",
                         "replyq": 200,
                         "msg_cnt": 200,
                         "msg_size": 3276800,
                         "tx": 10,
                         "tx_bytes": 3276800,
                         "txmsgs": 10,
                         "txmsg_bytes": 3276800,
                         "rx": 10,
                         "rx_bytes": 3276800,
                         "rxmsgs": 10,
                         "rxmsg_bytes": 3276800
                     }
                     """;
            Add(new TestVariationData()
            {
                UseKeyed = true,
                Statistics = [s1, s2]
            });
            Add(new TestVariationData()
            {
                UseKeyed = false,
                Statistics = [s1, s2]
            });
        }
    }

    public record TestVariationData
    {
        public bool UseKeyed { get; set; }
        public required List<string> Statistics { get; set; }
    }
}
