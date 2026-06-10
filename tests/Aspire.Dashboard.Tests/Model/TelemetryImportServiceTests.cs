// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Otlp.Serialization;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.Model;

public sealed class TelemetryImportServiceTests
{
    private static readonly DateTime s_testTime = new(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

    private static TelemetryImportService CreateImportService(TelemetryRepository repository, bool disableImport = false)
    {
        var options = new DashboardOptions { UI = new UIOptions { DisableImport = disableImport } };
        var optionsMonitor = new TestOptionsMonitor<DashboardOptions>(options);
        return new TelemetryImportService(repository, optionsMonitor, NullLogger<TelemetryImportService>.Instance);
    }

    [Fact]
    public async Task ImportAsync_WhenDisabled_ThrowsInvalidOperationException()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository, disableImport: true);

        var logsJson = CreateLogsJson("TestService", "instance-1", "Test log message");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(logsJson));

        // Act & Assert
        Assert.False(service.IsImportEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("logs.json", stream, CancellationToken.None));
    }

    [Fact]
    public async Task ImportAsync_JsonFile_WithLogs_ImportsSuccessfully()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        // Create log data
        var logsJson = CreateLogsJson("TestService", "instance-1", "Test log message");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(logsJson));

        // Act
        await service.ImportAsync("logs.json", stream, CancellationToken.None);

        // Assert
        var resources = repository.GetResources();
        Assert.Single(resources);
        Assert.Equal("TestService", resources[0].ResourceName);

        var logs = repository.GetLogs(GetLogsContext.ForResourceKey(resources[0].ResourceKey));

        Assert.Single(logs.Items);
        Assert.Equal("Test log message", logs.Items[0].Message);
    }

    [Fact]
    public async Task ImportAsync_JsonFile_WithTraces_ImportsSuccessfully()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        // Create trace data
        var tracesJson = CreateTracesJson("TestService", "instance-1", "TestOperation");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(tracesJson));

        // Act
        await service.ImportAsync("traces.json", stream, CancellationToken.None);

        // Assert
        var resources = repository.GetResources();
        Assert.Single(resources);

        var traces = repository.GetTraces(GetTracesRequest.ForResourceKey(resources[0].ResourceKey));

        Assert.Single(traces.PagedResult.Items);
    }

    [Fact]
    public async Task ImportAsync_JsonFile_WithMetrics_ImportsSuccessfully()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        // Create metrics data
        var metricsJson = CreateMetricsJson("TestService", "instance-1", "test.metric");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(metricsJson));

        // Act
        await service.ImportAsync("metrics.json", stream, CancellationToken.None);

        // Assert
        var resources = repository.GetResources();
        Assert.Single(resources);

        var instruments = resources[0].GetInstrumentsSummary();
        Assert.Single(instruments);
        Assert.Equal("test.metric", instruments[0].Name);
    }

    [Fact]
    public async Task ImportAsync_ZipFile_WithMultipleJsonFiles_ImportsAll()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        // Create a zip file with logs and traces JSON
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var logsEntry = archive.CreateEntry("logs.json");
            using (var entryStream = logsEntry.Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write(CreateLogsJson("LogService", "log-instance", "Log message"));
            }

            var tracesEntry = archive.CreateEntry("traces.json");
            using (var entryStream = tracesEntry.Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write(CreateTracesJson("TraceService", "trace-instance", "Trace operation"));
            }
        }

        zipStream.Position = 0;

        // Act
        await service.ImportAsync("telemetry.zip", zipStream, CancellationToken.None);

        // Assert
        var resources = repository.GetResources();
        Assert.Equal(2, resources.Count);

        var logResource = resources.FirstOrDefault(r => r.ResourceName == "LogService");
        Assert.NotNull(logResource);

        var traceResource = resources.FirstOrDefault(r => r.ResourceName == "TraceService");
        Assert.NotNull(traceResource);
    }

    [Fact]
    public async Task ImportAsync_ZipFile_IgnoresNonJsonFiles()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        // Create a zip file with a txt file and a json file
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var txtEntry = archive.CreateEntry("console.txt");
            using (var entryStream = txtEntry.Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write("Some console output");
            }

            var logsEntry = archive.CreateEntry("logs.json");
            using (var entryStream = logsEntry.Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write(CreateLogsJson("TestService", "instance-1", "Test message"));
            }
        }

        zipStream.Position = 0;

        // Act
        await service.ImportAsync("telemetry.zip", zipStream, CancellationToken.None);

        // Assert
        var resources = repository.GetResources();
        Assert.Single(resources);
    }

    [Fact]
    public async Task ImportAsync_TxtFile_IsIgnored()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("Some console output"));

        // Act
        await service.ImportAsync("console.txt", stream, CancellationToken.None);

        // Assert
        var resources = repository.GetResources();
        Assert.Empty(resources);
    }

    [Fact]
    public async Task ImportAsync_EmptyJsonFile_HandlesGracefully()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        // Act
        await service.ImportAsync("empty.json", stream, CancellationToken.None);

        // Assert - should not throw, no resources added
        var resources = repository.GetResources();
        Assert.Empty(resources);
    }

    [Fact]
    public async Task ImportAsync_InvalidJson_HandlesGracefully()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("{ invalid json }"));

        // Act
        await service.ImportAsync("invalid.json", stream, CancellationToken.None);

        // Assert - should not throw, no resources added
        var resources = repository.GetResources();
        Assert.Empty(resources);
    }

    [Fact]
    public async Task ImportAsync_UnsupportedExtension_HandlesGracefully()
    {
        // Arrange
        var repository = CreateRepository();
        var service = CreateImportService(repository);

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("some content"));

        // Act
        await service.ImportAsync("file.xml", stream, CancellationToken.None);

        // Assert - should not throw, no resources added
        var resources = repository.GetResources();
        Assert.Empty(resources);
    }

    [Fact]
    public async Task ImportAsync_RoundTrip_LogsExportAndImport_PreservesData()
    {
        // Arrange
        var sourceRepository = CreateRepository();
        var addContext = new AddContext();

        sourceRepository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "RoundTripService", instanceId: "round-trip-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Round trip test", severity: OpenTelemetry.Proto.Logs.V1.SeverityNumber.Warn) }
                    }
                }
            }
        });

        var resources = sourceRepository.GetResources();
        var logs = sourceRepository.GetLogs(GetLogsContext.ForResourceKey(resources[0].ResourceKey));

        // Export
        var exportedJson = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);
        var jsonString = JsonSerializer.Serialize(exportedJson, OtlpJsonSerializerContext.DefaultOptions);

        // Import
        var targetRepository = CreateRepository();
        var importService = CreateImportService(targetRepository);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));

        // Act
        await importService.ImportAsync("logs.json", stream, CancellationToken.None);

        // Assert
        var importedResources = targetRepository.GetResources();
        Assert.Single(importedResources);
        Assert.Equal("RoundTripService", importedResources[0].ResourceName);
        Assert.Equal("round-trip-1", importedResources[0].InstanceId);

        var importedLogs = targetRepository.GetLogs(GetLogsContext.ForResourceKey(importedResources[0].ResourceKey));

        Assert.Single(importedLogs.Items);
        Assert.Equal("Round trip test", importedLogs.Items[0].Message);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, importedLogs.Items[0].Severity);
    }

    [Fact]
    public async Task ImportAsync_RoundTrip_LogsWithAspireLogId_FiltersOutAttribute()
    {
        // Arrange - Export logs (which adds aspire.log_id attribute)
        var sourceRepository = CreateRepository();
        var addContext = new AddContext();

        sourceRepository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "TestService", instanceId: "test-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime, message: "Test message", attributes: [new KeyValuePair<string, string>("custom.attr", "custom-value")]) }
                    }
                }
            }
        });

        var resources = sourceRepository.GetResources();
        var logs = sourceRepository.GetLogs(GetLogsContext.ForResourceKey(resources[0].ResourceKey));
        var originalInternalId = logs.Items[0].InternalId;

        // Export adds aspire.log_id attribute
        var exportedJson = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);

        // Verify aspire.log_id was added during export
        var exportedLogRecord = exportedJson.ResourceLogs![0].ScopeLogs![0].LogRecords![0];
        Assert.Contains(exportedLogRecord.Attributes!, a => a.Key == OtlpHelpers.AspireLogIdAttribute && a.Value?.StringValue == originalInternalId.ToString(CultureInfo.InvariantCulture));

        var jsonString = JsonSerializer.Serialize(exportedJson, OtlpJsonSerializerContext.DefaultOptions);

        // Import
        var targetRepository = CreateRepository();
        var importService = CreateImportService(targetRepository);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));

        // Act
        await importService.ImportAsync("logs.json", stream, CancellationToken.None);

        // Assert
        var importedResources = targetRepository.GetResources();
        Assert.Single(importedResources);

        var importedLogs = targetRepository.GetLogs(GetLogsContext.ForResourceKey(importedResources[0].ResourceKey));
        Assert.Single(importedLogs.Items);

        // Verify aspire.log_id is NOT in the imported log's attributes (it should be filtered out)
        Assert.DoesNotContain(importedLogs.Items[0].Attributes, a => a.Key == OtlpHelpers.AspireLogIdAttribute);

        // Verify the imported log gets a new InternalId (not the original one)
        Assert.NotEqual(originalInternalId, importedLogs.Items[0].InternalId);

        // Verify other attributes are preserved
        Assert.Contains(importedLogs.Items[0].Attributes, a => a.Key == "custom.attr" && a.Value == "custom-value");
    }

    [Fact]
    public async Task ImportAsync_RoundTrip_TracesExportAndImport_PreservesData()
    {
        // Arrange
        var sourceRepository = CreateRepository();
        var addContext = new AddContext();

        sourceRepository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "TraceRoundTrip", instanceId: "trace-round-trip-1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("TestTracer"),
                        Spans = { CreateSpan(traceId: "trace123456789012", spanId: "span1234", startTime: s_testTime, endTime: s_testTime.AddSeconds(1), kind: Span.Types.SpanKind.Server) }
                    }
                }
            }
        });

        var resources = sourceRepository.GetResources();
        var traces = sourceRepository.GetTraces(GetTracesRequest.ForResourceKey(resources[0].ResourceKey));

        // Export
        var exportedJson = TelemetryExportService.ConvertTracesToOtlpJson(traces.PagedResult.Items, []);
        var jsonString = JsonSerializer.Serialize(exportedJson, OtlpJsonSerializerContext.DefaultOptions);

        // Import
        var targetRepository = CreateRepository();
        var importService = CreateImportService(targetRepository);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonString));

        // Act
        await importService.ImportAsync("traces.json", stream, CancellationToken.None);

        // Assert
        var importedResources = targetRepository.GetResources();
        Assert.Single(importedResources);
        Assert.Equal("TraceRoundTrip", importedResources[0].ResourceName);

        var importedTraces = targetRepository.GetTraces(GetTracesRequest.ForResourceKey(importedResources[0].ResourceKey));

        Assert.Single(importedTraces.PagedResult.Items);
    }

    private static string CreateLogsJson(string serviceName, string instanceId, string message)
    {
        var timeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime);
        var telemetryData = new OtlpTelemetryDataJson
        {
            ResourceLogs =
            [
                new OtlpResourceLogsJson
                {
                    Resource = new OtlpResourceJson
                    {
                        Attributes =
                        [
                            new OtlpKeyValueJson { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } },
                            new OtlpKeyValueJson { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = instanceId } }
                        ]
                    },
                    ScopeLogs =
                    [
                        new OtlpScopeLogsJson
                        {
                            Scope = new OtlpInstrumentationScopeJson { Name = "TestScope" },
                            LogRecords =
                            [
                                new OtlpLogRecordJson
                                {
                                    TimeUnixNano = timeUnixNano,
                                    SeverityNumber = (int)SeverityNumber.Info,
                                    SeverityText = "Information",
                                    Body = new OtlpAnyValueJson { StringValue = message }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        return JsonSerializer.Serialize(telemetryData, OtlpJsonSerializerContext.IndentedOptions);
    }

    private static string CreateTracesJson(string serviceName, string instanceId, string operationName)
    {
        var timeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime);
        var endTimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime.AddSeconds(1));
        var traceId = "0102030405060708090a0b0c0d0e0f10";
        var spanId = "0102030405060708";

        var telemetryData = new OtlpTelemetryDataJson
        {
            ResourceSpans =
            [
                new OtlpResourceSpansJson
                {
                    Resource = new OtlpResourceJson
                    {
                        Attributes =
                        [
                            new OtlpKeyValueJson { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } },
                            new OtlpKeyValueJson { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = instanceId } }
                        ]
                    },
                    ScopeSpans =
                    [
                        new OtlpScopeSpansJson
                        {
                            Scope = new OtlpInstrumentationScopeJson { Name = "TestScope" },
                            Spans =
                            [
                                new OtlpSpanJson
                                {
                                    TraceId = traceId,
                                    SpanId = spanId,
                                    Name = operationName,
                                    Kind = (int)Span.Types.SpanKind.Server,
                                    StartTimeUnixNano = timeUnixNano,
                                    EndTimeUnixNano = endTimeUnixNano,
                                    Status = new OtlpSpanStatusJson()
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        return JsonSerializer.Serialize(telemetryData, OtlpJsonSerializerContext.IndentedOptions);
    }

    private static string CreateMetricsJson(string serviceName, string instanceId, string metricName)
    {
        var timeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(s_testTime);

        var telemetryData = new OtlpTelemetryDataJson
        {
            ResourceMetrics =
            [
                new OtlpResourceMetricsJson
                {
                    Resource = new OtlpResourceJson
                    {
                        Attributes =
                        [
                            new OtlpKeyValueJson { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } },
                            new OtlpKeyValueJson { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = instanceId } }
                        ]
                    },
                    ScopeMetrics =
                    [
                        new OtlpScopeMetricsJson
                        {
                            Scope = new OtlpInstrumentationScopeJson { Name = "TestScope" },
                            Metrics =
                            [
                                new OtlpMetricJson
                                {
                                    Name = metricName,
                                    Description = "Test metric",
                                    Unit = "count",
                                    Gauge = new OtlpGaugeJson
                                    {
                                        DataPoints =
                                        [
                                            new OtlpNumberDataPointJson
                                            {
                                                TimeUnixNano = timeUnixNano,
                                                AsInt = 42
                                            }
                                        ]
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        return JsonSerializer.Serialize(telemetryData, OtlpJsonSerializerContext.IndentedOptions);
    }
}
