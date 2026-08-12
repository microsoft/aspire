// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.ConfluentKafka;

/// <summary>
/// Contains common constants and static members used by the Confluent Kafka instrumentation.
/// </summary>
/// <remarks>
/// Follows the v1.43.0 messaging semantic conventions:
/// https://github.com/open-telemetry/semantic-conventions/tree/v1.43.0/docs/messaging.
/// </remarks>
internal static class ConfluentKafkaCommon
{
    internal const string KafkaMessagingSystem = "kafka";

    // messaging.operation.name values (system-specific operation names).
    internal const string SendOperationName = "send";
    internal const string PollOperationName = "poll";
    internal const string ProcessOperationName = "process";

    // messaging.operation.type values.
    internal const string SendOperationType = "send";
    internal const string ReceiveOperationType = "receive";
    internal const string ProcessOperationType = "process";

    internal static readonly Version SemanticConventionsVersion = new(1, 43, 0);

    internal const string InstrumentationName = "OpenTelemetry.Instrumentation.ConfluentKafka";
    internal static readonly string InstrumentationVersion = new Version(0, 2, 0, 0).ToString();
    internal static readonly string SchemaUrl = $"https://opentelemetry.io/schemas/{SemanticConventionsVersion.ToString(3)}";
    internal static readonly ActivitySource ActivitySource = new(new ActivitySourceOptions(InstrumentationName)
    {
        Version = InstrumentationVersion,
        TelemetrySchemaUrl = SchemaUrl,
    });
    internal static readonly Meter Meter = new(new MeterOptions(InstrumentationName)
    {
        Version = InstrumentationVersion,
        TelemetrySchemaUrl = SchemaUrl,
    });

    internal static readonly Histogram<double> OperationDurationHistogram = Meter.CreateHistogram(
        SemanticConventions.MetricMessagingClientOperationDuration,
        unit: "s",
        description: "Duration of messaging operation initiated by a producer or consumer client.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10] });

    internal static readonly Counter<long> SentMessagesCounter = Meter.CreateCounter<long>(
        SemanticConventions.MetricMessagingClientSentMessages,
        unit: "{message}",
        description: "Number of messages producer attempted to send to the broker.");

    internal static readonly Counter<long> ConsumedMessagesCounter = Meter.CreateCounter<long>(
        SemanticConventions.MetricMessagingClientConsumedMessages,
        unit: "{message}",
        description: "Number of messages that were delivered to the application.");

    /// <summary>
    /// Normalizes a Kafka message key to the string representation required by the
    /// <see href="https://github.com/open-telemetry/semantic-conventions/blob/89aae438b3b3b0a8dd33003c9d70592baf7dbd0d/docs/messaging/kafka.md#L119"><c>messaging.kafka.message.key</c> semantic convention</see>.
    /// </summary>
    /// <param name="key">The message key, which may be <see langword="null"/>.</param>
    /// <returns>
    /// The canonical string representation of <paramref name="key"/>, or <see langword="null"/>
    /// when the key is absent or has no unambiguous, canonical string form (e.g. a
    /// <see cref="byte"/> array), in which case the attribute must be omitted.
    /// </returns>
    internal static string? FormatMessageKey(object? key) => key switch
    {
        string value => value,
        char value => value.ToString(),
        bool value => value.ToString(),
        byte value => value.ToString(CultureInfo.InvariantCulture),
        sbyte value => value.ToString(CultureInfo.InvariantCulture),
        short value => value.ToString(CultureInfo.InvariantCulture),
        ushort value => value.ToString(CultureInfo.InvariantCulture),
        int value => value.ToString(CultureInfo.InvariantCulture),
        uint value => value.ToString(CultureInfo.InvariantCulture),
        long value => value.ToString(CultureInfo.InvariantCulture),
        ulong value => value.ToString(CultureInfo.InvariantCulture),
        float value when !float.IsNaN(value) => value.ToString("R", CultureInfo.InvariantCulture),
        double value when !double.IsNaN(value) => value.ToString("R", CultureInfo.InvariantCulture),
        decimal value => value.ToString(CultureInfo.InvariantCulture),
        Guid value => value.ToString("D"),
        DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan value => value.ToString("c", CultureInfo.InvariantCulture),
        _ => null,
    };
}
