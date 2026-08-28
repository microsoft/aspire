// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Confluent.Kafka;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.ConfluentKafka;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;

public class OtelInstrumentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumeExceptionRecordsErrorTelemetry(bool hasConsumerRecord)
    {
        var activities = new List<Activity>();
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
            .AddInMemoryExporter(activities)
            .Build();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.Meter.Name)
            .AddInMemoryExporter(metrics)
            .Build();
        var consumer = CreateInstrumentedConsumer<string, string>(
            new FakeKafkaConsumer<string, string> { ExceptionToThrow = exception },
            traces: true,
            metrics: true);

        Assert.Throws<ConsumeException>(() => consumer.Consume(CancellationToken.None));
        tracerProvider.ForceFlush();
        meterProvider.EnsureMetricsAreFlushed();

        var activity = Assert.Single(activities, activity => Equals(GetTagValue(activity, "error.type"), error.Code.ToString()));
        Assert.Equal(hasConsumerRecord ? "poll error-topic" : "poll", activity.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(error.Code.ToString(), GetTagValue(activity, "error.type"));

        var durationMetric = Assert.Single(metrics, metric => metric.Name == "messaging.client.operation.duration");
        var durationPoint = Assert.IsType<MetricPoint>(GetMetricPointWithTag(durationMetric, "error.type", error.Code.ToString()));
        Assert.Equal(1, durationPoint.GetHistogramCount());
        Assert.True(durationPoint.GetHistogramSum() >= 0);
        Assert.Equal(error.Code.ToString(), GetTagValue(durationPoint, "error.type"));

        var consumedMetric = metrics.SingleOrDefault(metric => metric.Name == "messaging.client.consumed.messages");
        var consumedPoint = consumedMetric is null
            ? null
            : GetMetricPointWithTag(consumedMetric, "error.type", error.Code.ToString());
        if (hasConsumerRecord)
        {
            var recordedPoint = Assert.IsType<MetricPoint>(consumedPoint);
            Assert.Equal(1, recordedPoint.GetSumLong());
            Assert.Equal(error.Code.ToString(), GetTagValue(recordedPoint, "error.type"));
        }
        else
        {
            Assert.Null(consumedPoint);
        }
    }

    [Fact]
    public async Task ConsumeAndProcessMessageAsyncPropagatesHandlerExceptionAndRecordsError()
    {
        var activities = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddInMemoryExporter(activities)
            .Build();
        var consumer = CreateInstrumentedConsumer(
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
        var processActivity = Assert.Single(activities, activity => activity.DisplayName == "process process-error");
        Assert.Equal(ActivityStatusCode.Error, processActivity.Status);
        Assert.Equal("processing failed", processActivity.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, GetTagValue(processActivity, "error.type"));
    }

    [Fact]
    public async Task MessageKeyUsesInvariantNumericAndDateFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            var numericActivity = await CaptureProcessActivityAsync(1234.5m);
            Assert.Equal("1234.5", GetTagValue(numericActivity, "messaging.kafka.message.key"));

            var dateActivity = await CaptureProcessActivityAsync(
                new DateTime(2026, 7, 7, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234));
            Assert.Equal("2026-07-07T12:34:56.7891234Z", GetTagValue(dateActivity, "messaging.kafka.message.key"));
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
        var activities = new List<Activity>();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(OpenTelemetry.Instrumentation.ConfluentKafka.ConfluentKafkaCommon.ActivitySource.Name)
            .AddInMemoryExporter(activities)
            .Build();
        var consumer = CreateInstrumentedConsumer(
            new FakeKafkaConsumer<object, string>
            {
                ConsumeResult = CreateConsumeResult("key-topic", key),
            },
            traces: true,
            metrics: false);

        await consumer.ConsumeAndProcessMessageAsync((_, _, _) => ValueTask.CompletedTask);
        tracerProvider.ForceFlush();

        return Assert.Single(activities, activity => activity.DisplayName == "process key-topic");
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

    private static MetricPoint? GetMetricPointWithTag(Metric metric, string tagName, string tagValue)
    {
        MetricPoint? result = null;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            if (Equals(GetTagValue(point, tagName), tagValue))
            {
                Assert.Null(result);
                result = point;
            }
        }

        return result;
    }

    private static object? GetTagValue(MetricPoint metricPoint, string name)
    {
        foreach (var tag in metricPoint.Tags)
        {
            if (tag.Key == name)
            {
                return tag.Value;
            }
        }

        return null;
    }

    private static object? GetTagValue(Activity activity, string name) =>
        activity.TagObjects.SingleOrDefault(tag => tag.Key == name).Value;
}
