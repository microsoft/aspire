// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Confluent.Kafka;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.ConfluentKafka;

internal class InstrumentedConsumer<TKey, TValue> : IConsumer<TKey, TValue>
{
    private readonly IConsumer<TKey, TValue> consumer;
    private readonly ConfluentKafkaConsumerInstrumentationOptions<TKey, TValue> options;

    public InstrumentedConsumer(IConsumer<TKey, TValue> consumer, ConfluentKafkaConsumerInstrumentationOptions<TKey, TValue> options)
    {
        this.consumer = consumer;
        this.options = options;
    }

    public Handle Handle => this.consumer.Handle;

    public string Name => this.consumer.Name;

    public string MemberId => this.consumer.MemberId;

    public List<TopicPartition> Assignment => this.consumer.Assignment;

    public List<string> Subscription => this.consumer.Subscription;

    public IConsumerGroupMetadata ConsumerGroupMetadata => this.consumer.ConsumerGroupMetadata;

    public string? GroupId { get; internal set; }

    public void Dispose()
        => this.consumer.Dispose();

    public int AddBrokers(string brokers)
        => this.consumer.AddBrokers(brokers);

    public void SetSaslCredentials(string username, string password)
        => this.consumer.SetSaslCredentials(username, password);

    public ConsumeResult<TKey, TValue>? Consume(int millisecondsTimeout)
    {
        var start = DateTimeOffset.UtcNow;
        ConsumeResult<TKey, TValue>? result = null;
        ConsumeResult consumeResult = default;
        string? errorType = null;
        try
        {
            result = this.consumer.Consume(millisecondsTimeout);
            consumeResult = ExtractConsumeResult(result);
            return result;
        }
        catch (ConsumeException e)
        {
            (consumeResult, errorType) = ExtractConsumeResult(e);
            throw;
        }
        finally
        {
            if (ShouldInstrument(result, errorType))
            {
                var end = DateTimeOffset.UtcNow;
                this.InstrumentConsumption(start, end, consumeResult, errorType);
            }
        }
    }

    public ConsumeResult<TKey, TValue>? Consume(CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        ConsumeResult<TKey, TValue>? result = null;
        ConsumeResult consumeResult = default;
        string? errorType = null;
        try
        {
            result = this.consumer.Consume(cancellationToken);
            consumeResult = ExtractConsumeResult(result);
            return result;
        }
        catch (ConsumeException e)
        {
            (consumeResult, errorType) = ExtractConsumeResult(e);
            throw;
        }
        finally
        {
            if (ShouldInstrument(result, errorType))
            {
                var end = DateTimeOffset.UtcNow;
                this.InstrumentConsumption(start, end, consumeResult, errorType);
            }
        }
    }

    public ConsumeResult<TKey, TValue>? Consume(TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        ConsumeResult<TKey, TValue>? result = null;
        ConsumeResult consumeResult = default;
        string? errorType = null;
        try
        {
            result = this.consumer.Consume(timeout);
            consumeResult = ExtractConsumeResult(result);
            return result;
        }
        catch (ConsumeException e)
        {
            (consumeResult, errorType) = ExtractConsumeResult(e);
            throw;
        }
        finally
        {
            if (ShouldInstrument(result, errorType))
            {
                var end = DateTimeOffset.UtcNow;
                this.InstrumentConsumption(start, end, consumeResult, errorType);
            }
        }
    }

    public void Subscribe(IEnumerable<string> topics)
        => this.consumer.Subscribe(topics);

    public void Subscribe(string topic)
        => this.consumer.Subscribe(topic);

    public void Unsubscribe()
        => this.consumer.Unsubscribe();

    public void Assign(TopicPartition partition)
        => this.consumer.Assign(partition);

    public void Assign(TopicPartitionOffset partition)
        => this.consumer.Assign(partition);

    public void Assign(IEnumerable<TopicPartitionOffset> partitions)
        => this.consumer.Assign(partitions);

    public void Assign(IEnumerable<TopicPartition> partitions)
        => this.consumer.Assign(partitions);

    public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions)
        => this.consumer.IncrementalAssign(partitions);

    public void IncrementalAssign(IEnumerable<TopicPartition> partitions)
        => this.consumer.IncrementalAssign(partitions);

    public void IncrementalUnassign(IEnumerable<TopicPartition> partitions)
        => this.consumer.IncrementalUnassign(partitions);

    public void Unassign() => this.consumer.Unassign();

    public void StoreOffset(ConsumeResult<TKey, TValue> result)
        => this.consumer.StoreOffset(result);

    public void StoreOffset(TopicPartitionOffset offset)
        => this.consumer.StoreOffset(offset);

    public List<TopicPartitionOffset> Commit()
        => this.consumer.Commit();

    public void Commit(IEnumerable<TopicPartitionOffset> offsets)
        => this.consumer.Commit(offsets);

    public void Commit(ConsumeResult<TKey, TValue> result)
        => this.consumer.Commit(result);

    public void Seek(TopicPartitionOffset tpo)
        => this.consumer.Seek(tpo);

    public void Pause(IEnumerable<TopicPartition> partitions)
        => this.consumer.Pause(partitions);

    public void Resume(IEnumerable<TopicPartition> partitions)
        => this.consumer.Resume(partitions);

    public List<TopicPartitionOffset> Committed(TimeSpan timeout)
        => this.consumer.Committed(timeout);

    public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout)
        => this.consumer.Committed(partitions, timeout);

    public Offset Position(TopicPartition partition)
        => this.consumer.Position(partition);

    public List<TopicPartitionOffset> OffsetsForTimes(IEnumerable<TopicPartitionTimestamp> timestampsToSearch, TimeSpan timeout)
        => this.consumer.OffsetsForTimes(timestampsToSearch, timeout);

    public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition)
        => this.consumer.GetWatermarkOffsets(topicPartition);

    public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout)
        => this.consumer.QueryWatermarkOffsets(topicPartition, timeout);

    public void Close()
        => this.consumer.Close();

    private static bool ShouldInstrument(ConsumeResult<TKey, TValue>? result, string? errorType) =>
        result is { IsPartitionEOF: false } ||
        (result is null && errorType is not null);

    private static string FormatConsumeException(ConsumeException consumeException) =>
        $"ConsumeException: {consumeException.Error}";

    private static ConsumeResult ExtractConsumeResult(ConsumeResult<TKey, TValue> result) => result switch
    {
        null => new ConsumeResult(null, null),
        { Message: null } => new ConsumeResult(result.TopicPartitionOffset, null),
        _ => new ConsumeResult(result.TopicPartitionOffset, result.Message.Headers, result.Message.Key),
    };

    private static (ConsumeResult ConsumeResult, string ErrorType) ExtractConsumeResult(ConsumeException exception) => exception switch
    {
        { ConsumerRecord: null } => (new ConsumeResult(null, null), FormatConsumeException(exception)),
        { ConsumerRecord.Message: null } => (new ConsumeResult(exception.ConsumerRecord.TopicPartitionOffset, null), FormatConsumeException(exception)),
        _ => (new ConsumeResult(exception.ConsumerRecord.TopicPartitionOffset, exception.ConsumerRecord.Message.Headers, exception.ConsumerRecord.Message.Key), FormatConsumeException(exception)),
    };

    private static void GetTags(string? topic, out TagList tags, int? partition = null, string? errorType = null)
    {
        tags = new TagList()
        {
            new KeyValuePair<string, object?>(
                SemanticConventions.AttributeMessagingOperation,
                ConfluentKafkaCommon.ReceiveOperationName),
            new KeyValuePair<string, object?>(
                SemanticConventions.AttributeMessagingSystem,
                ConfluentKafkaCommon.KafkaMessagingSystem),
        };

        if (topic is not null)
        {
            tags.Add(
                new KeyValuePair<string, object?>(
                    SemanticConventions.AttributeMessagingDestinationName,
                    topic));
        }

        if (partition is not null)
        {
            tags.Add(
                new KeyValuePair<string, object?>(
                    SemanticConventions.AttributeMessagingKafkaDestinationPartition,
                    partition));
        }

        if (errorType is not null)
        {
            tags.Add(
                new KeyValuePair<string, object?>(
                    SemanticConventions.AttributeErrorType,
                    errorType));
        }
    }

    private static void RecordReceive(TopicPartition? topicPartition, TimeSpan duration, string? errorType = null)
    {
        GetTags(topicPartition?.Topic, out var tags, partition: topicPartition?.Partition, errorType);

        ConfluentKafkaCommon.ReceiveMessagesCounter.Add(1, in tags);
        ConfluentKafkaCommon.ReceiveDurationHistogram.Record(duration.TotalSeconds, in tags);
    }

    private void InstrumentConsumption(DateTimeOffset startTime, DateTimeOffset endTime, ConsumeResult consumeResult, string? errorType)
    {
        if (this.options.Traces)
        {
            var propagationContext = consumeResult.Headers != null
                ? OpenTelemetryConsumeResultExtensions.ExtractPropagationContext(consumeResult.Headers)
                : default;

            using var activity = this.StartReceiveActivity(propagationContext, startTime, consumeResult.TopicPartitionOffset, consumeResult.Key);
            if (activity != null)
            {
                if (errorType != null)
                {
                    activity.SetStatus(ActivityStatusCode.Error);
                    if (activity.IsAllDataRequested)
                    {
                        activity.SetTag(SemanticConventions.AttributeErrorType, errorType);
                    }
                }

                activity.SetEndTime(endTime.UtcDateTime);
            }
        }

        if (this.options.Metrics)
        {
            var duration = endTime - startTime;
            RecordReceive(consumeResult.TopicPartitionOffset?.TopicPartition, duration, errorType);
        }
    }

    private Activity? StartReceiveActivity(PropagationContext propagationContext, DateTimeOffset start, TopicPartitionOffset? topicPartitionOffset, object? key)
    {
#pragma warning disable IDE0370 // Suppression is unnecessary
        var spanName = string.IsNullOrEmpty(topicPartitionOffset?.Topic)
            ? ConfluentKafkaCommon.ReceiveOperationName
            : string.Concat(topicPartitionOffset!.Topic, " ", ConfluentKafkaCommon.ReceiveOperationName);
#pragma warning restore IDE0370 // Suppression is unnecessary

        ActivityLink[] activityLinks = propagationContext.ActivityContext.IsValid()
            ? [new ActivityLink(propagationContext.ActivityContext)]
            : [];

        var activity = ConfluentKafkaCommon.ActivitySource.StartActivity(spanName, kind: ActivityKind.Consumer, links: activityLinks, startTime: start, parentContext: default);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(SemanticConventions.AttributeMessagingSystem, ConfluentKafkaCommon.KafkaMessagingSystem);
            activity.SetTag(SemanticConventions.AttributeMessagingClientId, this.Name);
            activity.SetTag(SemanticConventions.AttributeMessagingDestinationName, topicPartitionOffset?.Topic);
            activity.SetTag(SemanticConventions.AttributeMessagingKafkaDestinationPartition, topicPartitionOffset?.Partition.Value);
            activity.SetTag(SemanticConventions.AttributeMessagingKafkaMessageOffset, topicPartitionOffset?.Offset.Value);
            activity.SetTag(SemanticConventions.AttributeMessagingKafkaConsumerGroup, this.GroupId);
            activity.SetTag(SemanticConventions.AttributeMessagingOperation, ConfluentKafkaCommon.ReceiveOperationName);
            if (key != null)
            {
                activity.SetTag(SemanticConventions.AttributeMessagingKafkaMessageKey, key);
            }
        }

        return activity;
    }

    private readonly record struct ConsumeResult(
        TopicPartitionOffset? TopicPartitionOffset,
        Headers? Headers,
        object? Key = null)
    {
        public object? Key { get; } = Key;

        public Headers? Headers { get; } = Headers;

        public TopicPartitionOffset? TopicPartitionOffset { get; } = TopicPartitionOffset;
    }
}
