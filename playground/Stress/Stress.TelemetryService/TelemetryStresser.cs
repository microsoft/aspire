// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Stress.TelemetryService;

/// <summary>
/// Send OTLP directly to the dashboard instead of going via opentelemetry-dotnet SDK to send raw and unlimited data.
/// </summary>
public class TelemetryStresser(ILogger<TelemetryStresser> logger, IConfiguration config) : BackgroundService
{
    private const string ExternalLogSourceName = "external-log-source";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var address = config["OTEL_EXPORTER_OTLP_ENDPOINT"]!;
        var channel = GrpcChannel.ForAddress(address);

        var metricsClient = new MetricsService.MetricsServiceClient(channel);
        var logsClient = new LogsService.LogsServiceClient(channel);
        var otlpApiKey = config["OTEL_EXPORTER_OTLP_HEADERS"]!.Split('=')[1];
        var metadata = new Metadata
        {
            { "x-otlp-api-key", otlpApiKey }
        };

        var value = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            value += Random.Shared.Next(0, 10);

            await ExportMetrics(logger, metadata, metricsClient, value, cancellationToken);
            await ExportStructuredLog(logger, metadata, logsClient, value, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task ExportMetrics(ILogger<TelemetryStresser> logger, Metadata metadata, MetricsService.MetricsServiceClient client, int value, CancellationToken cancellationToken)
    {
        var request = new ExportMetricsServiceRequest
        {
            ResourceMetrics =
                {
                    new ResourceMetrics
                    {
                        Resource = CreateResource("TestResource", "TestResource"),
                        ScopeMetrics =
                        {
                            new ScopeMetrics
                            {
                                Scope = new InstrumentationScope
                                {
                                    Name = "TestScope-<b>Bold</b>"
                                },
                                Metrics =
                                {
                                    CreateSumMetric("Test-<b>Bold</b>", DateTime.UtcNow, value: value)
                                }
                            },
                            new ScopeMetrics
                            {
                                Scope = null,
                                Metrics =
                                {
                                    CreateSumMetric("Test-<b>Bold</b>", DateTime.UtcNow, value: value)
                                }
                            }
                        }
                    }
                }
        };

        var response = await client.ExportAsync(request, headers: metadata, cancellationToken: cancellationToken);
        if (response.PartialSuccess is { RejectedDataPoints: > 0 } result)
        {
            logger.LogDebug($"Export complete. Rejected count: {result.RejectedDataPoints}");
        }
    }

    private static async Task ExportStructuredLog(ILogger<TelemetryStresser> logger, Metadata metadata, LogsService.LogsServiceClient client, int value, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeToUnixNanoseconds(DateTime.UtcNow);
        var request = new ExportLogsServiceRequest
        {
            ResourceLogs =
            {
                new ResourceLogs
                {
                    Resource = CreateResource(ExternalLogSourceName, "instance-1"),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = new InstrumentationScope { Name = "ExternalLogSource" },
                            LogRecords =
                            {
                                new LogRecord
                                {
                                    TimeUnixNano = timestamp,
                                    ObservedTimeUnixNano = timestamp,
                                    SeverityNumber = SeverityNumber.Info,
                                    SeverityText = "Information",
                                    Body = new AnyValue { StringValue = $"Processed item {value}." },
                                    Attributes =
                                    {
                                        new KeyValue { Key = "{OriginalFormat}", Value = new AnyValue { StringValue = "Processed item {ItemId}." } },
                                        new KeyValue { Key = "ItemId", Value = new AnyValue { IntValue = value } }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var response = await client.ExportAsync(request, headers: metadata, cancellationToken: cancellationToken);
        if (response.PartialSuccess is { RejectedLogRecords: > 0 } result)
        {
            logger.LogDebug("Export complete. Rejected log count: {RejectedLogCount}", result.RejectedLogRecords);
        }
    }

    public static Resource CreateResource(string? name = null, string? instanceId = null)
    {
        return new Resource()
        {
            Attributes =
            {
                new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = name ?? "TestService" } },
                new KeyValue { Key = "service.instance.id", Value = new AnyValue { StringValue = instanceId ?? "TestId" } }
            }
        };
    }

    public static Metric CreateSumMetric(string metricName, DateTime startTime, KeyValuePair<string, string>[]? attributes = null, int? value = null)
    {
        return new Metric
        {
            Name = metricName,
            Description = "Description-<b>Bold</b>",
            Unit = "Widget-<b>Bold</b>",
            Sum = new Sum
            {
                AggregationTemporality = AggregationTemporality.Cumulative,
                IsMonotonic = true,
                DataPoints =
                {
                    CreateNumberPoint(startTime, value ?? 1, attributes)
                }
            }
        };
    }

    private static NumberDataPoint CreateNumberPoint(DateTime startTime, int value, KeyValuePair<string, string>[]? attributes = null)
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

        return point;
    }

    public static ulong DateTimeToUnixNanoseconds(DateTime dateTime)
    {
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timeSinceEpoch = dateTime.ToUniversalTime() - unixEpoch;

        return (ulong)timeSinceEpoch.Ticks * 100;
    }
}
