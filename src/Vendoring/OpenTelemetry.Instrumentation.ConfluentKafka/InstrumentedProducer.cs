// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.ConfluentKafka;

internal sealed class InstrumentedProducer<TKey, TValue> : IProducer<TKey, TValue>
{
    private readonly TextMapPropagator propagator = Propagators.DefaultTextMapPropagator;
    private readonly IProducer<TKey, TValue> producer;
    private readonly ConfluentKafkaProducerInstrumentationOptions<TKey, TValue> options;

    public InstrumentedProducer(
        IProducer<TKey, TValue> producer,
        ConfluentKafkaProducerInstrumentationOptions<TKey, TValue> options)
    {
        this.producer = producer;
        this.options = options;
    }

    public Handle Handle => this.producer.Handle;

    public string Name => this.producer.Name;

    public int AddBrokers(string brokers)
        => this.producer.AddBrokers(brokers);

    public void SetSaslCredentials(string username, string password)
        => this.producer.SetSaslCredentials(username, password);

    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(
        string topic,
        Message<TKey, TValue> message,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        using var activity = this.StartPublishActivity(start, topic, message);
        if (activity != null)
        {
            this.InjectActivity(activity, message);
        }

        DeliveryResult<TKey, TValue> result;
        string? errorType = null;
        try
        {
            result = await this.producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
        }
        catch (ProduceException<TKey, TValue> produceException)
        {
            errorType = FormatProduceException(produceException);
            activity?.SetStatus(ActivityStatusCode.Error, produceException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        catch (ArgumentException argumentException)
        {
            errorType = FormatArgumentException(argumentException);
            activity?.SetStatus(ActivityStatusCode.Error, argumentException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        finally
        {
            var end = DateTimeOffset.UtcNow;
            activity?.SetEndTime(end.UtcDateTime);
            var duration = end - start;

            if (this.options.Metrics)
            {
                RecordPublish(topic, duration, errorType);
            }
        }

        return result;
    }

    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(
        TopicPartition topicPartition,
        Message<TKey, TValue> message,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        using var activity = this.StartPublishActivity(start, topicPartition.Topic, message, topicPartition.Partition);
        if (activity != null)
        {
            this.InjectActivity(activity, message);
        }

        DeliveryResult<TKey, TValue> result;
        string? errorType = null;
        try
        {
            result = await this.producer.ProduceAsync(topicPartition, message, cancellationToken).ConfigureAwait(false);
        }
        catch (ProduceException<TKey, TValue> produceException)
        {
            errorType = FormatProduceException(produceException);
            activity?.SetStatus(ActivityStatusCode.Error, produceException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        catch (ArgumentException argumentException)
        {
            errorType = FormatArgumentException(argumentException);
            activity?.SetStatus(ActivityStatusCode.Error, argumentException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        finally
        {
            var end = DateTimeOffset.UtcNow;
            activity?.SetEndTime(end.UtcDateTime);
            var duration = end - start;

            if (this.options.Metrics)
            {
                RecordPublish(topicPartition, duration, errorType);
            }
        }

        return result;
    }

    public void Produce(string topic, Message<TKey, TValue> message, Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null)
    {
        var start = DateTimeOffset.UtcNow;
        using var activity = this.StartPublishActivity(start, topic, message);
        if (activity != null)
        {
            this.InjectActivity(activity, message);
        }

        string? errorType = null;
        try
        {
            this.producer.Produce(topic, message, deliveryHandler);
        }
        catch (ProduceException<TKey, TValue> produceException)
        {
            errorType = FormatProduceException(produceException);
            activity?.SetStatus(ActivityStatusCode.Error, produceException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        catch (ArgumentException argumentException)
        {
            errorType = FormatArgumentException(argumentException);
            activity?.SetStatus(ActivityStatusCode.Error, argumentException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        finally
        {
            var end = DateTimeOffset.UtcNow;
            activity?.SetEndTime(end.UtcDateTime);
            var duration = end - start;

            if (this.options.Metrics)
            {
                RecordPublish(topic, duration, errorType);
            }
        }
    }

    public void Produce(TopicPartition topicPartition, Message<TKey, TValue> message, Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null)
    {
        var start = DateTimeOffset.UtcNow;
        using var activity = this.StartPublishActivity(start, topicPartition.Topic, message, topicPartition.Partition);
        if (activity != null)
        {
            this.InjectActivity(activity, message);
        }

        string? errorType = null;
        try
        {
            this.producer.Produce(topicPartition, message, deliveryHandler);
        }
        catch (ProduceException<TKey, TValue> produceException)
        {
            errorType = FormatProduceException(produceException);
            activity?.SetStatus(ActivityStatusCode.Error, produceException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        catch (ArgumentException argumentException)
        {
            errorType = FormatArgumentException(argumentException);
            activity?.SetStatus(ActivityStatusCode.Error, argumentException.Message);
            activity?.SetTag(SemanticConventions.AttributeErrorType, errorType);

            throw;
        }
        finally
        {
            var end = DateTimeOffset.UtcNow;
            activity?.SetEndTime(end.UtcDateTime);
            var duration = end - start;

            if (this.options.Metrics)
            {
                RecordPublish(topicPartition, duration, errorType);
            }
        }
    }

    public int Poll(TimeSpan timeout)
         => this.producer.Poll(timeout);

    public int Flush(TimeSpan timeout)
         => this.producer.Flush(timeout);

    public void Flush(CancellationToken cancellationToken = default) =>
        this.producer.Flush(cancellationToken);

    public void InitTransactions(TimeSpan timeout) =>
        this.producer.InitTransactions(timeout);

    public void BeginTransaction() =>
        this.producer.BeginTransaction();

    public void CommitTransaction(TimeSpan timeout) =>
        this.producer.CommitTransaction(timeout);

    public void CommitTransaction() =>
        this.producer.CommitTransaction();

    public void AbortTransaction(TimeSpan timeout) =>
        this.producer.AbortTransaction(timeout);

    public void AbortTransaction() =>
        this.producer.AbortTransaction();

    public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout) =>
        this.producer.SendOffsetsToTransaction(offsets, groupMetadata, timeout);

    public void Dispose() =>
        this.producer.Dispose();

    private static string FormatProduceException(ProduceException<TKey, TValue> produceException) =>
        produceException.Error.Code.ToString();

    private static string FormatArgumentException(ArgumentException argumentException) =>
        argumentException.GetType().FullName!;

    private static void GetTags(string topic, out TagList tags, int? partition = null, string? errorType = null)
    {
        tags = new TagList()
        {
            new KeyValuePair<string, object?>(
                SemanticConventions.AttributeMessagingOperationName,
                ConfluentKafkaCommon.SendOperationName),
            new KeyValuePair<string, object?>(
                SemanticConventions.AttributeMessagingOperationType,
                ConfluentKafkaCommon.SendOperationType),
            new KeyValuePair<string, object?>(
                SemanticConventions.AttributeMessagingSystem,
                ConfluentKafkaCommon.KafkaMessagingSystem),
            new KeyValuePair<string, object?>(
                SemanticConventions.AttributeMessagingDestinationName,
                topic),
        };

        if (partition is not null)
        {
            tags.Add(
                new KeyValuePair<string, object?>(
                    SemanticConventions.AttributeMessagingDestinationPartitionId,
                    partition.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (errorType is not null)
        {
            tags.Add(
                new KeyValuePair<string, object?>(
                    SemanticConventions.AttributeErrorType,
                    errorType));
        }
    }

    private static void RecordPublish(string topic, TimeSpan duration, string? errorType = null)
    {
        GetTags(topic, out var tags, partition: null, errorType);

        ConfluentKafkaCommon.SentMessagesCounter.Add(1, in tags);
        ConfluentKafkaCommon.OperationDurationHistogram.Record(duration.TotalSeconds, in tags);
    }

    private static void RecordPublish(TopicPartition topicPartition, TimeSpan duration, string? errorType = null)
    {
        GetTags(topicPartition.Topic, out var tags, partition: topicPartition.Partition, errorType);

        ConfluentKafkaCommon.SentMessagesCounter.Add(1, in tags);
        ConfluentKafkaCommon.OperationDurationHistogram.Record(duration.TotalSeconds, in tags);
    }

    private Activity? StartPublishActivity(DateTimeOffset start, string topic, Message<TKey, TValue> message, int? partition = null)
    {
        if (!this.options.Traces)
        {
            return null;
        }

        var spanName = string.Concat(ConfluentKafkaCommon.SendOperationName, " ", topic);

        // Provide the attributes that can influence sampling decisions at span creation time
        var initialTags = new ActivityTagsCollection
        {
            [SemanticConventions.AttributeMessagingDestinationName] = topic,
            [SemanticConventions.AttributeMessagingOperationName] = ConfluentKafkaCommon.SendOperationName,
            [SemanticConventions.AttributeMessagingOperationType] = ConfluentKafkaCommon.SendOperationType,
            [SemanticConventions.AttributeMessagingSystem] = ConfluentKafkaCommon.KafkaMessagingSystem,
        };

        if (partition is { } partitionValue)
        {
            initialTags.Add(SemanticConventions.AttributeMessagingDestinationPartitionId, partitionValue.ToString(CultureInfo.InvariantCulture));
        }

        var activity = ConfluentKafkaCommon.ActivitySource.StartActivity(
            spanName,
            kind: ActivityKind.Producer,
            parentContext: default,
            tags: initialTags,
            links: null,
            startTime: start);

        if (activity == null)
        {
            return null;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag(SemanticConventions.AttributeMessagingClientId, this.Name);

            if (ConfluentKafkaCommon.FormatMessageKey(message.Key) is { } messageKey)
            {
                activity.SetTag(SemanticConventions.AttributeMessagingKafkaMessageKey, messageKey);
            }

            if (message.Value is null)
            {
                activity.SetTag(SemanticConventions.AttributeMessagingKafkaMessageTombstone, true);
            }
        }

        return activity;
    }

    private void InjectActivity(Activity? activity, Message<TKey, TValue> message) =>
        this.propagator.Inject(new PropagationContext(activity?.Context ?? default, Baggage.Current), message, this.InjectTraceContext);

    private void InjectTraceContext(Message<TKey, TValue> message, string key, string value)
    {
        message.Headers ??= [];
        message.Headers.Add(key, Encoding.UTF8.GetBytes(value));
    }
}
