// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Instrumentation.ConfluentKafka;
using OpenTelemetry.Trace;

namespace Confluent.Kafka;

/// <summary>
/// <see cref="IConsumer{TKey,TValue}"/> extension methods.
/// </summary>
internal static class OpenTelemetryConsumeResultExtensions
{
    /// <summary>
    /// Attempts to extract a <see cref="PropagationContext"/> from the <see cref="ConsumeResult{TKey,TValue}"/>'s <see cref="Headers"/> property.
    /// </summary>
    /// <param name="consumeResult">The <see cref="ConsumeResult{TKey,TValue}"/>.</param>
    /// <param name="propagationContext">The <see cref="PropagationContext"/>.</param>
    /// <typeparam name="TKey">The type of key of the <see cref="ConsumeResult{TKey,TValue}"/>.</typeparam>
    /// <typeparam name="TValue">The type of value of the <see cref="ConsumeResult{TKey,TValue}"/>.</typeparam>
    /// <returns>True when a <see cref="PropagationContext"/> has been extracted from <see cref="Headers"/>, otherwise false.</returns>
    public static bool TryExtractPropagationContext<TKey, TValue>(
        this ConsumeResult<TKey, TValue> consumeResult,
        out PropagationContext propagationContext)
    {
#if NET
        ArgumentNullException.ThrowIfNull(consumeResult);
#else
        if (consumeResult == null)
        {
            throw new ArgumentNullException(nameof(consumeResult));
        }
#endif

        try
        {
            propagationContext = ExtractPropagationContext(consumeResult.Message?.Headers);
            return true;
        }
        catch
        {
            propagationContext = default;
            return false;
        }
    }

    /// <summary>
    /// Consumes a message and creates <see href="https://github.com/open-telemetry/semantic-conventions/blob/main/docs/messaging/messaging-spans.md#span-kind">a process span</see> embracing the <see cref="OpenTelemetryConsumeAndProcessMessageHandler{TKey,TValue}"/>.
    /// </summary>
    /// <param name="consumer">The <see cref="IConsumer{TKey,TValue}"/>.</param>
    /// <param name="handler">A <see cref="OpenTelemetryConsumeAndProcessMessageHandler{TKey,TValue}"/>.</param>
    /// <typeparam name="TKey">The type of key of the <see cref="ConsumeResult{TKey,TValue}"/>.</typeparam>
    /// <typeparam name="TValue">The type of value of the <see cref="ConsumeResult{TKey,TValue}"/>.</typeparam>
    /// <returns>A <see cref="ValueTask"/>.</returns>
    public static ValueTask<ConsumeResult<TKey, TValue>?> ConsumeAndProcessMessageAsync<TKey, TValue>(
        this IConsumer<TKey, TValue> consumer,
        OpenTelemetryConsumeAndProcessMessageHandler<TKey, TValue> handler) =>
        ConsumeAndProcessMessageAsync(consumer, handler, default);

    /// <summary>
    /// Consumes a message and creates <see href="https://github.com/open-telemetry/semantic-conventions/blob/main/docs/messaging/messaging-spans.md#span-kind">a process span</see> embracing the <see cref="OpenTelemetryConsumeAndProcessMessageHandler{TKey,TValue}"/>.
    /// </summary>
    /// <param name="consumer">The <see cref="IConsumer{TKey,TValue}"/>.</param>
    /// <param name="handler">A <see cref="OpenTelemetryConsumeAndProcessMessageHandler{TKey,TValue}"/>.</param>
    /// <param name="cancellationToken">An optional <see cref="CancellationToken"/>.</param>
    /// <typeparam name="TKey">The type of key of the <see cref="ConsumeResult{TKey,TValue}"/>.</typeparam>
    /// <typeparam name="TValue">The type of value of the <see cref="ConsumeResult{TKey,TValue}"/>.</typeparam>
    /// <returns>A <see cref="ValueTask"/>.</returns>
    public static async ValueTask<ConsumeResult<TKey, TValue>?> ConsumeAndProcessMessageAsync<TKey, TValue>(
        this IConsumer<TKey, TValue> consumer,
        OpenTelemetryConsumeAndProcessMessageHandler<TKey, TValue> handler,
        CancellationToken cancellationToken)
    {
#if NET
        ArgumentNullException.ThrowIfNull(consumer);
#else
        if (consumer == null)
        {
            throw new ArgumentNullException(nameof(consumer));
        }
#endif

        if (consumer is not InstrumentedConsumer<TKey, TValue> instrumentedConsumer)
        {
            throw new ArgumentException("Invalid consumer type.", nameof(consumer));
        }

#if NET
        ArgumentNullException.ThrowIfNull(handler);
#else
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }
#endif

        var consumeResult = instrumentedConsumer.Consume(cancellationToken);

        if (consumeResult?.Message == null || consumeResult.IsPartitionEOF)
        {
            return consumeResult;
        }

        var processActivity = StartProcessActivity(TryExtractPropagationContext(consumeResult, out var propagationContext) ? propagationContext : default, consumeResult.TopicPartitionOffset, consumeResult.Message.Key, consumeResult.Message.Value is null, instrumentedConsumer.Name, instrumentedConsumer.GroupId!);

        try
        {
            await handler(consumeResult, processActivity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            processActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            processActivity?.SetTag(SemanticConventions.AttributeErrorType, ex.GetType().FullName);
            throw;
        }
        finally
        {
            processActivity?.Dispose();
        }

        return consumeResult;
    }

    internal static PropagationContext ExtractPropagationContext(Headers? headers)
        => Propagators.DefaultTextMapPropagator.Extract(default, headers, ExtractTraceContext);

    private static Activity? StartProcessActivity<TKey>(PropagationContext propagationContext, TopicPartitionOffset? topicPartitionOffset, TKey? key, bool isTombstone, string clientId, string groupId)
    {
#pragma warning disable IDE0370 // Suppression is unnecessary
        var spanName = string.IsNullOrEmpty(topicPartitionOffset?.Topic)
            ? ConfluentKafkaCommon.ProcessOperationName
            : string.Concat(ConfluentKafkaCommon.ProcessOperationName, " ", topicPartitionOffset!.Topic);
#pragma warning restore IDE0370 // Suppression is unnecessary

        ActivityLink[] activityLinks = propagationContext != default && propagationContext.ActivityContext.IsValid()
            ? [new ActivityLink(propagationContext.ActivityContext)]
            : [];

        // Provide the attributes that can influence sampling decisions at span creation time
        var initialTags = new ActivityTagsCollection
        {
            [SemanticConventions.AttributeMessagingOperationName] = ConfluentKafkaCommon.ProcessOperationName,
            [SemanticConventions.AttributeMessagingOperationType] = ConfluentKafkaCommon.ProcessOperationType,
            [SemanticConventions.AttributeMessagingSystem] = ConfluentKafkaCommon.KafkaMessagingSystem,
        };

        if (groupId is { Length: > 0 })
        {
            initialTags.Add(SemanticConventions.AttributeMessagingConsumerGroupName, groupId);
        }

        // messaging.destination.name is only set for actual topics; it must be omitted when unknown.
        if (topicPartitionOffset?.Topic is { Length: > 0 } topic)
        {
            initialTags.Add(SemanticConventions.AttributeMessagingDestinationName, topic);

            if (topicPartitionOffset?.Partition is { } partition)
            {
                initialTags.Add(SemanticConventions.AttributeMessagingDestinationPartitionId, partition.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        var activity = ConfluentKafkaCommon.ActivitySource.StartActivity(
            spanName,
            kind: ActivityKind.Consumer,
            parentContext: default,
            tags: initialTags,
            links: activityLinks);

        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(SemanticConventions.AttributeMessagingClientId, clientId);
            activity.SetTag(SemanticConventions.AttributeMessagingKafkaOffset, topicPartitionOffset?.Offset.Value);

            if (ConfluentKafkaCommon.FormatMessageKey(key) is { } messageKey)
            {
                activity.SetTag(SemanticConventions.AttributeMessagingKafkaMessageKey, messageKey);
            }

            if (isTombstone)
            {
                activity.SetTag(SemanticConventions.AttributeMessagingKafkaMessageTombstone, true);
            }
        }

        return activity;
    }

    private static IEnumerable<string> ExtractTraceContext(Headers? headers, string value)
    {
        if (headers?.TryGetLastBytes(value, out var bytes) == true)
        {
            yield return Encoding.UTF8.GetString(bytes);
        }
    }
}
