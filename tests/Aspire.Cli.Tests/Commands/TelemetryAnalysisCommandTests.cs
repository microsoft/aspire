// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class TelemetryAnalysisCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;

    [Fact]
    public async Task TelemetrySummaryCommand_WithDashboardUrl_ReturnsJsonSummary()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var responseJson = BuildTelemetryApiResponse(
            CreateResourceSpans("frontend", null,
                CreateSpan("trace1", "span1", "GET /frontend", 0, 100)),
            CreateResourceSpans("backend", null,
                CreateSpan("trace1", "span2", "GET /backend", 10, 60, parentSpanId: "span1"),
                CreateSpan("trace2", "span3", "GET /error", 200, 250, hasError: true)));

        using var provider = CreateDashboardProvider(workspace, interactionService, responseJson);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel summary --dashboard-url http://localhost:18888 --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var (json, consoleOutput) = Assert.Single(interactionService.DisplayedRawText);
        Assert.Equal(ConsoleOutput.Standard, consoleOutput);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("resourceCount").GetInt32());
        Assert.Equal(2, root.GetProperty("traceCount").GetInt32());
        Assert.Equal(3, root.GetProperty("spanCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorTraceCount").GetInt32());
        Assert.Equal(1, root.GetProperty("errorSpanCount").GetInt32());
        Assert.Equal(75, root.GetProperty("averageDurationMs").GetDouble());
        Assert.Equal(100, root.GetProperty("p95DurationMs").GetDouble());
    }

    [Fact]
    public async Task TelemetrySlowTracesCommand_WithArchive_StitchesTraceAcrossResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var archivePath = CreateTelemetryArchive(workspace,
            ("traces/frontend.json", CreateTelemetryData(CreateResourceSpans("frontend", null,
                CreateSpan("trace1", "span1", "GET /frontend", 0, 100)))),
            ("traces/backend.json", CreateTelemetryData(CreateResourceSpans("backend", null,
                CreateSpan("trace1", "span2", "GET /backend", 20, 80, parentSpanId: "span1")))));

        using var provider = CreateArchiveProvider(workspace, interactionService);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"otel top-traces --file \"{archivePath}\" --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var (json, _) = Assert.Single(interactionService.DisplayedRawText);

        using var document = JsonDocument.Parse(json);
        var trace = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("trace1", trace.GetProperty("traceId").GetString());
        Assert.Equal("GET /frontend", trace.GetProperty("name").GetString());
        Assert.Equal("frontend", trace.GetProperty("resource").GetString());
        Assert.Equal(2, trace.GetProperty("spanCount").GetInt32());
        Assert.Equal(100, trace.GetProperty("durationMs").GetDouble());
    }

    [Fact]
    public async Task TelemetryWallTimeCommand_WithArchive_ReportsWallClockWorkAndGaps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var archivePath = CreateTelemetryArchive(workspace,
            ("traces/frontend.json", CreateTelemetryData(CreateResourceSpans("frontend", null,
                CreateSpan("trace-with-gap", "span1", "GET /frontend", 0, 40),
                CreateSpan("trace-with-gap", "span2", "GET /database", 160, 200, parentSpanId: "span1"),
                CreateSpan("trace-overlap", "span3", "GET /frontend", 0, 100)))),
            ("traces/backend.json", CreateTelemetryData(CreateResourceSpans("backend", null,
                CreateSpan("trace-overlap", "span4", "GET /backend", 20, 80, parentSpanId: "span3")))));

        using var provider = CreateArchiveProvider(workspace, interactionService);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"otel wall-time --file \"{archivePath}\" --format json --top 2");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var (json, _) = Assert.Single(interactionService.DisplayedRawText);

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);

        Assert.Equal("trace-with-gap", rows[0].GetProperty("traceId").GetString());
        Assert.Equal(200, rows[0].GetProperty("wallClockMs").GetDouble());
        Assert.Equal(80, rows[0].GetProperty("spanSumMs").GetDouble());
        Assert.Equal(80, rows[0].GetProperty("coveredMs").GetDouble());
        Assert.Equal(120, rows[0].GetProperty("gapMs").GetDouble());
        Assert.Equal(0, rows[0].GetProperty("overlapMs").GetDouble());
        Assert.Equal(0.4, rows[0].GetProperty("spanSumToWallRatio").GetDouble());

        Assert.Equal("trace-overlap", rows[1].GetProperty("traceId").GetString());
        Assert.Equal(100, rows[1].GetProperty("wallClockMs").GetDouble());
        Assert.Equal(160, rows[1].GetProperty("spanSumMs").GetDouble());
        Assert.Equal(100, rows[1].GetProperty("coveredMs").GetDouble());
        Assert.Equal(0, rows[1].GetProperty("gapMs").GetDouble());
        Assert.Equal(60, rows[1].GetProperty("overlapMs").GetDouble());
        Assert.Equal(1.6, rows[1].GetProperty("spanSumToWallRatio").GetDouble());
    }

    [Fact]
    public async Task TelemetrySpanStatsCommand_WithArchive_GroupsByResource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var archivePath = CreateTelemetryArchive(workspace,
            ("traces/frontend.json", CreateTelemetryData(CreateResourceSpans("frontend", null,
                CreateSpan("trace1", "span1", "GET /frontend", 0, 100)))),
            ("traces/backend.json", CreateTelemetryData(CreateResourceSpans("backend", null,
                CreateSpan("trace1", "span2", "GET /backend", 20, 80, parentSpanId: "span1")))));

        using var provider = CreateArchiveProvider(workspace, interactionService);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"otel span-stats --file \"{archivePath}\" --group-by resource --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var (json, _) = Assert.Single(interactionService.DisplayedRawText);

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("frontend", rows[0].GetProperty("group").GetString());
        Assert.Equal(100, rows[0].GetProperty("totalDurationMs").GetDouble());
        Assert.Equal("backend", rows[1].GetProperty("group").GetString());
        Assert.Equal(60, rows[1].GetProperty("totalDurationMs").GetDouble());
    }

    private ServiceProvider CreateDashboardProvider(TemporaryWorkspace workspace, TestInteractionService interactionService, string tracesJson)
    {
        var resources =
            """
            [
              { "name": "backend" },
              { "name": "frontend" }
            ]
            """;
        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resources, System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("/api/telemetry/traces", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tracesJson, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        return services.BuildServiceProvider();
    }

    private ServiceProvider CreateArchiveProvider(TemporaryWorkspace workspace, TestInteractionService interactionService)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });

        return services.BuildServiceProvider();
    }

    private static string CreateTelemetryArchive(TemporaryWorkspace workspace, params (string EntryName, OtlpTelemetryDataJson Data)[] entries)
    {
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "telemetry.zip");
        using var fileStream = File.Create(path);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        foreach (var (entryName, data) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            JsonSerializer.Serialize(entryStream, data, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        }

        return path;
    }

    private static string BuildTelemetryApiResponse(params OtlpResourceSpansJson[] resourceSpans)
    {
        var response = new TelemetryApiResponse
        {
            Data = CreateTelemetryData(resourceSpans),
            TotalCount = resourceSpans.SelectMany(rs => rs.ScopeSpans ?? []).SelectMany(ss => ss.Spans ?? []).Select(s => s.TraceId).Distinct().Count(),
            ReturnedCount = resourceSpans.Length
        };

        return JsonSerializer.Serialize(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
    }

    private static OtlpTelemetryDataJson CreateTelemetryData(params OtlpResourceSpansJson[] resourceSpans)
    {
        return new OtlpTelemetryDataJson
        {
            ResourceSpans = resourceSpans
        };
    }

    private static OtlpResourceSpansJson CreateResourceSpans(string serviceName, string? instanceId, params OtlpSpanJson[] spans)
    {
        return new OtlpResourceSpansJson
        {
            Resource = TelemetryTestHelper.CreateOtlpResource(serviceName, instanceId),
            ScopeSpans =
            [
                new OtlpScopeSpansJson
                {
                    Spans = spans
                }
            ]
        };
    }

    private static OtlpSpanJson CreateSpan(
        string traceId,
        string spanId,
        string name,
        int startMs,
        int endMs,
        bool hasError = false,
        string? parentSpanId = null)
    {
        return new OtlpSpanJson
        {
            TraceId = traceId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            Name = name,
            StartTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime.AddMilliseconds(startMs)),
            EndTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime.AddMilliseconds(endMs)),
            Status = new OtlpSpanStatusJson { Code = hasError ? 2 : 1 }
        };
    }
}
