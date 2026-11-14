// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.ConfluentKafka;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;
using KafkaInstrumentationCommon = OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon;
using static Aspire.Confluent.Kafka.Tests.MetricTestHelpers;

namespace Aspire.Confluent.Kafka.Tests;

public class OtelInstrumentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientConstructionDefersClusterDiscovery(bool tracingEnabled)
    {
        var innerProducer = new FakeKafkaProducer<string, string> { ExceptionToThrow = new ArgumentException("serialization error") };
        var innerConsumer = new FakeKafkaConsumer<string, string>();
        using var producer = new InstrumentedProducer<string, string>(
            innerProducer,
            new ConfluentKafkaProducerInstrumentationOptions<string, string> { Traces = tracingEnabled });
        using var consumer = new InstrumentedConsumer<string, string>(
            innerConsumer,
            new ConfluentKafkaConsumerInstrumentationOptions<string, string> { Traces = tracingEnabled });

        Assert.Equal(0, innerProducer.HandleReadCount);
        Assert.Equal(0, innerConsumer.HandleReadCount);
    }

    [Fact]
    public async Task FailedClusterIdLookupAllowsRetry()
    {
        var bootstrapServers = $"cache-test-{Guid.NewGuid()}";
        Assert.True(KafkaInstrumentationCommon.ClusterIdCache.TryAdd(bootstrapServers, Task.FromResult<string?>(null)));

        try
        {
            Assert.Null(await KafkaInstrumentationCommon.GetOrFetchClusterIdAsync(null!, bootstrapServers));
            Assert.True(KafkaInstrumentationCommon.ClusterIdCache.TryAdd(bootstrapServers, Task.FromResult<string?>("retried-cluster")));
            Assert.Equal("retried-cluster", await KafkaInstrumentationCommon.GetOrFetchClusterIdAsync(null!, bootstrapServers));
        }
        finally
        {
            KafkaInstrumentationCommon.ClusterIdCache.TryRemove(bootstrapServers, out _);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedClusterIdLookupPreservesReplacement(bool replacementCompleted)
    {
        var bootstrapServers = $"cache-test-{Guid.NewGuid()}";
        var failedDiscovery = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementDiscovery = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(KafkaInstrumentationCommon.ClusterIdCache.TryAdd(bootstrapServers, failedDiscovery.Task));

        try
        {
            var lookup = KafkaInstrumentationCommon.GetOrFetchClusterIdAsync(null!, bootstrapServers);
            if (replacementCompleted)
            {
                replacementDiscovery.SetResult("replacement-cluster");
            }

            // Recreate a newer cache entry being installed before a stale waiter resumes.
            Assert.True(KafkaInstrumentationCommon.ClusterIdCache.TryUpdate(bootstrapServers, replacementDiscovery.Task, failedDiscovery.Task));
            failedDiscovery.SetResult(null);

            Assert.Null(await lookup.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(KafkaInstrumentationCommon.ClusterIdCache.TryGetValue(bootstrapServers, out var cachedDiscovery));
            Assert.Same(replacementDiscovery.Task, cachedDiscovery);

            if (!replacementCompleted)
            {
                replacementDiscovery.SetResult("replacement-cluster");
            }

            Assert.Equal("replacement-cluster", await KafkaInstrumentationCommon.GetOrFetchClusterIdAsync(null!, bootstrapServers));
        }
        finally
        {
            failedDiscovery.TrySetResult(null);
            replacementDiscovery.TrySetResult("replacement-cluster");
            KafkaInstrumentationCommon.ClusterIdCache.TryRemove(bootstrapServers, out _);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProducerClusterDiscoveryStartsWhenTracingIsEnabled(bool asyncProduce, bool usePartition)
    {
        var activityExporter = new TestActivityExporter();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(KafkaInstrumentationCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        var topic = $"lazy-producer-{Guid.NewGuid()}";
        var exception = new ArgumentException("serialization error");
        var innerProducer = new FakeKafkaProducer<string, string> { ExceptionToThrow = exception };
        var options = new ConfluentKafkaProducerInstrumentationOptions<string, string> { Traces = false, Metrics = true };
        using var producer = new InstrumentedProducer<string, string>(innerProducer, options);
        var message = new Message<string, string> { Key = "key", Value = "value" };
        var partition = new TopicPartition(topic, new Partition(0));
        Assert.Equal(0, innerProducer.HandleReadCount);

        await ProduceAsync();
        Assert.Equal(0, innerProducer.HandleReadCount);
        var disabledActivities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic))
            .ToArray();
        Assert.Empty(disabledActivities);

        options.Traces = true;
        await ProduceAsync();
        await ProduceAsync();
        Assert.Equal(1, innerProducer.HandleReadCount);
        Assert.Equal(2, activityExporter.GetActivities().Count(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic)));

        async Task ProduceAsync()
        {
            var actual = asyncProduce
                ? await Record.ExceptionAsync(() => usePartition ? producer.ProduceAsync(partition, message) : producer.ProduceAsync(topic, message))
                : Record.Exception(() =>
                {
                    if (usePartition)
                    {
                        producer.Produce(partition, message);
                    }
                    else
                    {
                        producer.Produce(topic, message);
                    }
                });
            Assert.Same(exception, actual);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumerClusterDiscoveryStartsWhenTracingIsEnabled(bool processMessages)
    {
        var activityExporter = new TestActivityExporter();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(KafkaInstrumentationCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        var topic = $"lazy-consumer-{Guid.NewGuid()}";
        var result = CreateConsumeResult(topic, "key");
        var innerConsumer = new FakeKafkaConsumer<string, string> { ConsumeResult = result };
        var options = new ConfluentKafkaConsumerInstrumentationOptions<string, string> { Traces = false, Metrics = true };
        using var consumer = new InstrumentedConsumer<string, string>(innerConsumer, options) { GroupId = $"lazy-group-{Guid.NewGuid()}" };
        Assert.Equal(0, innerConsumer.HandleReadCount);

        await ConsumeAsync();
        Assert.Equal(0, innerConsumer.HandleReadCount);
        var disabledActivities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic))
            .ToArray();
        Assert.Empty(disabledActivities);

        options.Traces = true;
        await ConsumeAsync();
        Assert.Equal(1, innerConsumer.HandleReadCount);
        Assert.Equal(processMessages ? 2 : 3, activityExporter.GetActivities().Count(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic)));

        async Task ConsumeAsync()
        {
            if (processMessages)
            {
                Assert.Same(result, await consumer.ConsumeAndProcessMessageAsync((_, activity, _) =>
                {
                    Assert.Equal(options.Traces, activity is not null);
                    return ValueTask.CompletedTask;
                }));
            }
            else
            {
                Assert.Same(result, consumer.Consume(0));
                Assert.Same(result, consumer.Consume(TimeSpan.Zero));
                Assert.Same(result, consumer.Consume(CancellationToken.None));
            }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void EmptyPollsRecordDurationWithoutCountingMessages(bool isPartitionEof, bool metricsEnabled)
    {
        var activityExporter = new TestActivityExporter();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        using var durationCollector = new MetricCollector<double>(
            OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.OperationDurationHistogram);
        using var consumedCollector = new MetricCollector<long>(
            OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ConsumedMessagesCounter);
        var result = isPartitionEof
            ? new ConsumeResult<string, string>
            {
                Topic = $"empty-topic-{Guid.NewGuid()}",
                Partition = new Partition(2),
                Offset = new Offset(100),
                IsPartitionEOF = true,
            }
            : null;
        using var consumer = CreateInstrumentedConsumer(
            new FakeKafkaConsumer<string, string> { ConsumeResult = result },
            traces: true,
            metrics: metricsEnabled);
        var groupId = $"empty-group-{Guid.NewGuid()}";
        consumer.GroupId = groupId;

        Assert.Same(result, consumer.Consume(0));
        Assert.Same(result, consumer.Consume(TimeSpan.Zero));
        Assert.Same(result, consumer.Consume(CancellationToken.None));

        // Producer measurements share this histogram but have no consumer-group tag.
        OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.OperationDurationHistogram.Record(
            0,
            new TagList
            {
                { "messaging.operation.name", "send" },
                { "messaging.operation.type", "send" },
                { "messaging.system", "kafka" },
                { "messaging.destination.name", $"other-topic-{Guid.NewGuid()}" },
            });

        var durations = durationCollector.GetMeasurementSnapshot()
            .Where(measurement => measurement.Tags.TryGetValue("messaging.consumer.group.name", out var value)
                && Equals(value, groupId))
            .ToArray();
        Assert.Equal(metricsEnabled ? 3 : 0, durations.Length);
        Assert.All(durations, measurement =>
        {
            Assert.True(measurement.Value >= 0);
            Assert.Equal("poll", measurement.Tags["messaging.operation.name"]);
            Assert.Equal("receive", measurement.Tags["messaging.operation.type"]);
            Assert.Equal("kafka", measurement.Tags["messaging.system"]);
            if (isPartitionEof)
            {
                Assert.Equal(result!.Topic, measurement.Tags["messaging.destination.name"]);
                Assert.Equal("2", measurement.Tags["messaging.destination.partition.id"]);
            }
        });
        var consumedMeasurements = consumedCollector.GetMeasurementSnapshot()
            .Where(measurement => measurement.Tags.TryGetValue("messaging.consumer.group.name", out var value)
                && Equals(value, groupId))
            .ToArray();
        Assert.Empty(consumedMeasurements);
        var activities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("messaging.consumer.group.name"), groupId))
            .ToArray();
        Assert.Empty(activities);
    }

    [Fact]
    public void CanceledPollsAreNotRecordedAsSuccessfulEmptyPolls()
    {
        using var durationCollector = new MetricCollector<double>(
            OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.OperationDurationHistogram);
        using var consumer = CreateInstrumentedConsumer(
            new FakeKafkaConsumer<string, string> { ExceptionToThrow = new OperationCanceledException() },
            traces: false,
            metrics: true);
        var groupId = $"canceled-group-{Guid.NewGuid()}";
        consumer.GroupId = groupId;

        Assert.Throws<OperationCanceledException>(() => consumer.Consume(0));
        Assert.Throws<OperationCanceledException>(() => consumer.Consume(TimeSpan.Zero));
        Assert.Throws<OperationCanceledException>(() => consumer.Consume(CancellationToken.None));

        var durations = durationCollector.GetMeasurementSnapshot()
            .Where(measurement => measurement.Tags.TryGetValue("messaging.consumer.group.name", out var value)
                && Equals(value, groupId))
            .ToArray();
        Assert.Empty(durations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumeExceptionRecordsErrorTelemetry(bool hasConsumerRecord)
    {
        var activityExporter = new TestActivityExporter();
        var metrics = new List<Metric>();
        var error = new Error(ErrorCode.Local_ValueDeserialization, "Deserialization error");
        var consumerRecord = hasConsumerRecord
            ? new ConsumeResult<byte[], byte[]>
            {
                Topic = "error-topic",
                Partition = new Partition(2),
                Offset = new Offset(100),
                Message = null,
            }
            : null;
        var exception = new ConsumeException(consumerRecord!, error);

        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.Meter.Name)
            .AddInMemoryExporter(metrics)
            .Build();
        using var consumer = CreateInstrumentedConsumer<string, string>(
            new FakeKafkaConsumer<string, string> { ExceptionToThrow = exception },
            traces: true,
            metrics: true);

        Assert.Throws<ConsumeException>(() => consumer.Consume(0));
        Assert.Throws<ConsumeException>(() => consumer.Consume(TimeSpan.Zero));
        Assert.Throws<ConsumeException>(() => consumer.Consume(CancellationToken.None));
        tracerProvider.ForceFlush();
        meterProvider.EnsureMetricsAreFlushed();

        var activities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("error.type"), error.Code.ToString()))
            .ToArray();
        Assert.Equal(3, activities.Length);
        Assert.All(activities, activity =>
        {
            Assert.Equal(hasConsumerRecord ? "poll error-topic" : "poll", activity.DisplayName);
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Null(activity.StatusDescription);
            Assert.Equal(error.Code.ToString(), activity.GetTagItem("error.type"));
        });

        var durationMetric = Assert.Single(metrics, metric => metric.Name == "messaging.client.operation.duration");
        var durationPoint = Assert.IsType<MetricPoint>(GetMetricPointWithTag(durationMetric, "error.type", error.Code.ToString()));
        Assert.Equal(3, durationPoint.GetHistogramCount());
        Assert.True(durationPoint.GetHistogramSum() >= 0);
        Assert.Equal(error.Code.ToString(), GetTagValue(durationPoint, "error.type"));

        var consumedMetric = metrics.SingleOrDefault(metric => metric.Name == "messaging.client.consumed.messages");
        var consumedPoint = consumedMetric is null
            ? null
            : GetMetricPointWithTag(consumedMetric, "error.type", error.Code.ToString());
        if (hasConsumerRecord)
        {
            var recordedPoint = Assert.IsType<MetricPoint>(consumedPoint);
            Assert.Equal(3, recordedPoint.GetSumLong());
            Assert.Equal(error.Code.ToString(), GetTagValue(recordedPoint, "error.type"));
        }
        else
        {
            Assert.Null(consumedPoint);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProduceExceptionRecordsErrorTelemetryWithoutStatusDescription(bool isKafkaError)
    {
        var activityExporter = new TestActivityExporter();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        var topic = $"produce-error-{Guid.NewGuid()}";
        var error = new Error(ErrorCode.Local_ValueSerialization, "sensitive message");
        Exception exception = isKafkaError
            ? new ProduceException<string, string>(error, new DeliveryResult<string, string> { Topic = topic })
            : new ArgumentException("sensitive message");
        using var producer = new InstrumentedProducer<string, string>(
            new FakeKafkaProducer<string, string> { ExceptionToThrow = exception },
            new ConfluentKafkaProducerInstrumentationOptions<string, string> { Traces = true, Metrics = false });
        var message = new Message<string, string> { Key = "key", Value = "value" };
        var partition = new TopicPartition(topic, new Partition(0));

        Assert.Same(exception, await Record.ExceptionAsync(() => producer.ProduceAsync(topic, message)));
        Assert.Same(exception, await Record.ExceptionAsync(() => producer.ProduceAsync(partition, message)));
        Assert.Same(exception, Record.Exception(() => producer.Produce(topic, message)));
        Assert.Same(exception, Record.Exception(() => producer.Produce(partition, message)));
        tracerProvider.ForceFlush();

        var activities = activityExporter.GetActivities()
            .Where(activity => Equals(activity.GetTagItem("messaging.destination.name"), topic))
            .ToArray();
        Assert.Equal(4, activities.Length);
        Assert.All(activities, activity =>
        {
            Assert.Equal($"send {topic}", activity.DisplayName);
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Null(activity.StatusDescription);
            Assert.Equal(isKafkaError ? error.Code.ToString() : exception.GetType().FullName, activity.GetTagItem("error.type"));
        });
    }

    [Fact]
    public async Task ConsumeAndProcessMessageAsyncPropagatesHandlerExceptionAndRecordsError()
    {
        var activityExporter = new TestActivityExporter();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        using var consumer = CreateInstrumentedConsumer(
            new FakeKafkaConsumer<string, string>
            {
                ConsumeResult = CreateConsumeResult("process-error", "key"),
            },
            traces: true,
            metrics: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.ConsumeAndProcessMessageAsync(
                (_, _, _) => throw new InvalidOperationException("processing failed")).AsTask());
        tracerProvider.ForceFlush();

        Assert.Equal("processing failed", exception.Message);
        var processActivity = Assert.Single(activityExporter.GetActivities(), activity => activity.DisplayName == "process process-error");
        Assert.Equal(ActivityStatusCode.Error, processActivity.Status);
        Assert.Null(processActivity.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, processActivity.GetTagItem("error.type"));
    }

    [Fact]
    public async Task MessageKeyUsesInvariantNumericAndDateFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            var numericActivity = await CaptureProcessActivityAsync(1234.5m);
            Assert.Equal("1234.5", numericActivity.GetTagItem("messaging.kafka.message.key"));

            var dateActivity = await CaptureProcessActivityAsync(
                new DateTime(2026, 7, 7, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234));
            Assert.Equal("2026-07-07T12:34:56.7891234Z", dateActivity.GetTagItem("messaging.kafka.message.key"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task UnsupportedMessageKeyIsOmitted()
    {
        var activity = await CaptureProcessActivityAsync(new byte[] { 1, 2, 3 });

        Assert.Null(activity.TagObjects.FirstOrDefault(tag => tag.Key == "messaging.kafka.message.key").Key);
    }

    private static async Task<Activity> CaptureProcessActivityAsync(object key)
    {
        var activityExporter = new TestActivityExporter();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddProcessor(new SimpleActivityExportProcessor(activityExporter))
            .Build();
        using var consumer = CreateInstrumentedConsumer(
            new FakeKafkaConsumer<object, string>
            {
                ConsumeResult = CreateConsumeResult("key-topic", key),
            },
            traces: true,
            metrics: false);

        await consumer.ConsumeAndProcessMessageAsync((_, _, _) => ValueTask.CompletedTask);
        tracerProvider.ForceFlush();

        return Assert.Single(activityExporter.GetActivities(), activity => activity.DisplayName == "process key-topic");
    }

    private static ConsumeResult<TKey, string> CreateConsumeResult<TKey>(string topic, TKey key) =>
        new()
        {
            Topic = topic,
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<TKey, string> { Key = key, Value = "value" },
        };

    private static InstrumentedConsumer<TKey, TValue> CreateInstrumentedConsumer<TKey, TValue>(
        FakeKafkaConsumer<TKey, TValue> consumer,
        bool traces,
        bool metrics) =>
        new(consumer, new ConfluentKafkaConsumerInstrumentationOptions<TKey, TValue>
        {
            Traces = traces,
            Metrics = metrics,
        })
        {
            GroupId = "test-group",
        };
}
