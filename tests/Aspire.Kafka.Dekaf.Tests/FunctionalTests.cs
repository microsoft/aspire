// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Confluent.Kafka.Tests;
using Aspire.TestUtilities;
using Dekaf;
using Dekaf.Admin;
using Dekaf.Consumer;
using Dekaf.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

[Collection("Kafka Broker collection")]
public class FunctionalTests(KafkaContainerFixture fixture)
{
    [Theory]
    [RequiresFeature(TestFeature.Testcontainers)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RoundTripWithCustomSerializersHealthChecksAndTelemetry(bool keyed, bool disableTelemetry)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var topic = $"aspire-dekaf-{Guid.NewGuid():N}";
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = fixture.Container!.GetBootstrapAddress();
        builder.Services.AddSingleton<MessageSerializer>();
        builder.Logging.SetMinimumLevel(LogLevel.Debug).AddFakeLogging();
        List<Activity> activities = [];
        List<Metric> metrics = [];
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(activities))
            .WithMetrics(meter => meter.AddInMemoryExporter(metrics));

        void ConfigureProducer(IServiceProvider services, ProducerBuilder<string, TestMessage> producer)
            => producer.WithValueSerializer(services.GetRequiredService<MessageSerializer>());
        void ConfigureConsumer(IServiceProvider services, ConsumerBuilder<string, TestMessage> consumer)
            => consumer.WithValueDeserializer(services.GetRequiredService<MessageSerializer>())
                .WithGroupId(topic)
                .WithAutoOffsetReset(AutoOffsetReset.Earliest)
                .SubscribeTo(topic);
        void ProducerSettings(KafkaProducerSettings settings)
        {
            settings.DisableTracing = disableTelemetry;
            settings.DisableMetrics = disableTelemetry;
        }
        void ConsumerSettings(KafkaConsumerSettings settings)
        {
            settings.DisableTracing = disableTelemetry;
            settings.DisableMetrics = disableTelemetry;
        }
        if (keyed)
        {
            builder.AddKeyedDekafKafkaProducer<string, TestMessage>("messaging", ProducerSettings, ConfigureProducer);
            builder.AddKeyedDekafKafkaConsumer<string, TestMessage>("messaging", ConsumerSettings, ConfigureConsumer);
            builder.AddKeyedDekafKafkaAdminClient("messaging", settings =>
            {
                settings.DisableTracing = disableTelemetry;
                settings.DisableMetrics = disableTelemetry;
            });
        }
        else
        {
            builder.AddDekafKafkaProducer<string, TestMessage>("messaging", ProducerSettings, ConfigureProducer);
            builder.AddDekafKafkaConsumer<string, TestMessage>("messaging", ConsumerSettings, ConfigureConsumer);
            builder.AddDekafKafkaAdminClient("messaging", settings =>
            {
                settings.DisableTracing = disableTelemetry;
                settings.DisableMetrics = disableTelemetry;
            });
        }

        using var host = builder.Build();
        var admin = keyed ? host.Services.GetRequiredKeyedService<IAdminClient>("messaging") : host.Services.GetRequiredService<IAdminClient>();
        await admin.CreateTopicsAsync([new NewTopic { Name = topic }], cancellationToken: timeout.Token);
        await host.StartAsync(timeout.Token);
        try
        {
            var producer = keyed
                ? host.Services.GetRequiredKeyedService<IKafkaProducer<string, TestMessage>>("messaging")
                : host.Services.GetRequiredService<IKafkaProducer<string, TestMessage>>();
            var consumer = keyed
                ? host.Services.GetRequiredKeyedService<IKafkaConsumer<string, TestMessage>>("messaging")
                : host.Services.GetRequiredService<IKafkaConsumer<string, TestMessage>>();
            var message = new TestMessage("hello from Aspire");
            var sent = await producer.ProduceAsync(topic, "key", message, timeout.Token);
            var received = await consumer.ConsumeOneAsync(TimeSpan.FromSeconds(30), timeout.Token);
            Assert.NotNull(received);
            Assert.Equal("key", received.Value.Key);
            Assert.Equal(message, received.Value.Value);
            Assert.Equal(sent.Offset, received.Value.Offset);

            // Dekaf accounts consumed messages when the fetched batch is drained. The next poll
            // completes that batch and also verifies that the record is not delivered twice.
            Assert.Null(await consumer.ConsumeOneAsync(TimeSpan.FromMilliseconds(100), timeout.Token));

            var serializer = host.Services.GetRequiredService<MessageSerializer>();
            Assert.Equal(1, serializer.SerializedCount);
            Assert.Equal(1, serializer.DeserializedCount);
            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(timeout.Token);
            Assert.Equal(3, report.Entries.Count);
            Assert.All(report.Entries, entry => Assert.Equal(HealthStatus.Healthy, entry.Value.Status));

            host.Services.GetRequiredService<TracerProvider>().ForceFlush();
            host.Services.GetRequiredService<MeterProvider>().ForceFlush();
            var sendActivities = activities.Where(activity => activity.GetTagItem("messaging.destination.name") as string == topic && activity.Kind == ActivityKind.Producer).ToArray();
            var receiveActivities = activities.Where(activity => activity.GetTagItem("messaging.destination.name") as string == topic && activity.Kind == ActivityKind.Client).ToArray();
            if (disableTelemetry)
            {
                Assert.Empty(sendActivities);
                Assert.Empty(receiveActivities);
                Assert.Empty(metrics);
            }
            else
            {
                var send = Assert.Single(sendActivities);
                var receive = Assert.Single(receiveActivities);
                var linkedContext = Assert.Single(receive.Links).Context;
                Assert.Equal(send.TraceId, linkedContext.TraceId);
                Assert.Equal(send.SpanId, linkedContext.SpanId);
                Assert.True(linkedContext.IsRemote);
                Assert.Contains(metrics, metric => metric.Name == "messaging.client.sent.messages");
                Assert.Contains(metrics, metric => metric.Name == "messaging.client.consumed.messages");
            }

            var logs = host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot();
            Assert.Contains(logs, log => log.Category?.StartsWith("Dekaf.", StringComparison.Ordinal) is true);
        }
        finally
        {
            await host.StopAsync(timeout.Token);
            await admin.DeleteTopicsAsync([topic], cancellationToken: timeout.Token);
        }
    }
}
