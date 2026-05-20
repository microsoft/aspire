// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests;

public class TelemetryApiServiceTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FollowSpansAsync_StreamsAllSpans()
    {
        var repository = CreateRepository();
        AddSpans(repository, count: 5);

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivedItems = new List<string>();
        await foreach (var item in service.FollowSpansAsync(null, null, null, null, cancellationToken: cts.Token))
        {
            receivedItems.Add(item);
            if (receivedItems.Count >= 5)
            {
                break;
            }
        }

        Assert.Equal(5, receivedItems.Count);
    }

    [Fact]
    public async Task FollowLogsAsync_StreamsAllLogs()
    {
        var repository = CreateRepository();
        AddLogs(repository, ["log1", "log2", "log3", "log4", "log5"]);

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivedItems = new List<string>();
        await foreach (var item in service.FollowLogsAsync(null, null, null, null, cts.Token))
        {
            receivedItems.Add(item);
            if (receivedItems.Count >= 5)
            {
                break;
            }
        }

        Assert.Equal(5, receivedItems.Count);
    }

    [Theory]
    [InlineData(false, "ok-span", "error-span")]
    [InlineData(true, "error-span", "ok-span")]
    public void GetSpans_HasErrorFilter_ReturnsExpectedSpans(bool hasError, string expectedSpan, string excludedSpan)
    {
        var repository = CreateRepository();
        AddSpansWithStatus(repository);

        var service = CreateService(repository);

        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: hasError, limit: null);

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains(expectedSpan, json);
        Assert.DoesNotContain(excludedSpan, json);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(null, 2)]
    public void GetTraces_HasErrorFilter_ReturnsExpectedTraces(bool? hasError, int expectedCount)
    {
        var repository = CreateRepository();
        AddTracesWithStatus(repository);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: hasError, limit: null);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.ReturnedCount);
    }

    [Fact]
    public async Task FollowSpansAsync_WithInvalidResourceName_ReturnsNoSpans()
    {
        var repository = CreateRepository();
        AddSpans(repository, count: 1);

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var receivedItems = new List<string>();
        try
        {
            await foreach (var item in service.FollowSpansAsync(["nonexistent-service"], null, null, null, cancellationToken: cts.Token))
            {
                receivedItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Empty(receivedItems);
    }

    [Fact]
    public async Task FollowLogsAsync_WithInvalidResourceName_ReturnsNoLogs()
    {
        var repository = CreateRepository();
        AddLogs(repository, ["log1"]);

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var receivedItems = new List<string>();
        try
        {
            await foreach (var item in service.FollowLogsAsync(["nonexistent-service"], null, null, null, cts.Token))
            {
                receivedItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Empty(receivedItems);
    }

    [Theory]
    [InlineData("747261636531", true)] // full hex trace ID
    [InlineData("7472616", true)] // shortened (7 char) prefix
    [InlineData("747261", false)] // too short
    [InlineData("nonexistent", false)]
    public void GetTrace_VariousTraceIds_ReturnsExpectedResult(string lookupId, bool expectFound)
    {
        var repository = CreateRepository();
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        AddSpansToRepository(repository, [
            CreateSpan(traceId: traceId, spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1))
        ]);

        var service = CreateService(repository);

        var result = service.GetTrace(lookupId);

        if (expectFound)
        {
            Assert.NotNull(result);
            Assert.Equal(1, result.ReturnedCount);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData("747261636531", 1)]
    [InlineData("7472616", 1)]
    [InlineData("747261", 0)]
    public void GetSpans_WithTraceIdFilter_MatchesShortenedIds(string traceIdFilter, int expectedCount)
    {
        var repository = CreateRepository();
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        AddSpansToRepository(repository, [
            CreateSpan(traceId: traceId, spanId: "matching-span", startTime: s_testTime, endTime: s_testTime.AddMinutes(1)),
            CreateSpan(traceId: "other-trace", spanId: "other-span", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3))
        ]);

        var service = CreateService(repository);

        var result = service.GetSpans(resourceNames: null, traceId: traceIdFilter, hasError: null, limit: null);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        if (expectedCount > 0)
        {
            Assert.Contains("matching-span", json);
        }
        else
        {
            Assert.DoesNotContain("matching-span", json);
        }
        Assert.DoesNotContain("other-span", json);
    }

    [Fact]
    public async Task FollowSpansAsync_WithTraceIdFilter_MatchesShortenedIds()
    {
        var repository = CreateRepository();
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        AddSpansToRepository(repository, [
            CreateSpan(traceId: traceId, spanId: "matching-span", startTime: s_testTime, endTime: s_testTime.AddMinutes(1)),
            CreateSpan(traceId: "other-trace", spanId: "other-span", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3))
        ]);

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var receivedItems = new List<string>();
        await foreach (var streamedItem in service.FollowSpansAsync(null, "7472616", null, null, cancellationToken: cts.Token))
        {
            receivedItems.Add(streamedItem);
            break;
        }

        var receivedItem = Assert.Single(receivedItems);
        Assert.Contains("matching-span", receivedItem);
        Assert.DoesNotContain("other-span", receivedItem);
    }

    [Fact]
    public void GetTrace_WithMinimumDurationMs_FiltersShortSpans()
    {
        var repository = CreateRepository();
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        AddSpansToRepository(repository, [
            CreateSpan(traceId: traceId, spanId: "short-span", startTime: s_testTime, endTime: s_testTime.AddMilliseconds(49)),
            CreateSpan(traceId: traceId, spanId: "long-span", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(1).AddMilliseconds(50))
        ]);

        var service = CreateService(repository);

        var result = service.GetTrace("747261636531", minDurationMs: 50);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("short-span", json);
        Assert.Contains("long-span", json);
    }

    [Fact]
    public void GetSpans_WithLimit_ReturnsMostRecentSpans()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "old-span", startTime: s_testTime, endTime: s_testTime.AddMinutes(1)),
            CreateSpan(traceId: "trace2", spanId: "mid-span", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3)),
            CreateSpan(traceId: "trace3", spanId: "new-span", startTime: s_testTime.AddMinutes(4), endTime: s_testTime.AddMinutes(5))
        ]);

        var service = CreateService(repository);

        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: null, limit: 2);

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("old-span", json);
        Assert.Contains("mid-span", json);
        Assert.Contains("new-span", json);
    }

    [Fact]
    public void GetSpans_WithMinimumDurationMs_FiltersShortSpans()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "short-span", startTime: s_testTime, endTime: s_testTime.AddMilliseconds(49)),
            CreateSpan(traceId: "trace2", spanId: "threshold-span", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(1).AddMilliseconds(50)),
            CreateSpan(traceId: "trace3", spanId: "long-span", startTime: s_testTime.AddSeconds(2), endTime: s_testTime.AddSeconds(2).AddMilliseconds(75))
        ]);

        var service = CreateService(repository);

        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: null, limit: null, minDurationMs: 50);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("short-span", json);
        Assert.Contains("threshold-span", json);
        Assert.Contains("long-span", json);
    }

    [Fact]
    public void GetTraces_WithLimit_ReturnsMostRecentTraces()
    {
        var repository = CreateRepository();
        AddSpans(repository, count: 3, startMinuteSpacing: 10);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: null, limit: 2);

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("span1", json);
        Assert.Contains("span2", json);
        Assert.Contains("span3", json);
    }

    [Fact]
    public void GetTraces_WithMinimumDurationMs_FiltersShortSpans()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "short-trace", spanId: "short-trace-span", startTime: s_testTime, endTime: s_testTime.AddMilliseconds(49))
        ]);
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "mixed-trace", spanId: "mixed-short-span", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(1).AddMilliseconds(49)),
            CreateSpan(traceId: "mixed-trace", spanId: "mixed-long-span", startTime: s_testTime.AddSeconds(2), endTime: s_testTime.AddSeconds(2).AddMilliseconds(50))
        ]);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: null, limit: null, minDurationMs: 50);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("short-trace-span", json);
        Assert.DoesNotContain("mixed-short-span", json);
        Assert.Contains("mixed-long-span", json);
    }

    [Fact]
    public void GetTraces_WithHasErrorAndMinimumDurationMs_FiltersDisplayedSpansAfterTraceSelection()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(
                traceId: "mixed-trace",
                spanId: "short-error-span",
                startTime: s_testTime,
                endTime: s_testTime.AddMilliseconds(49),
                status: new Status { Code = Status.Types.StatusCode.Error }),
            CreateSpan(
                traceId: "mixed-trace",
                spanId: "long-ok-span",
                startTime: s_testTime.AddSeconds(1),
                endTime: s_testTime.AddSeconds(1).AddMilliseconds(50),
                status: new Status { Code = Status.Types.StatusCode.Ok })
        ]);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: true, limit: null, minDurationMs: 50);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("short-error-span", json);
        Assert.Contains("long-ok-span", json);
    }

    [Fact]
    public void GetLogs_WithLimit_ReturnsMostRecentLogs()
    {
        var repository = CreateRepository();
        AddLogs(repository, ["old-log", "mid-log", "new-log"]);

        var service = CreateService(repository);

        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: 2);

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("old-log", json);
        Assert.Contains("mid-log", json);
        Assert.Contains("new-log", json);
    }

    [Fact]
    public void GetLogs_LargeLimit_ReturnsAllLogs()
    {
        const int totalLogs = 20_000;
        var repository = CreateRepository(maxLogCount: totalLogs);

        var logRecords = new RepeatedField<LogRecord>();
        for (var i = 0; i < totalLogs; i++)
        {
            logRecords.Add(CreateLogRecord(time: s_testTime.AddMilliseconds(i), message: $"log{i}", severity: SeverityNumber.Info));
        }

        AddLogsToRepository(repository, logRecords);

        var service = CreateService(repository);

        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: 100_000);

        Assert.NotNull(result);
        Assert.Equal(totalLogs, result.TotalCount);
        Assert.Equal(totalLogs, result.ReturnedCount);
    }

    [Theory]
    [InlineData("Connection", 2)]
    [InlineData("nonexistent", 0)]
    public void GetLogs_WithSearch_FiltersLogsByMessage(string search, int expectedCount)
    {
        var repository = CreateRepository();
        AddLogs(repository, ["Connection established", "Request received", "Connection closed"]);

        var service = CreateService(repository);

        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: null, search: search);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.ReturnedCount);
    }

    [Fact]
    public void GetLogs_WithSearch_IsCaseInsensitive()
    {
        var repository = CreateRepository();
        AddLogs(repository, ["UPPERCASE warning detected"]);
        AddLogs(repository, ["Normal log"]);

        var service = CreateService(repository);

        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: null, search: "uppercase warning");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
    }

    [Fact]
    public void GetLogs_WithSearch_MatchesAttributes()
    {
        var repository = CreateRepository();
        AddLogsToRepository(repository, [
            CreateLogRecord(time: s_testTime, message: "log1", severity: SeverityNumber.Info,
                attributes: [new KeyValuePair<string, string>("http.url", "/api/products")]),
            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "log2", severity: SeverityNumber.Info,
                attributes: [new KeyValuePair<string, string>("http.url", "/api/orders")])
        ]);

        var service = CreateService(repository);

        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: null, search: "products");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
    }

    [Theory]
    [InlineData("span1", 1)]
    [InlineData("products", 1)]
    public void GetSpans_WithSearch_FiltersSpans(string search, int expectedCount)
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1),
                attributes: [new KeyValuePair<string, string>("http.url", "/api/products")]),
            CreateSpan(traceId: "trace2", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3),
                attributes: [new KeyValuePair<string, string>("http.url", "/api/orders")])
        ]);

        var service = CreateService(repository);

        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: null, limit: null, search: search);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.ReturnedCount);
    }

    [Theory]
    [InlineData("span1", 1)]
    [InlineData("nonexistent-xyz", 0)]
    public void GetTraces_WithSearch_FiltersTraces(string search, int expectedCount)
    {
        var repository = CreateRepository();

        // Each trace needs a separate AddTraces call to get distinct trace IDs in the repository
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1))
        ]);
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace2", spanId: "span2", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(11))
        ]);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: null, limit: null, search: search);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.ReturnedCount);

        if (expectedCount > 0)
        {
            var allResult = service.GetTraces(resourceNames: null, hasError: null, limit: null);
            Assert.NotNull(allResult);
            Assert.Equal(2, allResult.ReturnedCount);
        }
    }

    /// <summary>
    /// Adds spans with sequential trace/span IDs to the repository. Each span is added in a separate
    /// AddTraces call so that it gets its own trace entry.
    /// </summary>
    private static void AddSpans(TelemetryRepository repository, int count, int startMinuteSpacing = 1)
    {
        for (var i = 1; i <= count; i++)
        {
            AddSpansToRepository(repository, [
                CreateSpan(traceId: $"trace{i}", spanId: $"span{i}", startTime: s_testTime.AddMinutes(i * startMinuteSpacing), endTime: s_testTime.AddMinutes(i * startMinuteSpacing + 1))
            ]);
        }
    }

    /// <summary>
    /// Adds a batch of spans (as raw Span objects) to the repository under a single resource.
    /// </summary>
    private static void AddSpansToRepository(TelemetryRepository repository, IEnumerable<Span> spans)
    {
        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { spans }
                    }
                }
            }
        });
    }

    /// <summary>
    /// Adds one OK span and one Error span to the repository for hasError filter tests.
    /// </summary>
    private static void AddSpansWithStatus(TelemetryRepository repository)
    {
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "ok-span", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), status: new Status { Code = Status.Types.StatusCode.Ok }),
            CreateSpan(traceId: "trace2", spanId: "error-span", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Error })
        ]);
    }

    /// <summary>
    /// Adds two traces (separate trace IDs) with OK and Error status for hasError filter tests.
    /// </summary>
    private static void AddTracesWithStatus(TelemetryRepository repository)
    {
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "ok-trace", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), status: new Status { Code = Status.Types.StatusCode.Ok })
        ]);
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "error-trace", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Error })
        ]);
    }

    /// <summary>
    /// Adds log entries with the specified messages to the repository.
    /// </summary>
    private static void AddLogs(TelemetryRepository repository, string[] messages, SeverityNumber severity = SeverityNumber.Info)
    {
        var logRecords = new RepeatedField<LogRecord>();
        for (var i = 0; i < messages.Length; i++)
        {
            logRecords.Add(CreateLogRecord(time: s_testTime.AddMinutes(i), message: messages[i], severity: severity));
        }

        AddLogsToRepository(repository, logRecords);
    }

    /// <summary>
    /// Adds a batch of raw LogRecord objects to the repository under a single resource.
    /// </summary>
    private static void AddLogsToRepository(TelemetryRepository repository, RepeatedField<LogRecord> logRecords)
    {
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { logRecords }
                    }
                }
            }
        });
    }

    private static TelemetryApiService CreateService(
        TelemetryRepository? repository = null,
        IOutgoingPeerResolver[]? peerResolvers = null)
    {
        return new TelemetryApiService(
            repository ?? CreateRepository(),
            peerResolvers ?? []);
    }
}
