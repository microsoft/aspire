// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Aspire.Confluent.Kafka;

internal sealed class ConfluentKafkaMetrics
{
    private readonly Meter _meter;

    public Counter<long> Tx { get; }
    public Counter<long> TxBytes { get; }
    public Counter<long> TxMessages { get; }
    public Counter<long> TxMessageBytes { get; }
    public Counter<long> Rx { get; }
    public Counter<long> RxBytes { get; }
    public Counter<long> RxMessages { get; }
    public Counter<long> RxMessageBytes { get; }

    public ConcurrentQueue<Measurement<long>> ReplyQueueMeasurements { get; } = new ConcurrentQueue<Measurement<long>>();
    public ConcurrentQueue<Measurement<long>> MessageCountMeasurements { get; } = new ConcurrentQueue<Measurement<long>>();
    public ConcurrentQueue<Measurement<long>> MessageSizeMeasurements { get; } = new ConcurrentQueue<Measurement<long>>();

    public ConfluentKafkaMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(ConfluentKafkaCommon.MeterName);

        _meter.CreateObservableGauge(Gauges.ReplyQueue, GetReplyQMeasurements, description: Descriptions.ReplyQueue);
        _meter.CreateObservableGauge(Gauges.MessageCount, GetMessageCountMeasurements, description: Descriptions.MessageCount);
        _meter.CreateObservableGauge(Gauges.MessageSize, GetMessageSizeMeasurements, description: Descriptions.MessageSize);

        Tx = _meter.CreateCounter<long>(Counters.Tx, description: Descriptions.Tx);
        TxBytes = _meter.CreateCounter<long>(Counters.TxBytes, description: Descriptions.TxBytes);
        TxMessages = _meter.CreateCounter<long>(Counters.TxMessages, description: Descriptions.TxMessages);
        TxMessageBytes = _meter.CreateCounter<long>(Counters.TxMessageBytes, description: Descriptions.TxMessageBytes);
        Rx = _meter.CreateCounter<long>(Counters.Rx, description: Descriptions.Rx);
        RxBytes = _meter.CreateCounter<long>(Counters.RxBytes, description: Descriptions.RxBytes);
        RxMessages = _meter.CreateCounter<long>(Counters.RxMessages, description: Descriptions.RxMessages);
        RxMessageBytes = _meter.CreateCounter<long>(Counters.RxMessageBytes, description: Descriptions.RxMessageBytes);
    }

    public static class Gauges
    {
        public const string ReplyQueue = "messaging.kafka.consumer.queue.message_count";
        public const string MessageCount = "messaging.kafka.producer.queue.message_count";
        public const string MessageSize = "messaging.kafka.producer.queue.size";
    }

    public static class Counters
    {
        public const string Tx = "messaging.kafka.network.tx";
        public const string TxBytes = "messaging.kafka.network.transmitted";
        public const string Rx = "messaging.kafka.network.rx";
        public const string RxBytes = "messaging.kafka.network.received";
        public const string TxMessages = "messaging.client.sent.messages";
        public const string TxMessageBytes = "messaging.kafka.message.transmitted";
        public const string RxMessages = "messaging.client.consumed.messages";
        public const string RxMessageBytes = "messaging.kafka.message.received";
    }

    public static class Tags
    {
        public const string ClientId = "messaging.client.id";
        public const string Type = "type";
        public const string Name = "name";
    }

    private static class Descriptions
    {
        public const string ReplyQueue = "Number of ops (callbacks, events, etc) waiting in queue for application to serve with rd_kafka_poll()";
        public const string MessageCount = "Current number of messages in producer queues";
        public const string MessageSize = "Current total size of messages in producer queues";
        public const string Tx = "Total number of requests sent to Kafka brokers";
        public const string TxBytes = "Total number of bytes transmitted to Kafka brokers";
        public const string Rx = "Total number of responses received from Kafka brokers";
        public const string RxBytes = "Total number of bytes received from Kafka brokers";
        public const string TxMessages = "Total number of messages transmitted (produced) to Kafka brokers";
        public const string TxMessageBytes = "Total number of message bytes (including framing, such as per-Message framing and MessageSet/batch framing) transmitted to Kafka brokers";
        public const string RxMessages = "Total number of messages consumed, not including ignored messages (due to offset, etc), from Kafka brokers";
        public const string RxMessageBytes = "Total number of message bytes (including framing) received from Kafka brokers";
    }

    private IEnumerable<Measurement<long>> GetReplyQMeasurements()
    {
        while (ReplyQueueMeasurements.TryDequeue(out var measurement))
        {
            yield return measurement;
        }
    }

    private IEnumerable<Measurement<long>> GetMessageCountMeasurements()
    {
        while (MessageCountMeasurements.TryDequeue(out var measurement))
        {
            yield return measurement;
        }
    }

    private IEnumerable<Measurement<long>> GetMessageSizeMeasurements()
    {
        while (MessageSizeMeasurements.TryDequeue(out var measurement))
        {
            yield return measurement;
        }
    }
}
