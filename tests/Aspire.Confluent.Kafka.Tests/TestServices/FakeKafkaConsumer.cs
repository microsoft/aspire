// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Confluent.Kafka;

namespace Aspire.Confluent.Kafka.Tests;

internal sealed class FakeKafkaConsumer<TKey, TValue> : IConsumer<TKey, TValue>
{
    public ConsumeResult<TKey, TValue>? ConsumeResult { get; set; }

    public ConsumeException? ExceptionToThrow { get; set; }

    public Handle Handle => null!;

    public string Name => "fake-consumer";

    public string MemberId => string.Empty;

    public List<TopicPartition> Assignment => [];

    public List<string> Subscription => [];

    public IConsumerGroupMetadata ConsumerGroupMetadata => null!;

    public int AddBrokers(string brokers) => 0;

    public void SetSaslCredentials(string username, string password)
    {
    }

    public ConsumeResult<TKey, TValue>? Consume(int millisecondsTimeout) =>
        ExceptionToThrow is not null ? throw ExceptionToThrow : ConsumeResult;

    public ConsumeResult<TKey, TValue>? Consume(CancellationToken cancellationToken = default) =>
        ExceptionToThrow is not null ? throw ExceptionToThrow : ConsumeResult;

    public ConsumeResult<TKey, TValue>? Consume(TimeSpan timeout) =>
        ExceptionToThrow is not null ? throw ExceptionToThrow : ConsumeResult;

    public void Subscribe(IEnumerable<string> topics)
    {
    }

    public void Subscribe(string topic)
    {
    }

    public void Unsubscribe()
    {
    }

    public void Assign(TopicPartition partition)
    {
    }

    public void Assign(TopicPartitionOffset partition)
    {
    }

    public void Assign(IEnumerable<TopicPartitionOffset> partitions)
    {
    }

    public void Assign(IEnumerable<TopicPartition> partitions)
    {
    }

    public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions)
    {
    }

    public void IncrementalAssign(IEnumerable<TopicPartition> partitions)
    {
    }

    public void IncrementalUnassign(IEnumerable<TopicPartition> partitions)
    {
    }

    public void Unassign()
    {
    }

    public void StoreOffset(ConsumeResult<TKey, TValue> result)
    {
    }

    public void StoreOffset(TopicPartitionOffset offset)
    {
    }

    public List<TopicPartitionOffset> Commit() => [];

    public void Commit(IEnumerable<TopicPartitionOffset> offsets)
    {
    }

    public void Commit(ConsumeResult<TKey, TValue> result)
    {
    }

    public void Seek(TopicPartitionOffset tpo)
    {
    }

    public void Pause(IEnumerable<TopicPartition> partitions)
    {
    }

    public void Resume(IEnumerable<TopicPartition> partitions)
    {
    }

    public List<TopicPartitionOffset> Committed(TimeSpan timeout) => [];

    public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout) => [];

    public Offset Position(TopicPartition partition) => Offset.Unset;

    public List<TopicPartitionOffset> OffsetsForTimes(IEnumerable<TopicPartitionTimestamp> timestampsToSearch, TimeSpan timeout) => [];

    public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition) => new(Offset.Unset, Offset.Unset);

    public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout) => new(Offset.Unset, Offset.Unset);

    public void Close()
    {
    }

    public void Dispose()
    {
    }
}
