// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Otlp.Serialization;
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
    public void GetTrace_ReturnsAllSpansForTrace()
    {
        var repository = CreateRepository();
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        AddSpansToRepository(repository, [
            CreateSpan(traceId: traceId, spanId: "short-span", startTime: s_testTime, endTime: s_testTime.AddMilliseconds(49)),
            CreateSpan(traceId: traceId, spanId: "long-span", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(1).AddMilliseconds(50))
        ]);

        var service = CreateService(repository);

        var result = service.GetTrace("747261636531");

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);
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
    public void GetTraces_WithLimitAndDurationSearchFilter_ReturnsMostRecentMatchingTraces()
    {
        var repository = CreateRepository();
        AddSpans(repository, count: 3, startMinuteSpacing: 10);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: null, limit: 2, search: "duration:>=50");

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.ReturnedCount);

        var spanIds = GetAllSpans(result).Select(s => DecodeSpanId(s.SpanId)).ToList();
        Assert.Equal(2, spanIds.Count);
        Assert.Contains("span2", spanIds);
        Assert.Contains("span3", spanIds);
        Assert.DoesNotContain("span1", spanIds);
    }

    [Fact]
    public void GetTraces_WithDurationSearchFilter_FiltersShortSpans()
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

        var result = service.GetTraces(resourceNames: null, hasError: null, limit: null, search: "duration:>=50");

        Assert.NotNull(result);
        // The trace with short-trace-span (49ms) is excluded because no span matches the filter.
        // The mixed-trace is included because mixed-long-span (50ms) matches, and all its spans are returned.
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);

        var spans = GetAllSpans(result);
        Assert.Equal(2, spans.Count);
        Assert.Contains(spans, s => DecodeSpanId(s.SpanId) == "mixed-short-span");
        Assert.Contains(spans, s => DecodeSpanId(s.SpanId) == "mixed-long-span");
    }

    [Fact]
    public void GetTraces_WithHasErrorAndDurationSearchFilter_ReturnsAllSpansFromMatchingTraces()
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

        var result = service.GetTraces(resourceNames: null, hasError: true, limit: null, search: "duration:>=50");

        Assert.NotNull(result);
        // The trace matches hasError because it has an error span.
        // The duration filter selects the trace because long-ok-span (50ms) matches.
        // All spans from the matching trace are returned.
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ReturnedCount);

        var spans = GetAllSpans(result);
        Assert.Equal(2, spans.Count);
        Assert.Contains(spans, s => DecodeSpanId(s.SpanId) == "short-error-span");
        Assert.Contains(spans, s => DecodeSpanId(s.SpanId) == "long-ok-span");
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

    [Fact]
    public void GetSpans_WithAttributeFilter_FiltersSpans()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1),
                attributes: [new KeyValuePair<string, string>("http.method", "GET")]),
            CreateSpan(traceId: "trace1", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3),
                attributes: [new KeyValuePair<string, string>("http.method", "POST")])
        ]);

        var service = CreateService(repository);

        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: null, limit: null, search: "@http.method:GET");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);

        var spans = GetAllSpans(result);
        Assert.Single(spans);
        Assert.Equal("span1", DecodeSpanId(spans[0].SpanId));
    }

    [Fact]
    public void GetTraces_WithAttributeFilter_FiltersTraces()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1),
                attributes: [new KeyValuePair<string, string>("http.method", "GET")])
        ]);
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace2", spanId: "span2", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(11),
                attributes: [new KeyValuePair<string, string>("http.method", "POST")])
        ]);

        var service = CreateService(repository);

        var result = service.GetTraces(resourceNames: null, hasError: null, limit: null, search: "@http.method:POST");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);

        var spanIds = GetAllSpans(result).Select(s => DecodeSpanId(s.SpanId)).ToList();
        Assert.Contains("span2", spanIds);
        Assert.DoesNotContain("span1", spanIds);
    }

    [Fact]
    public void GetLogs_WithAttributeFilter_FiltersLogs()
    {
        var repository = CreateRepository();
        AddLogsToRepository(repository, [
            CreateLogRecord(time: s_testTime, message: "log1", severity: SeverityNumber.Info,
                attributes: [new KeyValuePair<string, string>("http.method", "GET")]),
            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "log2", severity: SeverityNumber.Info,
                attributes: [new KeyValuePair<string, string>("http.method", "POST")])
        ]);

        var service = CreateService(repository);

        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: null, search: "@http.method:GET");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
    }

    [Fact]
    public void GetSpans_WithDurationRangeFilter_ReturnsSpansInRange()
    {
        var repository = CreateRepository();
        AddSpansToRepository(repository, [
            CreateSpan(traceId: "trace1", spanId: "short-span", startTime: s_testTime, endTime: s_testTime.AddMilliseconds(30)),
            CreateSpan(traceId: "trace1", spanId: "mid-span", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(1).AddMilliseconds(75)),
            CreateSpan(traceId: "trace1", spanId: "long-span", startTime: s_testTime.AddSeconds(2), endTime: s_testTime.AddSeconds(2).AddMilliseconds(200))
        ]);

        var service = CreateService(repository);

        // Filter for spans with duration > 50ms AND < 100ms (only mid-span at 75ms matches)
        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: null, limit: null, search: "duration:>50 duration:<100");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);

        var spans = GetAllSpans(result);
        Assert.Single(spans);
        Assert.Equal("mid-span", DecodeSpanId(spans[0].SpanId));
    }

    [Fact]
    public void GetLogs_WithUrlSearch_MatchesExactScheme()
    {
        var repository = CreateRepository();
        AddLogs(repository, [
            "Request to http://www.contoso.com/api completed",
            "Request to https://www.contoso.com/api completed",
            "No URL in this message"
        ]);

        var service = CreateService(repository);

        // The entire URL should be treated as a text fragment, not parsed as a qualifier
        var result = service.GetLogs(resourceNames: null, traceId: null, severity: null, limit: null, search: "http://www.contoso.com");

        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
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

    private static List<OtlpSpanJson> GetAllSpans(TelemetryApiResponse result)
    {
        // These tests care about which OTLP spans are returned, not the complete JSON
        // serialization shape. Assert over the structured response model so a formatting
        // change can't hide a filtering regression or create snapshot churn.
        return result.Data?.ResourceSpans?
            .SelectMany(rs => rs.ScopeSpans ?? [])
            .SelectMany(ss => ss.Spans ?? [])
            .ToList() ?? [];
    }

    // SpanId is serialized as lowercase hex per the OTLP/JSON spec
    // (see https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding), and our
    // CreateSpan test helper stores the friendly identifier as the raw UTF-8 bytes of
    // the SpanId. Decode the hex back to text so assertions can compare against the
    // original identifier the test supplied.
    private static string DecodeSpanId(string? hexSpanId)
    {
        Assert.NotNull(hexSpanId);
        return Encoding.UTF8.GetString(Convert.FromHexString(hexSpanId));
    }
}
