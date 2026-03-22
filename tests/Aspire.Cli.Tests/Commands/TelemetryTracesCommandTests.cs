// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class TelemetryTracesCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;
    [Fact]
    public async Task TelemetryTracesCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel traces");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task TelemetryTracesCommand_WithInvalidLimitValue_ReturnsInvalidCommand(int limitValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"telemetry traces --limit {limitValue}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task TelemetryTracesCommand_TableOutput_ResolvesUniqueResourceNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
            resources:
            [
                new ResourceInfoJson { Name = "frontend", InstanceId = null },
                new ResourceInfoJson { Name = "backend", InstanceId = null },
            ],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/traces"] = BuildTracesJson(
                ("abc1234567890def", "frontend", null, "span001", s_testTime, s_testTime.AddMilliseconds(50), false),
                ("def9876543210abc", "backend", null, "span002", s_testTime.AddMilliseconds(10), s_testTime.AddMilliseconds(30), false))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel traces");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Parse table data rows for assertion
        var dataRows = ParseTableDataRows(outputWriter.Logs);

        // Traces are sorted oldest first: trace1 (s_testTime) first, trace2 (s_testTime+10ms) second
        Assert.Equal(2, dataRows.Length);

        Assert.Equal(FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime), dataRows[0][0]);
        Assert.Equal("frontend: GET /frontend abc1234", dataRows[0][1]);
        Assert.Equal("1", dataRows[0][2]);
        Assert.Equal("50ms", dataRows[0][3]);
        Assert.Equal("OK", dataRows[0][4]);

        Assert.Equal(FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime.AddMilliseconds(10)), dataRows[1][0]);
        Assert.Equal("backend: GET /backend def9876", dataRows[1][1]);
        Assert.Equal("1", dataRows[1][2]);
        Assert.Equal("20ms", dataRows[1][3]);
        Assert.Equal("OK", dataRows[1][4]);

        // Check summary line
        var summaryLine = outputWriter.Logs.Last(l => l.StartsWith("Showing", StringComparison.Ordinal));
        Assert.Equal("Showing 2 of 2 traces", summaryLine);
    }

    [Fact]
    public async Task TelemetryTracesCommand_TableOutput_ResolvesReplicaResourceNames()
    {
        var guid1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var guid2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
            resources:
            [
                new ResourceInfoJson { Name = "apiservice", InstanceId = guid1.ToString() },
                new ResourceInfoJson { Name = "apiservice", InstanceId = guid2.ToString() },
            ],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/traces"] = BuildTracesJson(
                ("abc1234567890def", "apiservice", guid1.ToString(), "span001", s_testTime, s_testTime.AddMilliseconds(75), false),
                ("def9876543210abc", "apiservice", guid2.ToString(), "span002", s_testTime.AddMilliseconds(10), s_testTime.AddMilliseconds(60), true))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel traces");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Parse table data rows for assertion
        var dataRows = ParseTableDataRows(outputWriter.Logs);

        // Traces sorted oldest first: trace1 (s_testTime) first, trace2 (s_testTime+10ms) second
        Assert.Equal(2, dataRows.Length);

        Assert.Equal(FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime), dataRows[0][0]);
        Assert.Equal("apiservice-11111111: GET /apiservice abc1234", dataRows[0][1]);
        Assert.Equal("1", dataRows[0][2]);
        Assert.Equal("75ms", dataRows[0][3]);
        Assert.Equal("OK", dataRows[0][4]);

        Assert.Equal(FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime.AddMilliseconds(10)), dataRows[1][0]);
        Assert.Equal("apiservice-aaaaaaaa: GET /apiservice def9876", dataRows[1][1]);
        Assert.Equal("1", dataRows[1][2]);
        Assert.Equal("50ms", dataRows[1][3]);
        Assert.Equal("ERR", dataRows[1][4]);

        // Check summary line
        var summaryLine = outputWriter.Logs.Last(l => l.StartsWith("Showing", StringComparison.Ordinal));
        Assert.Equal("Showing 2 of 2 traces", summaryLine);
    }

    /// <summary>
    /// Parses table rows from Spectre Console output, splitting by │ borders.
    /// Returns cell values for each data row (excludes header and border rows).
    /// </summary>
    private static string[][] ParseTableDataRows(IReadOnlyList<string> outputLines)
    {
        // Find rows with │ separator, then skip the first one which is the header row.
        // Border rows contain ─ characters and are also filtered out.
        return outputLines
            .Where(line => line.Contains('│') && !line.Contains('─') && !line.Contains('┬') && !line.Contains('┼') && !line.Contains('┴'))
            .Skip(1) // Skip header row
            .Select(line => line.Split('│', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(cells => cells.Length >= 5)
            .ToArray();
    }

    private static string BuildTracesJson(params (string traceId, string serviceName, string? instanceId, string spanId, DateTime startTime, DateTime endTime, bool hasError)[] entries)
    {
        var resourceSpans = entries
            .GroupBy(e => (e.serviceName, e.instanceId))
            .Select(g => new OtlpResourceSpansJson
            {
                Resource = TelemetryTestHelper.CreateOtlpResource(g.Key.serviceName, g.Key.instanceId),
                ScopeSpans =
                [
                    new OtlpScopeSpansJson
                    {
                        Spans = g.Select(e => new OtlpSpanJson
                        {
                            TraceId = e.traceId,
                            SpanId = e.spanId,
                            Name = $"GET /{e.serviceName}",
                            StartTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(e.startTime),
                            EndTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(e.endTime),
                            Status = e.hasError ? new OtlpSpanStatusJson { Code = 2 } : new OtlpSpanStatusJson { Code = 1 }
                        }).ToArray()
                    }
                ]
            }).ToArray();

        var response = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = resourceSpans },
            TotalCount = entries.Length,
            ReturnedCount = entries.Length
        };

        return JsonSerializer.Serialize(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
    }
}
