// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Confluent.Kafka;

namespace Aspire.Confluent.Kafka.Tests;

internal sealed class FakeKafkaProducer<TKey, TValue> : IProducer<TKey, TValue>
{
    public required Exception ExceptionToThrow { get; init; }

    public int HandleReadCount => Volatile.Read(ref _handleReadCount);

    public Handle Handle
    {
        get
        {
            Interlocked.Increment(ref _handleReadCount);
            return null!;
        }
    }

    public string Name => "fake-producer";

    public Task<DeliveryResult<TKey, TValue>> ProduceAsync(string topic, Message<TKey, TValue> message, CancellationToken cancellationToken = default) =>
        Task.FromException<DeliveryResult<TKey, TValue>>(ExceptionToThrow);

    public Task<DeliveryResult<TKey, TValue>> ProduceAsync(TopicPartition topicPartition, Message<TKey, TValue> message, CancellationToken cancellationToken = default) =>
        Task.FromException<DeliveryResult<TKey, TValue>>(ExceptionToThrow);

    public void Produce(string topic, Message<TKey, TValue> message, Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null) =>
        throw ExceptionToThrow;

    public void Produce(TopicPartition topicPartition, Message<TKey, TValue> message, Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null) =>
        throw ExceptionToThrow;

    public int AddBrokers(string brokers) => throw new NotSupportedException();

    public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

    public int Poll(TimeSpan timeout) => throw new NotSupportedException();

    public int Flush(TimeSpan timeout) => throw new NotSupportedException();

    public void Flush(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void InitTransactions(TimeSpan timeout) => throw new NotSupportedException();

    public void BeginTransaction() => throw new NotSupportedException();

    public void CommitTransaction(TimeSpan timeout) => throw new NotSupportedException();

    public void CommitTransaction() => throw new NotSupportedException();

    public void AbortTransaction(TimeSpan timeout) => throw new NotSupportedException();

    public void AbortTransaction() => throw new NotSupportedException();

    public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout) =>
        throw new NotSupportedException();

    public void Dispose()
    {
    }

    private int _handleReadCount;
}
