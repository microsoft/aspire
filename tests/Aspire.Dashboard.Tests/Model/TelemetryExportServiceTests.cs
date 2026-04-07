// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Serialization;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Otlp.Serialization;
using Aspire.Dashboard.Tests.TelemetryRepositoryTests;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Model;

public sealed class TelemetryExportServiceTests
{
    private static readonly DateTime s_testTime = new(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void ConvertLogsToOtlpJson_SingleLog_ReturnsCorrectStructure()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestService", instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Test log message", severity: OpenTelemetry.Proto.Logs.V1.SeverityNumber.Info, eventName: "TestEvent", traceId: "abcd1234abcd1234", spanId: "efgh5678", attributes: [new KeyValuePair<string, string>("custom.attr", "custom-value")]) }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var logs = repository.GetLogs(new GetLogsContext
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        });

        // Act
        var result = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);

        // Assert
        Assert.NotNull(result.ResourceLogs);
        Assert.Single(result.ResourceLogs);

        var resourceLogs = result.ResourceLogs[0];
        Assert.NotNull(resourceLogs.Resource);
        Assert.NotNull(resourceLogs.Resource.Attributes);
        Assert.Contains(resourceLogs.Resource.Attributes, a => a.Key == OtlpResource.SERVICE_NAME && a.Value?.StringValue == "TestService");
        Assert.Contains(resourceLogs.Resource.Attributes, a => a.Key == OtlpResource.SERVICE_INSTANCE_ID && a.Value?.StringValue == "instance-1");

        Assert.NotNull(resourceLogs.ScopeLogs);
        Assert.Single(resourceLogs.ScopeLogs);

        var scopeLogs = resourceLogs.ScopeLogs[0];
        Assert.NotNull(scopeLogs.Scope);
        Assert.Equal("TestLogger", scopeLogs.Scope.Name);

        Assert.NotNull(scopeLogs.LogRecords);
        Assert.Single(scopeLogs.LogRecords);

        var logRecord = scopeLogs.LogRecords[0];
        Assert.Equal("Test log message", logRecord.Body?.StringValue);
        Assert.Equal((int)SeverityNumber.Info, logRecord.SeverityNumber);
        Assert.Equal("Information", logRecord.SeverityText);
        Assert.Equal("TestEvent", logRecord.EventName);
        Assert.Equal(OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime), logRecord.TimeUnixNano);
        Assert.Equal("61626364313233346162636431323334", logRecord.TraceId); // hex of UTF-8 bytes of "abcd1234abcd1234"
        Assert.Equal("6566676835363738", logRecord.SpanId); // hex of UTF-8 bytes of "efgh5678"
        Assert.NotNull(logRecord.Attributes);
        Assert.Contains(logRecord.Attributes, a => a.Key == "custom.attr" && a.Value?.StringValue == "custom-value");
    }

    [Fact]
    public void ConvertLogsToOtlpJson_AddsAspireLogIdAttribute()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestService", instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Test log message") }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var logs = repository.GetLogs(GetLogsContext.ForResourceKey(resource.ResourceKey));

        // Act
        var result = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);

        // Assert
        var logRecord = result.ResourceLogs![0].ScopeLogs![0].LogRecords![0];
        Assert.NotNull(logRecord.Attributes);

        // Verify aspire.log_id attribute is added with the InternalId value
        var logIdAttribute = Assert.Single(logRecord.Attributes, a => a.Key == OtlpHelpers.AspireLogIdAttribute);
        Assert.Equal(logs.Items[0].InternalId.ToString(CultureInfo.InvariantCulture), logIdAttribute.Value?.StringValue);
    }

    [Fact]
    public void ConvertLogsToOtlpJson_MultipleLogs_GroupsByScope()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestService", instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("Logger1"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime, message: "Log from Logger1"),
                            CreateLogRecord(time: s_testTime.AddSeconds(1), message: "Another log from Logger1")
                        }
                    },
                    new ScopeLogs
                    {
                        Scope = CreateScope("Logger2"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddSeconds(2), message: "Log from Logger2") }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var logs = repository.GetLogs(GetLogsContext.ForResourceKey(resource.ResourceKey));

        // Act
        var result = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);

        // Assert
        Assert.NotNull(result.ResourceLogs);
        Assert.Single(result.ResourceLogs);

        var resourceLogs = result.ResourceLogs[0];
        Assert.NotNull(resourceLogs.ScopeLogs);
        Assert.Equal(2, resourceLogs.ScopeLogs.Length);

        var logger1Scope = resourceLogs.ScopeLogs.FirstOrDefault(s => s.Scope?.Name == "Logger1");
        Assert.NotNull(logger1Scope);
        Assert.NotNull(logger1Scope.LogRecords);
        Assert.Equal(2, logger1Scope.LogRecords.Length);

        var logger2Scope = resourceLogs.ScopeLogs.FirstOrDefault(s => s.Scope?.Name == "Logger2");
        Assert.NotNull(logger2Scope);
        Assert.NotNull(logger2Scope.LogRecords);
        Assert.Single(logger2Scope.LogRecords);
    }

    [Theory]
    [InlineData(SeverityNumber.Trace, "Trace")]
    [InlineData(SeverityNumber.Trace2, "Trace")]
    [InlineData(SeverityNumber.Trace3, "Trace")]
    [InlineData(SeverityNumber.Trace4, "Trace")]
    [InlineData(SeverityNumber.Debug, "Debug")]
    [InlineData(SeverityNumber.Debug2, "Debug")]
    [InlineData(SeverityNumber.Debug3, "Debug")]
    [InlineData(SeverityNumber.Debug4, "Debug")]
    [InlineData(SeverityNumber.Info, "Information")]
    [InlineData(SeverityNumber.Info2, "Information")]
    [InlineData(SeverityNumber.Info3, "Information")]
    [InlineData(SeverityNumber.Info4, "Information")]
    [InlineData(SeverityNumber.Warn, "Warning")]
    [InlineData(SeverityNumber.Warn2, "Warning")]
    [InlineData(SeverityNumber.Warn3, "Warning")]
    [InlineData(SeverityNumber.Warn4, "Warning")]
    [InlineData(SeverityNumber.Error, "Error")]
    [InlineData(SeverityNumber.Error2, "Error")]
    [InlineData(SeverityNumber.Error3, "Error")]
    [InlineData(SeverityNumber.Error4, "Error")]
    [InlineData(SeverityNumber.Fatal, "Critical")]
    [InlineData(SeverityNumber.Fatal2, "Critical")]
    [InlineData(SeverityNumber.Fatal3, "Critical")]
    [InlineData(SeverityNumber.Fatal4, "Critical")]
    public void ConvertLogsToOtlpJson_RoundTripsSeverityNumber(SeverityNumber inputSeverity, string expectedSeverityText)
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord(severity: inputSeverity) }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var logs = repository.GetLogs(GetLogsContext.ForResourceKey(resource.ResourceKey));

        // Act
        var result = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);

        // Assert
        var logRecord = result.ResourceLogs![0].ScopeLogs![0].LogRecords![0];
        // Verify exact severity number is preserved (round-trip)
        Assert.Equal((int)inputSeverity, logRecord.SeverityNumber);
        // Verify severity text is the mapped LogLevel
        Assert.Equal(expectedSeverityText, logRecord.SeverityText);
    }

    [Fact]
    public void ConvertTracesToOtlpJson_SingleTrace_ReturnsCorrectStructure()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "TestService", instanceId: "instance-1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("TestTracer"),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "trace123456789012",
                                spanId: "span1234",
                                startTime: s_testTime,
                                endTime: s_testTime.AddSeconds(5),
                                kind: Span.Types.SpanKind.Server,
                                status: new Status { Code = Status.Types.StatusCode.Error, Message = "Something went wrong" },
                                attributes: [new KeyValuePair<string, string>("http.method", "GET")])
                        }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = int.MaxValue,
            FilterText = string.Empty,
            Filters = []
        });

        // Act
        var result = TelemetryExportService.ConvertTracesToOtlpJson(traces.PagedResult.Items, []);

        // Assert
        Assert.NotNull(result.ResourceSpans);
        Assert.Single(result.ResourceSpans);

        var resourceSpans = result.ResourceSpans[0];
        Assert.NotNull(resourceSpans.Resource);
        Assert.NotNull(resourceSpans.Resource.Attributes);
        Assert.Contains(resourceSpans.Resource.Attributes, a => a.Key == OtlpResource.SERVICE_NAME && a.Value?.StringValue == "TestService");

        Assert.NotNull(resourceSpans.ScopeSpans);
        Assert.Single(resourceSpans.ScopeSpans);

        var scopeSpans = resourceSpans.ScopeSpans[0];
        Assert.NotNull(scopeSpans.Scope);
        Assert.Equal("TestTracer", scopeSpans.Scope.Name);

        Assert.NotNull(scopeSpans.Spans);
        Assert.Single(scopeSpans.Spans);

        var span = scopeSpans.Spans[0];
        Assert.Equal((int)OtlpSpanKind.Server, span.Kind);
        Assert.Equal("7472616365313233343536373839303132", span.TraceId); // hex of UTF-8 bytes of "trace123456789012"
        Assert.Equal("7370616e31323334", span.SpanId); // hex of UTF-8 bytes of "span1234"
        Assert.Equal("Test span. Id: span1234", span.Name);
        Assert.Equal(OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime), span.StartTimeUnixNano);
        Assert.Equal(OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime.AddSeconds(5)), span.EndTimeUnixNano);
        Assert.NotNull(span.Status);
        Assert.Equal((int)Status.Types.StatusCode.Error, span.Status.Code);
        Assert.Equal("Something went wrong", span.Status.Message);
        Assert.NotNull(span.Attributes);
        Assert.Contains(span.Attributes, a => a.Key == "http.method" && a.Value?.StringValue == "GET");
    }

    [Fact]
    public void ConvertTracesToOtlpJson_SpanWithParent_IncludesParentSpanId()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace123456789012", spanId: "parent12", startTime: s_testTime, endTime: s_testTime.AddSeconds(10)),
                            CreateSpan(traceId: "trace123456789012", spanId: "child123", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(5), parentSpanId: "parent12")
                        }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var traces = repository.GetTraces(GetTracesRequest.ForResourceKey(resource.ResourceKey));

        // Act
        var result = TelemetryExportService.ConvertTracesToOtlpJson(traces.PagedResult.Items, []);

        // Assert
        var spans = result.ResourceSpans![0].ScopeSpans![0].Spans!;
        Assert.Equal(2, spans.Length);

        var parentSpan = spans.First(s => s.ParentSpanId is null);
        var childSpan = spans.First(s => s.ParentSpanId is not null);

        Assert.NotNull(childSpan.ParentSpanId);
    }

    [Fact]
    public void ConvertTracesToOtlpJson_WithPeerResolvers_AddsDestinationNameAttribute()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "trace123456789012",
                                spanId: "span1234",
                                startTime: s_testTime,
                                endTime: s_testTime.AddSeconds(5),
                                attributes: [new KeyValuePair<string, string>("peer.service", "target-service")])
                        }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var traces = repository.GetTraces(GetTracesRequest.ForResourceKey(resource.ResourceKey));

        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: attributes =>
        {
            var peerService = attributes.FirstOrDefault(a => a.Key == "peer.service");
            return (peerService.Value, null);
        });

        // Act
        var result = TelemetryExportService.ConvertTracesToOtlpJson(traces.PagedResult.Items, [outgoingPeerResolver]);

        // Assert
        var span = result.ResourceSpans![0].ScopeSpans![0].Spans![0];
        Assert.NotNull(span.Attributes);
        Assert.Contains(span.Attributes, a => a.Key == OtlpHelpers.AspireDestinationNameAttribute && a.Value?.StringValue == "target-service");
    }

    [Fact]
    public void ConvertTracesToOtlpJson_WithoutPeerResolvers_DoesNotAddDestinationNameAttribute()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "trace123456789012",
                                spanId: "span1234",
                                startTime: s_testTime,
                                endTime: s_testTime.AddSeconds(5),
                                attributes: [new KeyValuePair<string, string>("peer.service", "target-service")])
                        }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var traces = repository.GetTraces(GetTracesRequest.ForResourceKey(resource.ResourceKey));

        // Act
        var result = TelemetryExportService.ConvertTracesToOtlpJson(traces.PagedResult.Items, []);

        // Assert
        var span = result.ResourceSpans![0].ScopeSpans![0].Spans![0];
        Assert.NotNull(span.Attributes);
        Assert.DoesNotContain(span.Attributes, a => a.Key == OtlpHelpers.AspireDestinationNameAttribute);
    }

    [Fact]
    public void ConvertMetricsToOtlpJson_SingleInstrument_ReturnsCorrectStructure()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddMetrics(addContext, new RepeatedField<OpenTelemetry.Proto.Metrics.V1.ResourceMetrics>()
        {
            new OpenTelemetry.Proto.Metrics.V1.ResourceMetrics
            {
                Resource = CreateResource(name: "TestService", instanceId: "instance-1"),
                ScopeMetrics =
                {
                    new OpenTelemetry.Proto.Metrics.V1.ScopeMetrics
                    {
                        Scope = CreateScope("TestMeter"),
                        Metrics = { CreateSumMetric("test_counter", s_testTime) }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var instrumentSummaries = repository.GetInstrumentsSummaries(resource.ResourceKey);

        // Get full instrument data with values
        var instrumentsData = new List<OtlpInstrumentData>();
        foreach (var summary in instrumentSummaries)
        {
            var instrumentData = repository.GetInstrument(new GetInstrumentRequest
            {
                ResourceKey = resource.ResourceKey,
                MeterName = summary.Parent.Name,
                InstrumentName = summary.Name,
                StartTime = DateTime.MinValue,
                EndTime = DateTime.MaxValue
            });
            if (instrumentData is not null)
            {
                instrumentsData.Add(instrumentData);
            }
        }

        // Act
        var result = TelemetryExportService.ConvertMetricsToOtlpJson(resource, instrumentsData);

        // Assert
        Assert.NotNull(result.ResourceMetrics);
        Assert.Single(result.ResourceMetrics);

        var resourceMetrics = result.ResourceMetrics[0];
        Assert.NotNull(resourceMetrics.Resource);
        Assert.NotNull(resourceMetrics.Resource.Attributes);
        Assert.Contains(resourceMetrics.Resource.Attributes, a => a.Key == OtlpResource.SERVICE_NAME && a.Value?.StringValue == "TestService");

        Assert.NotNull(resourceMetrics.ScopeMetrics);
        Assert.Single(resourceMetrics.ScopeMetrics);

        var scopeMetrics = resourceMetrics.ScopeMetrics[0];
        Assert.NotNull(scopeMetrics.Scope);
        Assert.Equal("TestMeter", scopeMetrics.Scope.Name);

        Assert.NotNull(scopeMetrics.Metrics);
        Assert.Single(scopeMetrics.Metrics);

        var metric = scopeMetrics.Metrics[0];
        Assert.Equal("test_counter", metric.Name);
        Assert.Equal("Test metric description", metric.Description);
        Assert.Equal("widget", metric.Unit);

        // Verify data points are included
        Assert.NotNull(metric.Sum);
        Assert.NotNull(metric.Sum.DataPoints);
        Assert.NotEmpty(metric.Sum.DataPoints);
    }

    [Fact]
    public void ConvertMetricsToOtlpJson_MultipleInstruments_GroupsByScope()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddMetrics(addContext, new RepeatedField<OpenTelemetry.Proto.Metrics.V1.ResourceMetrics>()
        {
            new OpenTelemetry.Proto.Metrics.V1.ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new OpenTelemetry.Proto.Metrics.V1.ScopeMetrics
                    {
                        Scope = CreateScope("Meter1"),
                        Metrics =
                        {
                            CreateSumMetric("counter1", s_testTime),
                            CreateSumMetric("counter2", s_testTime)
                        }
                    },
                    new OpenTelemetry.Proto.Metrics.V1.ScopeMetrics
                    {
                        Scope = CreateScope("Meter2"),
                        Metrics = { CreateHistogramMetric("histogram1", s_testTime) }
                    }
                }
            }
        });

        var resources = repository.GetResources();
        var resource = resources[0];
        var instrumentSummaries = repository.GetInstrumentsSummaries(resource.ResourceKey);

        // Get full instrument data with values
        var instrumentsData = new List<OtlpInstrumentData>();
        foreach (var summary in instrumentSummaries)
        {
            var instrumentData = repository.GetInstrument(new GetInstrumentRequest
            {
                ResourceKey = resource.ResourceKey,
                MeterName = summary.Parent.Name,
                InstrumentName = summary.Name,
                StartTime = DateTime.MinValue,
                EndTime = DateTime.MaxValue
            });
            if (instrumentData is not null)
            {
                instrumentsData.Add(instrumentData);
            }
        }

        // Act
        var result = TelemetryExportService.ConvertMetricsToOtlpJson(resource, instrumentsData);

        // Assert
        Assert.NotNull(result.ResourceMetrics);
        Assert.Single(result.ResourceMetrics);

        var resourceMetrics = result.ResourceMetrics[0];
        Assert.NotNull(resourceMetrics.ScopeMetrics);
        Assert.Equal(2, resourceMetrics.ScopeMetrics.Length);

        var meter1Scope = resourceMetrics.ScopeMetrics.FirstOrDefault(s => s.Scope?.Name == "Meter1");
        Assert.NotNull(meter1Scope);
        Assert.NotNull(meter1Scope.Metrics);
        Assert.Equal(2, meter1Scope.Metrics.Length);

        var meter2Scope = resourceMetrics.ScopeMetrics.FirstOrDefault(s => s.Scope?.Name == "Meter2");
        Assert.NotNull(meter2Scope);
        Assert.NotNull(meter2Scope.Metrics);
        Assert.Single(meter2Scope.Metrics);

        // Verify histogram has data points
        var histogram = meter2Scope.Metrics[0];
        Assert.NotNull(histogram.Histogram);
        Assert.NotNull(histogram.Histogram.DataPoints);
        Assert.NotEmpty(histogram.Histogram.DataPoints);
    }

    [Fact]
    public async Task ExportSelectedAsync_ExportsOnlySelectedDataTypesForSpecificResources()
    {
        // Arrange
        var repository = CreateRepository();
        var exportService = await CreateExportServiceAsync(repository);

        // Add test data for three resources
        AddTestData(repository, "resource1", "111");
        AddTestData(repository, "resource2", "222");
        AddTestData(repository, "resource3", "333");
        AddTestData(repository, "resource4", "444");

        // Act - Export only structured logs for resource1, only traces for resource2, all types for resource3
        var selectedResources = new Dictionary<string, HashSet<AspireDataType>>
        {
            ["resource1-111"] = [AspireDataType.StructuredLogs],
            ["resource2-222"] = [AspireDataType.Traces],
            ["resource3-333"] = [AspireDataType.StructuredLogs, AspireDataType.Traces, AspireDataType.Metrics]
        };

        using var memoryStream = await exportService.ExportSelectedAsync(selectedResources, CancellationToken.None);

        // Assert - Verify the zip archive contents
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(e => e.FullName).OrderBy(e => e).ToList();

        // Verify exactly 5 entries: resource1 (logs), resource2 (traces), resource3 (logs, traces, metrics)
        // resource4 is not selected so should not be exported
        Assert.Collection(entryNames,
            e => Assert.Equal("metrics/resource3.json", e),
            e => Assert.Equal("structuredlogs/resource1.json", e),
            e => Assert.Equal("structuredlogs/resource3.json", e),
            e => Assert.Equal("traces/resource2.json", e),
            e => Assert.Equal("traces/resource3.json", e));

        // Verify the content of the exported structured logs for resource1
        var resource1LogsEntry = archive.Entries.First(e => e.FullName.Contains("structuredlogs") && e.FullName.Contains("resource1"));
        using var logStream = resource1LogsEntry.Open();
        var logsData = await JsonSerializer.DeserializeAsync(logStream, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        var logRecord = logsData?.ResourceLogs?.FirstOrDefault()?.ScopeLogs?.FirstOrDefault()?.LogRecords?.FirstOrDefault();
        Assert.NotNull(logRecord);
        Assert.Equal("log-resource1-111", logRecord.Body?.StringValue);

        // Verify the content of the exported traces for resource2
        var resource2TracesEntry = archive.Entries.First(e => e.FullName.Contains("traces") && e.FullName.Contains("resource2"));
        using var traceStream = resource2TracesEntry.Open();
        var tracesData = await JsonSerializer.DeserializeAsync(traceStream, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        var span = tracesData?.ResourceSpans?.FirstOrDefault()?.ScopeSpans?.FirstOrDefault()?.Spans?.FirstOrDefault();
        Assert.NotNull(span);
        Assert.Contains("resource2-222", span.Name);

        // Verify the content of the exported metrics for resource3
        var resource3MetricsEntry = archive.Entries.First(e => e.FullName.Contains("metrics") && e.FullName.Contains("resource3"));
        using var metricsStream = resource3MetricsEntry.Open();
        var metricsData = await JsonSerializer.DeserializeAsync(metricsStream, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        var metric = metricsData?.ResourceMetrics?.FirstOrDefault()?.ScopeMetrics?.FirstOrDefault()?.Metrics?.FirstOrDefault();
        Assert.NotNull(metric);
        Assert.Equal("metric-resource3-333", metric.Name);
    }

    [Fact]
    public async Task ExportAllAsync_WhenDashboardClientDisabled_ExportsOnlyTelemetry()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();

        // Add logs
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "Service1", instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("Logger1"),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Structured log") }
                    }
                }
            }
        });

        // Dashboard client is disabled (no console logs)
        var service = await CreateExportServiceAsync(repository, isDashboardClientEnabled: false);

        // Build selection for all resources with all data types
        var selectedResources = BuildAllResourcesSelection(repository);

        // Act
        using var zipStream = await service.ExportSelectedAsync(selectedResources, CancellationToken.None).DefaultTimeout();

        // Assert
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(e => e.FullName).Order().ToList();

        // Verify only structured logs are exported (no console logs)
        Assert.Collection(entryNames,
            name => Assert.Equal("structuredlogs/Service1.json", name));
    }

    [Fact]
    public async Task ExportSelectedAsync_SkipsEmptyResources()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();

        // Add logs for only one resource
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "ServiceWithLogs", instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("Logger1"),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Log message") }
                    }
                }
            }
        });

        // Add traces for a different resource
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "ServiceWithTraces", instanceId: "instance-2"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("Tracer1"),
                        Spans = { CreateSpan(traceId: "trace123456789012", spanId: "span1111", startTime: s_testTime, endTime: s_testTime.AddSeconds(5)) }
                    }
                }
            }
        });

        var service = await CreateExportServiceAsync(repository, isDashboardClientEnabled: false);

        // Build selection for all resources with all data types
        var selectedResources = BuildAllResourcesSelection(repository);

        // Act
        using var zipStream = await service.ExportSelectedAsync(selectedResources, CancellationToken.None).DefaultTimeout();

        // Assert
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entryNames = archive.Entries.Select(e => e.FullName).Order().ToList();

        // Verify each resource only has its own data type exported
        Assert.Collection(entryNames,
            name => Assert.Equal("structuredlogs/ServiceWithLogs.json", name),
            name => Assert.Equal("traces/ServiceWithTraces.json", name));
    }

    [Fact]
    public async Task ExportSelectedAsync_JapaneseCharactersInLogs_PreservesContent()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();

        const string japaneseMessage = "これはテストログメッセージです"; // "This is a test log message"
        const string japaneseAttributeValue = "日本語の属性値"; // "Japanese attribute value"
        const string japaneseEventName = "テストイベント"; // "Test event"

        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "JapaneseService", instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("JapaneseLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(
                                time: s_testTime,
                                message: japaneseMessage,
                                severity: SeverityNumber.Info,
                                eventName: japaneseEventName,
                                attributes: [new KeyValuePair<string, string>("japanese.attr", japaneseAttributeValue)])
                        }
                    }
                }
            }
        });

        var service = await CreateExportServiceAsync(repository, isDashboardClientEnabled: false);

        // Build selection for all resources with all data types
        var selectedResources = BuildAllResourcesSelection(repository);

        // Act
        using var zipStream = await service.ExportSelectedAsync(selectedResources, CancellationToken.None).DefaultTimeout();

        // Assert
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var logEntry = archive.GetEntry("structuredlogs/JapaneseService.json");
        Assert.NotNull(logEntry);

        using var reader = new StreamReader(logEntry.Open());
        var jsonContent = await reader.ReadToEndAsync().DefaultTimeout();

        // Verify Japanese characters appear directly in JSON (not Unicode-escaped)
        Assert.Contains(japaneseMessage, jsonContent);
        Assert.Contains(japaneseAttributeValue, jsonContent);
        Assert.Contains(japaneseEventName, jsonContent);

        // Deserialize the JSON to verify the content is correct after round-trip
        var logsData = JsonSerializer.Deserialize(jsonContent, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);

        Assert.NotNull(logsData);
        Assert.NotNull(logsData.ResourceLogs);
        Assert.Single(logsData.ResourceLogs);

        var resourceLogs = logsData.ResourceLogs[0];
        Assert.NotNull(resourceLogs.ScopeLogs);
        Assert.Single(resourceLogs.ScopeLogs);

        var scopeLogs = resourceLogs.ScopeLogs[0];
        Assert.NotNull(scopeLogs.LogRecords);
        Assert.Single(scopeLogs.LogRecords);

        var logRecord = scopeLogs.LogRecords[0];

        // Verify Japanese characters are preserved after serialization and deserialization
        Assert.Equal(japaneseMessage, logRecord.Body?.StringValue);
        Assert.Equal(japaneseEventName, logRecord.EventName);

        Assert.NotNull(logRecord.Attributes);
        var japaneseAttr = Assert.Single(logRecord.Attributes, a => a.Key == "japanese.attr");
        Assert.Equal(japaneseAttributeValue, japaneseAttr.Value?.StringValue);
    }

    [Fact]
    public void ConvertSpanToJson_ReturnsValidOtlpTelemetryDataJson()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "trace123456789012", spanId: "span1234", startTime: s_testTime, endTime: s_testTime.AddSeconds(5)) }
                    }
                }
            }
        });

        var span = repository.GetTraces(GetTracesRequest.ForResourceKey(repository.GetResources()[0].ResourceKey)).PagedResult.Items[0].Spans[0];

        // Act
        var json = TelemetryExportService.ConvertSpanToJson(span, []);

        // Assert - deserialize back to verify OtlpTelemetryDataJson structure
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);

        Assert.NotNull(data?.ResourceSpans);
        Assert.Single(data.ResourceSpans);
        Assert.NotNull(data.ResourceSpans[0].Resource?.Attributes);
        Assert.NotNull(data.ResourceSpans[0].ScopeSpans);
        Assert.Single(data.ResourceSpans[0].ScopeSpans![0].Spans!);
    }

    [Fact]
    public void ConvertSpanToJson_WithLogs_IncludesLogsInOutput()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "trace123456789012", spanId: "span1234", startTime: s_testTime, endTime: s_testTime.AddSeconds(5)) }
                    }
                }
            }
        });
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddSeconds(1), message: "Span log", traceId: "trace123456789012", spanId: "span1234") }
                    }
                }
            }
        });

        var span = repository.GetTraces(GetTracesRequest.ForResourceKey(repository.GetResources()[0].ResourceKey)).PagedResult.Items[0].Spans[0];
        var logs = repository.GetLogs(GetLogsContext.ForResourceKey(repository.GetResources()[0].ResourceKey)).Items;

        // Act
        var json = TelemetryExportService.ConvertSpanToJson(span, [], logs);

        // Assert - verify both spans and logs are in the output
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);

        Assert.NotNull(data?.ResourceSpans);
        Assert.Single(data.ResourceSpans[0].ScopeSpans![0].Spans!);
        Assert.NotNull(data.ResourceLogs);
        Assert.Single(data.ResourceLogs[0].ScopeLogs![0].LogRecords!);
    }

    [Fact]
    public void ConvertTraceToJson_WithLogs_IncludesLogsInOutput()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace123456789012", spanId: "parent12", startTime: s_testTime, endTime: s_testTime.AddSeconds(10)),
                            CreateSpan(traceId: "trace123456789012", spanId: "child123", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(5), parentSpanId: "parent12")
                        }
                    }
                }
            }
        });
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddSeconds(1), message: "Log 1", traceId: "trace123456789012", spanId: "parent12"),
                            CreateLogRecord(time: s_testTime.AddSeconds(2), message: "Log 2", traceId: "trace123456789012", spanId: "child123")
                        }
                    }
                }
            }
        });

        var trace = repository.GetTraces(GetTracesRequest.ForResourceKey(repository.GetResources()[0].ResourceKey)).PagedResult.Items[0];
        var logs = repository.GetLogs(GetLogsContext.ForResourceKey(repository.GetResources()[0].ResourceKey)).Items;

        // Act
        var json = TelemetryExportService.ConvertTraceToJson(trace, [], logs);

        // Assert - verify both spans and logs are in the output
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);

        Assert.NotNull(data?.ResourceSpans);
        Assert.Equal(2, data.ResourceSpans[0].ScopeSpans![0].Spans!.Length);
        Assert.NotNull(data.ResourceLogs);
        Assert.Equal(2, data.ResourceLogs[0].ScopeLogs![0].LogRecords!.Length);
    }

    [Fact]
    public void ConvertTraceToJson_ReturnsValidOtlpTelemetryDataJson()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace123456789012", spanId: "parent12", startTime: s_testTime, endTime: s_testTime.AddSeconds(10)),
                            CreateSpan(traceId: "trace123456789012", spanId: "child123", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(5), parentSpanId: "parent12")
                        }
                    }
                }
            }
        });

        var trace = repository.GetTraces(GetTracesRequest.ForResourceKey(repository.GetResources()[0].ResourceKey)).PagedResult.Items[0];

        // Act
        var json = TelemetryExportService.ConvertTraceToJson(trace, []);

        // Assert - deserialize back to verify OtlpTelemetryDataJson structure
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);

        Assert.NotNull(data?.ResourceSpans);
        Assert.Single(data.ResourceSpans);
        Assert.NotNull(data.ResourceSpans[0].Resource?.Attributes);
        Assert.Equal(2, data.ResourceSpans[0].ScopeSpans![0].Spans!.Length);
    }

    [Fact]
    public void ConvertLogEntryToJson_ReturnsValidOtlpTelemetryDataJson()
    {
        // Arrange
        var repository = CreateRepository();
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Test message") }
                    }
                }
            }
        });

        var logEntry = repository.GetLogs(GetLogsContext.ForResourceKey(repository.GetResources()[0].ResourceKey)).Items[0];

        // Act
        var json = TelemetryExportService.ConvertLogEntryToJson(logEntry);

        // Assert - deserialize back to verify OtlpTelemetryDataJson structure
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);

        Assert.NotNull(data?.ResourceLogs);
        Assert.Single(data.ResourceLogs);
        Assert.NotNull(data.ResourceLogs[0].Resource?.Attributes);
        Assert.Single(data.ResourceLogs[0].ScopeLogs![0].LogRecords!);
    }

    private static async Task<TelemetryExportService> CreateExportServiceAsync(TelemetryRepository repository, bool isDashboardClientEnabled = true)
    {
        var dashboardClient = new TestDashboardClient(isEnabled: isDashboardClientEnabled);
        var sessionStorage = new TestSessionStorage();
        var consoleLogsManager = new ConsoleLogsManager(sessionStorage);
        await consoleLogsManager.EnsureInitializedAsync();
        var consoleLogsFetcher = new ConsoleLogsFetcher(dashboardClient, consoleLogsManager);
        return new TelemetryExportService(repository, consoleLogsFetcher, dashboardClient, Array.Empty<IOutgoingPeerResolver>());
    }

    private static Dictionary<string, HashSet<AspireDataType>> BuildAllResourcesSelection(TelemetryRepository repository)
    {
        var allResources = repository.GetResources();
        return allResources.ToDictionary(
            r => r.ResourceKey.GetCompositeName(),
            _ => new HashSet<AspireDataType>([AspireDataType.ConsoleLogs, AspireDataType.StructuredLogs, AspireDataType.Traces, AspireDataType.Metrics]));
    }

    private static void AddTestData(TelemetryRepository repository, string resourceName, string instanceId)
    {
        var compositeName = $"{resourceName}-{instanceId}";

        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: resourceName, instanceId: instanceId),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(1), message: $"log-{compositeName}") }
                    }
                }
            }
        });

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: resourceName, instanceId: instanceId),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: compositeName, spanId: $"{compositeName}-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        repository.AddMetrics(new AddContext(), new RepeatedField<OpenTelemetry.Proto.Metrics.V1.ResourceMetrics>()
        {
            new OpenTelemetry.Proto.Metrics.V1.ResourceMetrics
            {
                Resource = CreateResource(name: resourceName, instanceId: instanceId),
                ScopeMetrics =
                {
                    new OpenTelemetry.Proto.Metrics.V1.ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: $"metric-{compositeName}", value: 1, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });
    }

    [Fact]
    public void ConvertResourceToJson_ReturnsExpectedJson()
    {
        // Arrange
        var dependencyResource = ModelTestHelpers.CreateResource(
            resourceName: "dependency-resource",
            displayName: "dependency",
            resourceType: "Container",
            state: KnownResourceState.Running);

        var resource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            displayName: "Test Resource",
            resourceType: "Container",
            state: KnownResourceState.Running,
            urls: [new UrlViewModel("http", new Uri("http://localhost:5000"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)],
            environment: [new EnvironmentVariableViewModel("MY_VAR", "my-value", fromSpec: true)],
            relationships: [new RelationshipViewModel("dependency", "Reference")]);

        var allResources = new[] { resource, dependencyResource };

        // Act
        var json = TelemetryExportService.ConvertResourceToJson(resource, allResources);

        // Assert
        var deserialized = JsonSerializer.Deserialize(json, ResourceJsonSerializerContext.Default.ResourceJson);
        Assert.NotNull(deserialized);
        Assert.Equal("test-resource", deserialized.Name);
        Assert.Equal("Test Resource", deserialized.DisplayName);
        Assert.Equal("Container", deserialized.ResourceType);
        Assert.Equal("Running", deserialized.State);

        Assert.NotNull(deserialized.Urls);
        Assert.Single(deserialized.Urls);
        Assert.Equal("http://localhost:5000/", deserialized.Urls[0].Url);

        Assert.NotNull(deserialized.Environment);
        Assert.Single(deserialized.Environment);
        Assert.True(deserialized.Environment.ContainsKey("MY_VAR"));

        // Relationships are resolved by matching DisplayName. Since there's only one resource
        // with that display name (not a replica), the display name is used as the resource name.
        Assert.NotNull(deserialized.Relationships);
        Assert.Single(deserialized.Relationships);
        Assert.Equal("dependency", deserialized.Relationships[0].ResourceName);
        Assert.Equal("Reference", deserialized.Relationships[0].Type);
    }

    [Fact]
    public void ConvertResourceToJson_OnlyIncludesFromSpecEnvironmentVariables()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "test-resource",
            displayName: "Test Resource",
            resourceType: "Container",
            state: KnownResourceState.Running,
            environment:
            [
                new EnvironmentVariableViewModel("FROM_SPEC_VAR", "spec-value", fromSpec: true),
                new EnvironmentVariableViewModel("NOT_FROM_SPEC_VAR", "other-value", fromSpec: false),
                new EnvironmentVariableViewModel("ANOTHER_SPEC_VAR", "another-spec-value", fromSpec: true)
            ]);

        // Act
        var json = TelemetryExportService.ConvertResourceToJson(resource, [resource]);

        // Assert
        var deserialized = JsonSerializer.Deserialize(json, ResourceJsonSerializerContext.Default.ResourceJson);
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized.Environment);
        Assert.Equal(2, deserialized.Environment.Count);
        Assert.Contains("FROM_SPEC_VAR", deserialized.Environment.Keys);
        Assert.Equal("spec-value", deserialized.Environment["FROM_SPEC_VAR"]);
        Assert.Contains("ANOTHER_SPEC_VAR", deserialized.Environment.Keys);
        Assert.Equal("another-spec-value", deserialized.Environment["ANOTHER_SPEC_VAR"]);
        Assert.DoesNotContain("NOT_FROM_SPEC_VAR", deserialized.Environment.Keys);
    }

    [Fact]
    public void ConvertResourceToJson_NonAsciiContent_IsNotEscaped()
    {
        // Arrange
        const string japaneseName = "テストリソース"; // "Test resource"
        const string japaneseDisplayName = "日本語の表示名"; // "Japanese display name"
        const string japaneseEnvValue = "これは環境変数です"; // "This is an environment variable"

        var resource = ModelTestHelpers.CreateResource(
            resourceName: japaneseName,
            displayName: japaneseDisplayName,
            resourceType: "Container",
            state: KnownResourceState.Running,
            environment: [new EnvironmentVariableViewModel("JAPANESE_VAR", japaneseEnvValue, fromSpec: true)]);

        // Act
        var json = TelemetryExportService.ConvertResourceToJson(resource, [resource]);

        // Assert - Verify Japanese characters appear directly in JSON (not Unicode-escaped)
        Assert.Contains(japaneseName, json);
        Assert.Contains(japaneseDisplayName, json);
        Assert.Contains(japaneseEnvValue, json);

        // Verify content is preserved after round-trip deserialization
        var deserialized = JsonSerializer.Deserialize(json, ResourceJsonSerializerContext.Default.ResourceJson);
        Assert.NotNull(deserialized);
        Assert.Equal(japaneseName, deserialized.Name);
        Assert.Equal(japaneseDisplayName, deserialized.DisplayName);

        Assert.NotNull(deserialized.Environment);
        Assert.Single(deserialized.Environment);
        Assert.Equal(japaneseEnvValue, deserialized.Environment["JAPANESE_VAR"]);
    }
}
