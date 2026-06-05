// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Aspire.Tests.Shared.Telemetry;

internal static class TelemetryTestHelpers
{
    public static void AssertId(string expected, string actual)
    {
        var resolvedActual = GetStringId(actual);

        Assert.Equal(expected, resolvedActual);
    }

    public static string GetStringId(string hexString)
    {
        var bytes = Convert.FromHexString(hexString);
        var resolved = Encoding.UTF8.GetString(bytes);
        return resolved;
    }

    public static string GetHexId(string text)
    {
        var id = Encoding.UTF8.GetBytes(text);
        return OtlpHelpers.ToHexString(id);
    }

    public static OtlpScope CreateOtlpScope(OtlpContext context, string? name = null, IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var scope = CreateScope(name, attributes);
        return new OtlpScope(scope.Name, scope.Version, scope.Attributes.ToKeyValuePairs(context));
    }

    public static InstrumentationScope CreateScope(string? name = null, IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var scope = new InstrumentationScope() { Name = name ?? "TestScope" };

        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                scope.Attributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
            }
        }

        return scope;
    }

    public static Metric CreateHistogramMetric(string metricName, DateTime startTime)
    {
        return new Metric
        {
            Name = metricName,
            Description = "Test metric description",
            Unit = "widget",
            Histogram = new Histogram
            {
                AggregationTemporality = AggregationTemporality.Cumulative,
                DataPoints =
                {
                    new HistogramDataPoint
                    {
                        Count = 1,
                        Sum = 1,
                        ExplicitBounds = { 1, 2, 3 },
                        BucketCounts = { 1, 2, 3 },
                        TimeUnixNano = DateTimeToUnixNanoseconds(startTime)
                    }
                }
            }
        };
    }

    public static Metric CreateSumMetric(string metricName, DateTime startTime, IEnumerable<KeyValuePair<string, string>>? attributes = null, IEnumerable<Exemplar>? exemplars = null, int? value = null)
    {
        return new Metric
        {
            Name = metricName,
            Description = "Test metric description",
            Unit = "widget",
            Sum = new Sum
            {
                AggregationTemporality = AggregationTemporality.Cumulative,
                IsMonotonic = true,
                DataPoints =
                {
                    CreateNumberPoint(startTime, value ?? 1, attributes, exemplars)
                }
            }
        };
    }

    private static NumberDataPoint CreateNumberPoint(DateTime startTime, int value, IEnumerable<KeyValuePair<string, string>>? attributes = null, IEnumerable<Exemplar>? exemplars = null)
    {
        var point = new NumberDataPoint
        {
            AsInt = value,
            StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
            TimeUnixNano = DateTimeToUnixNanoseconds(startTime)
        };
        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                point.Attributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
            }
        }
        if (exemplars != null)
        {
            foreach (var exemplar in exemplars)
            {
                point.Exemplars.Add(exemplar);
            }
        }

        return point;
    }

    public static Span.Types.Event CreateSpanEvent(string name, int startTime, IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var e = new Span.Types.Event
        {
            Name = name,
            TimeUnixNano = (ulong)startTime
        };
        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                e.Attributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
            }
        }

        return e;
    }

    public static Span CreateSpan(string traceId, string spanId, DateTime startTime, DateTime endTime, string? parentSpanId = null, List<Span.Types.Event>? events = null, List<Span.Types.Link>? links = null, IEnumerable<KeyValuePair<string, string>>? attributes = null, Span.Types.SpanKind? kind = null, Status? status = null)
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes(traceId)),
            SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes(spanId)),
            ParentSpanId = parentSpanId is null ? ByteString.Empty : ByteString.CopyFrom(Encoding.UTF8.GetBytes(parentSpanId)),
            StartTimeUnixNano = DateTimeToUnixNanoseconds(startTime),
            EndTimeUnixNano = DateTimeToUnixNanoseconds(endTime),
            Name = $"Test span. Id: {spanId}",
            Kind = kind ?? Span.Types.SpanKind.Internal,
            Status = status
        };
        if (events != null)
        {
            span.Events.AddRange(events);
        }
        if (links != null)
        {
            span.Links.AddRange(links);
        }
        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                span.Attributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
            }
        }

        return span;
    }

    public static LogRecord CreateLogRecord(DateTime? time = null, DateTime? observedTime = null, string? message = null, SeverityNumber? severity = null, IEnumerable<KeyValuePair<string, string>>? attributes = null,
        bool? skipBody = null, string? traceId = null, string? spanId = null, string? eventName = null)
    {
        attributes ??= [new KeyValuePair<string, string>("{OriginalFormat}", "Test {Log}"), new KeyValuePair<string, string>("Log", "Value!")];

        var logRecord = new LogRecord
        {
            Body = (skipBody ?? false) ? null : new AnyValue { StringValue = message ?? "Test Value!" },
            TraceId = (traceId != null) ? ByteString.CopyFrom(Encoding.UTF8.GetBytes(traceId)) : ByteString.CopyFrom(Convert.FromHexString("5465737454726163654964")),
            SpanId = (spanId != null) ? ByteString.CopyFrom(Encoding.UTF8.GetBytes(spanId)) : ByteString.CopyFrom(Convert.FromHexString("546573745370616e4964")),
            TimeUnixNano = time != null ? DateTimeToUnixNanoseconds(time.Value) : 1000,
            ObservedTimeUnixNano = observedTime != null ? DateTimeToUnixNanoseconds(observedTime.Value) : 1000,
            SeverityNumber = severity ?? SeverityNumber.Info,
            EventName = eventName ?? string.Empty
        };

        foreach (var attribute in attributes)
        {
            logRecord.Attributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
        }

        return logRecord;
    }

    public static Resource CreateResource(string? name = null, string? instanceId = null, IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var resource = new Resource()
        {
            Attributes =
            {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name ?? "TestService" } },
                new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = instanceId ?? "TestId" } }
            }
        };

        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                resource.Attributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
            }
        }

        return resource;
    }

    public static TelemetryRepository CreateRepository(
        int? maxMetricsCount = null,
        int? maxAttributeCount = null,
        int? maxAttributeLength = null,
        int? maxSpanEventCount = null,
        int? maxTraceCount = null,
        TimeSpan? subscriptionMinExecuteInterval = null,
        ILoggerFactory? loggerFactory = null,
        PauseManager? pauseManager = null,
        IOutgoingPeerResolver[]? outgoingPeerResolvers = null)
    {
        var options = new TelemetryLimitOptions();
        if (maxMetricsCount != null)
        {
            options.MaxMetricsCount = maxMetricsCount.Value;
        }
        if (maxAttributeCount != null)
        {
            options.MaxAttributeCount = maxAttributeCount.Value;
        }
        if (maxAttributeLength != null)
        {
            options.MaxAttributeLength = maxAttributeLength.Value;
        }
        if (maxSpanEventCount != null)
        {
            options.MaxSpanEventCount = maxSpanEventCount.Value;
        }
        if (maxTraceCount != null)
        {
            options.MaxTraceCount = maxTraceCount.Value;
        }

        var repository = new TelemetryRepository(
            loggerFactory ?? NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions { TelemetryLimits = options }),
            pauseManager ?? new PauseManager(),
            outgoingPeerResolvers ?? []);
        if (subscriptionMinExecuteInterval != null)
        {
            repository._subscriptionMinExecuteInterval = subscriptionMinExecuteInterval.Value;
        }
        return repository;
    }

    public static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timeSinceEpoch = dateTime.ToUniversalTime() - unixEpoch;

        return (ulong)timeSinceEpoch.Ticks * 100;
    }

    public static string GetValue(int valueLength)
    {
        var value = new StringBuilder(valueLength);
        for (var i = 0; i < valueLength; i++)
        {
            value.Append((i % 10).ToString(CultureInfo.InvariantCulture));
        }

        return value.ToString();
    }

    public static OtlpContext CreateContext(TelemetryLimitOptions? options = null, ILogger? logger = null)
    {
        return new OtlpContext
        {
            Options = options ?? new TelemetryLimitOptions(),
            Logger = logger ?? NullLogger.Instance
        };
    }

    public static OtlpSpan CreateOtlpSpan(OtlpResource resource, OtlpTrace trace, OtlpScope scope, string spanId, string? parentSpanId, DateTime startDate,
        KeyValuePair<string, string>[]? attributes = null, OtlpSpanStatusCode? statusCode = null, string? statusMessage = null, OtlpSpanKind kind = OtlpSpanKind.Unspecified,
        OtlpResource? uninstrumentedPeer = null, DateTime? endDate = null, string? spanName = null)
    {
        return new OtlpSpan(resource.GetView([]), trace, scope)
        {
            Attributes = attributes ?? [],
            BackLinks = [],
            EndTime = endDate ?? DateTime.MaxValue,
            Events = [],
            Kind = kind,
            Links = [],
            Name = spanName ?? "Test",
            ParentSpanId = parentSpanId,
            SpanId = spanId,
            StartTime = startDate,
            State = null,
            Status = statusCode ?? OtlpSpanStatusCode.Unset,
            StatusMessage = statusMessage,
            UninstrumentedPeer = uninstrumentedPeer
        };
    }

    public static X509Certificate2 GenerateDummyCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DummyCertificate",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddYears(1));

        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }
}
