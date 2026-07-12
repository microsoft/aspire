// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Aspire.Otlp.Serialization;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using DashboardProto = global::Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Tests.Integration;

public class DeckApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task GetConfig_ReturnsDeckConfigContract()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, applicationName: "StressApp")));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var response = await httpClient.GetAsync("/api/deck/config").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync().DefaultTimeout());
        var expected = new JsonObject
        {
            ["applicationName"] = "StressApp",
            ["resourceServiceUrl"] = null,
            ["otlpGrpcUrl"] = null,
            ["otlpHttpUrl"] = null,
            ["version"] = VersionHelpers.DashboardDisplayVersion ?? string.Empty
        };

        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Fact]
    public async Task GetResources_ReturnsDeckResourceContract()
    {
        var resource = CreateResource();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, initialResources: [resource])));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var response = await httpClient.GetAsync("/api/deck/resources").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync().DefaultTimeout());
        var expected = JsonNode.Parse(
            """
            [
              {
                "name": "frontend-abc123",
                "resourceType": "Project",
                "displayName": "frontend",
                "uid": "resource-uid",
                "state": "Running",
                "stateStyle": "success",
                "health": "Healthy",
                "createdAt": "2026-07-10T08:00:00Z",
                "startedAt": "2026-07-10T08:00:01Z",
                "stoppedAt": null,
                "urls": [
                  {
                    "name": "https",
                    "url": "https://localhost:7443/",
                    "isInternal": false,
                    "isInactive": false,
                    "displayName": "HTTPS",
                    "sortOrder": 10
                  }
                ],
                "properties": [
                  {
                    "name": "connectionString",
                    "displayName": "Connection string",
                    "value": "Server=database",
                    "isSensitive": true,
                    "isHighlighted": true,
                    "sortOrder": 20
                  }
                ],
                "environment": [
                  {
                    "name": "API_KEY",
                    "value": "secret",
                    "isFromSpec": true
                  }
                ],
                "healthReports": [
                  {
                    "status": "Healthy",
                    "key": "ready",
                    "description": "Ready to serve traffic."
                  }
                ],
                "commands": [
                  {
                    "name": "restart",
                    "displayName": "Restart",
                    "displayDescription": "Restart this resource.",
                    "confirmationMessage": "Restart frontend?",
                    "iconName": "ArrowClockwise",
                    "iconVariant": "regular",
                    "isHighlighted": true,
                    "state": "enabled"
                  }
                ],
                "relationships": [
                  {
                    "resourceName": "database",
                    "type": "Reference"
                  }
                ],
                "isHidden": true,
                "supportsDetailedTelemetry": true,
                "iconName": "Code",
                "iconVariant": "filled"
              }
            ]
            """);

        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Fact]
    public async Task GetResources_BrowserTokenAuthWithoutCookie_RedirectsToLogin()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "TestKey123!";
        });
        await app.StartAsync().DefaultTimeout();

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}")
        };
        var response = await httpClient.GetAsync("/api/deck/resources").DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?returnUrl=%2Fapi%2Fdeck%2Fresources", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task PostExecuteCommand_ExecutesResolvedCommandInteractively()
    {
        var resource = CreateResource();
        string? actualResourceName = null;
        string? actualResourceType = null;
        CommandViewModel? actualCommand = null;
        ExecuteResourceCommandOptions? actualOptions = null;
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(
                    isEnabled: true,
                    initialResources: [resource],
                    executeResourceCommand: (resourceName, resourceType, command, options, _) =>
                    {
                        actualResourceName = resourceName;
                        actualResourceType = resourceType;
                        actualCommand = command;
                        actualOptions = options;
                        return Task.FromResult(new ResourceCommandResponseViewModel
                        {
                            Kind = ResourceCommandResponseKind.Succeeded,
                            Message = "Restarted."
                        });
                    })));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var content = new StringContent(
            """
            {"resourceName":"frontend-abc123","commandName":"restart"}
            """,
            Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/api/deck/commands/execute", content).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(resource.Name, actualResourceName);
        Assert.Equal(resource.ResourceType, actualResourceType);
        Assert.Equal("restart", actualCommand?.Name);
        Assert.False(actualOptions?.NonInteractive);
        Assert.Null(actualOptions?.Arguments);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync().DefaultTimeout());
        var expected = JsonNode.Parse(
            """
            {"kind":"succeeded","message":"Restarted."}
            """);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Theory]
    [InlineData("missing-resource", "restart")]
    [InlineData("frontend-abc123", "missing-command")]
    public async Task PostExecuteCommand_UnknownResourceOrCommand_ReturnsNotFound(string resourceName, string commandName)
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, initialResources: [CreateResource()])));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var content = new StringContent(
            $$"""
            {"resourceName":"{{resourceName}}","commandName":"{{commandName}}"}
            """,
            Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/api/deck/commands/execute", content).DefaultTimeout();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetConsoleLogs_StreamsBacklogAndLiveBatches()
    {
        var consoleLogs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        string? requestedResourceName = null;
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(
                    isEnabled: true,
                    initialResources: [CreateResource()],
                    consoleLogsChannelProvider: resourceName =>
                    {
                        requestedResourceName = resourceName;
                        return consoleLogs;
                    })));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/deck/resources/frontend-abc123/console-logs");
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));

        await consoleLogs.Writer.WriteAsync(
            [
                new ResourceLogLine(12, "\u001b[32mListening on https://localhost:7443\u001b[39m", IsErrorMessage: false),
                new ResourceLogLine(13, "Connection failed", IsErrorMessage: true)
            ],
            cancellationTokenSource.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationTokenSource.Token);
        using var reader = new StreamReader(stream);
        var responseLine = await reader.ReadLineAsync(cancellationTokenSource.Token)
            ?? throw new InvalidOperationException("The console stream ended before emitting a batch.");
        Assert.Equal("frontend-abc123", requestedResourceName);
        var actual = JsonNode.Parse(responseLine);
        var expected = JsonNode.Parse(
            """
            {
              "resourceName": "frontend-abc123",
              "lines": [
                {
                  "lineNumber": 12,
                  "text": "Listening on https://localhost:7443",
                  "isStdErr": false
                },
                {
                  "lineNumber": 13,
                  "text": "Connection failed",
                  "isStdErr": true
                }
              ]
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
        consoleLogs.Writer.Complete();
    }

    [Fact]
    public async Task GetConsoleLogs_UnknownResource_ReturnsNotFound()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, initialResources: [CreateResource()])));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var response = await httpClient.GetAsync("/api/deck/resources/missing/console-logs").DefaultTimeout();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStructuredLogs_ReturnsFilteredSnapshot()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddStructuredLog(
            repository,
            time: new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            message: "Frontend started",
            severity: SeverityNumber.Info);
        AddStructuredLog(
            repository,
            time: new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            message: "Database connection failed",
            severity: SeverityNumber.Error);

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var response = await httpClient.GetAsync(
            "/api/deck/telemetry/logs?resource=frontend&severity=Warning&search=database&limit=10").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var snapshot = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.TotalCount);
        Assert.Equal(1, snapshot.ReturnedCount);

        var resourceLogs = Assert.Single(snapshot.Data?.ResourceLogs ?? []);
        Assert.Equal("frontend", resourceLogs.Resource?.GetServiceName());
        Assert.Equal("instance-1", resourceLogs.Resource?.GetServiceInstanceId());
        var scopeLogs = Assert.Single(resourceLogs.ScopeLogs ?? []);
        Assert.Equal("Stress.Telemetry", scopeLogs.Scope?.Name);
        var log = Assert.Single(scopeLogs.LogRecords ?? []);
        Assert.Equal("Database connection failed", log.Body?.StringValue);
        Assert.Equal((int)SeverityNumber.Error, log.SeverityNumber);
        Assert.Equal("Error", log.SeverityText);
        Assert.Equal("30313233343536373839616263646566", log.TraceId);
        Assert.Equal("7370616e2d303031", log.SpanId);
    }

    [Fact]
    public async Task GetStructuredLogs_FollowStreamsBacklogAndLiveRecords()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddStructuredLog(
            repository,
            time: new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            message: "Backlog record",
            severity: SeverityNumber.Info);

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await httpClient.GetAsync(
            "/api/deck/telemetry/logs?resource=frontend&follow=true",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationTokenSource.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal(
            "Backlog record",
            GetStructuredLogMessage(await reader.ReadLineAsync(cancellationTokenSource.Token)));

        AddStructuredLog(
            repository,
            time: new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            message: "Live record",
            severity: SeverityNumber.Warn);

        Assert.Equal(
            "Live record",
            GetStructuredLogMessage(await reader.ReadLineAsync(cancellationTokenSource.Token)));
    }

    [Fact]
    public async Task GetSpans_ReturnsSnapshot()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddSpan(
            repository,
            startTime: new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            spanId: "span-001");
        AddSpan(
            repository,
            startTime: new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            spanId: "span-002",
            parentSpanId: "span-001");

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var response = await httpClient.GetAsync(
            "/api/deck/telemetry/spans?resource=frontend&limit=10").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var snapshot = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.TotalCount);
        Assert.Equal(2, snapshot.ReturnedCount);

        var resourceSpans = Assert.Single(snapshot.Data?.ResourceSpans ?? []);
        Assert.Equal("frontend", resourceSpans.Resource?.GetServiceName());
        Assert.Equal("instance-1", resourceSpans.Resource?.GetServiceInstanceId());
        var scopeSpans = Assert.Single(resourceSpans.ScopeSpans ?? []);
        Assert.Equal("Stress.Telemetry", scopeSpans.Scope?.Name);
        Assert.Collection(
            scopeSpans.Spans ?? [],
            span =>
            {
                Assert.Equal("7370616e2d303031", span.SpanId);
                Assert.Null(span.ParentSpanId);
            },
            span =>
            {
                Assert.Equal("7370616e2d303032", span.SpanId);
                Assert.Equal("7370616e2d303031", span.ParentSpanId);
            });
    }

    [Fact]
    public async Task GetSpans_FollowStreamsBacklogAndLiveRecords()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddSpan(
            repository,
            startTime: new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            spanId: "span-001");

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await httpClient.GetAsync(
            "/api/deck/telemetry/spans?resource=frontend&follow=true",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationTokenSource.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal(
            "Test span. Id: span-001",
            GetSpanName(await reader.ReadLineAsync(cancellationTokenSource.Token)));

        AddSpan(
            repository,
            startTime: new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            spanId: "span-002");

        Assert.Equal(
            "Test span. Id: span-002",
            GetSpanName(await reader.ReadLineAsync(cancellationTokenSource.Token)));
    }

    [Fact]
    public async Task DeleteStructuredLogs_ClearsSelectedOrAllResources()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddStructuredLog(
            repository,
            time: new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            message: "Frontend record",
            severity: SeverityNumber.Info);
        AddStructuredLog(
            repository,
            time: new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            message: "Backend record",
            severity: SeverityNumber.Warn,
            resourceName: "backend");

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        using var missingResponse = await httpClient.DeleteAsync("/api/deck/telemetry/logs?resource=missing").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        using var selectedResponse = await httpClient.DeleteAsync("/api/deck/telemetry/logs?resource=frontend").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NoContent, selectedResponse.StatusCode);

        using var selectedSnapshotResponse = await httpClient.GetAsync("/api/deck/telemetry/logs").DefaultTimeout();
        var selectedSnapshot = await selectedSnapshotResponse.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(selectedSnapshot);
        Assert.Equal(1, selectedSnapshot.TotalCount);
        Assert.Equal("backend", Assert.Single(selectedSnapshot.Data?.ResourceLogs ?? []).Resource?.GetServiceName());

        using var allResponse = await httpClient.DeleteAsync("/api/deck/telemetry/logs").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NoContent, allResponse.StatusCode);

        using var emptySnapshotResponse = await httpClient.GetAsync("/api/deck/telemetry/logs").DefaultTimeout();
        var emptySnapshot = await emptySnapshotResponse.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(emptySnapshot);
        Assert.Equal(0, emptySnapshot.TotalCount);
        Assert.Empty(emptySnapshot.Data?.ResourceLogs ?? []);
    }

    [Fact]
    public async Task DeleteSpans_ClearsSelectedOrAllResources()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddSpan(
            repository,
            startTime: new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            spanId: "span-frontend",
            resourceName: "frontend");
        AddSpan(
            repository,
            startTime: new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            spanId: "span-backend",
            traceId: "fedcba9876543210",
            resourceName: "backend");

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        using var missingResponse = await httpClient.DeleteAsync("/api/deck/telemetry/spans?resource=missing").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        using var selectedResponse = await httpClient.DeleteAsync("/api/deck/telemetry/spans?resource=frontend").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NoContent, selectedResponse.StatusCode);

        using var selectedSnapshotResponse = await httpClient.GetAsync("/api/deck/telemetry/spans").DefaultTimeout();
        var selectedSnapshot = await selectedSnapshotResponse.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(selectedSnapshot);
        Assert.Equal(1, selectedSnapshot.TotalCount);
        Assert.Equal("backend", Assert.Single(selectedSnapshot.Data?.ResourceSpans ?? []).Resource?.GetServiceName());

        using var allResponse = await httpClient.DeleteAsync("/api/deck/telemetry/spans").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NoContent, allResponse.StatusCode);

        using var emptySnapshotResponse = await httpClient.GetAsync("/api/deck/telemetry/spans").DefaultTimeout();
        var emptySnapshot = await emptySnapshotResponse.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(emptySnapshot);
        Assert.Equal(0, emptySnapshot.TotalCount);
        Assert.Empty(emptySnapshot.Data?.ResourceSpans ?? []);
    }

    [Fact]
    public async Task DeleteMetrics_ClearsSelectedOrAllResources()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        AddMetric(repository, "frontend.requests", "frontend");
        AddMetric(repository, "backend.jobs", "backend");

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        using var missingResponse = await httpClient.DeleteAsync("/api/deck/telemetry/metrics?resource=missing").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        using var selectedResponse = await httpClient.DeleteAsync("/api/deck/telemetry/metrics?resource=frontend").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NoContent, selectedResponse.StatusCode);
        Assert.Empty(repository.GetInstrumentsSummaries(new ResourceKey("frontend", "instance-1")));
        Assert.Equal(
            "backend.jobs",
            Assert.Single(repository.GetInstrumentsSummaries(new ResourceKey("backend", "instance-1"))).Name);

        using var allResponse = await httpClient.DeleteAsync("/api/deck/telemetry/metrics").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NoContent, allResponse.StatusCode);
        Assert.Empty(repository.GetInstrumentsSummaries(new ResourceKey("backend", "instance-1")));
    }

    [Fact]
    public async Task GetMetrics_ReturnsSummariesAndSeries()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        var repository = app.Services.GetRequiredService<TelemetryRepository>();
        var now = DateTime.UtcNow;
        AddMetric(
            repository,
            "frontend.requests",
            "frontend",
            startTime: now.AddSeconds(-2),
            value: 1,
            attributes: [KeyValuePair.Create("http.method", "GET")],
            exemplars:
            [
                new Exemplar
                {
                    TimeUnixNano = TelemetryTestHelpers.DateTimeToUnixNanoseconds(now.AddSeconds(-2)),
                    AsDouble = 1,
                    TraceId = ByteString.CopyFromUtf8("trace-id"),
                    SpanId = ByteString.CopyFromUtf8("span-id"),
                    FilteredAttributes = { new KeyValue { Key = "sampled", Value = new AnyValue { StringValue = "true" } } }
                }
            ]);
        AddMetric(
            repository,
            "frontend.requests",
            "frontend",
            startTime: now.AddSeconds(-1),
            value: 3,
            attributes: [KeyValuePair.Create("http.method", "POST")]);
        AddHistogramMetric(repository, "frontend.duration", "frontend", now.AddSeconds(-1));

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var summariesResponse = await httpClient.GetAsync("/api/deck/telemetry/metrics").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, summariesResponse.StatusCode);
        Assert.Equal("no-store", summariesResponse.Headers.CacheControl?.ToString());
        var summaries = JsonNode.Parse(await summariesResponse.Content.ReadAsStringAsync().DefaultTimeout())!.AsArray();
        var summary = Assert.IsType<JsonObject>(summaries.Single(node => (string?)node?["name"] == "frontend.requests"));
        Assert.Equal("frontend.requests", (string?)summary["name"]);
        Assert.Equal("Test metric description", (string?)summary["description"]);
        Assert.Equal("widget", (string?)summary["unit"]);
        Assert.Equal("frontend", (string?)summary["resourceName"]);
        Assert.Equal("Stress.Telemetry", (string?)summary["meterName"]);
        Assert.Equal("counter", (string?)summary["kind"]);
        Assert.Equal(3, (double?)summary["lastValue"]);
        Assert.Equal(2UL, (ulong?)summary["pointCount"]);

        using var seriesResponse = await httpClient.GetAsync(
            "/api/deck/telemetry/metrics/series?resource=frontend&meter=Stress.Telemetry&instrument=frontend.requests&windowSeconds=60&maxPoints=20").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, seriesResponse.StatusCode);
        Assert.Equal("no-store", seriesResponse.Headers.CacheControl?.ToString());
        var series = JsonNode.Parse(await seriesResponse.Content.ReadAsStringAsync().DefaultTimeout())!.AsObject();
        Assert.Equal("frontend.requests", (string?)series["name"]);
        Assert.Equal("frontend", (string?)series["resourceName"]);
        Assert.Equal("Stress.Telemetry", (string?)series["meterName"]);
        Assert.Equal("counter", (string?)series["kind"]);
        var timestamps = Assert.IsType<JsonArray>(series["timestampsMs"]);
        var values = Assert.IsType<JsonArray>(series["values"]);
        Assert.NotEmpty(timestamps);
        Assert.Equal(timestamps.Count, values.Count);
        Assert.Null(series["p50"]);
        Assert.False((bool?)series["hasOverflow"]);

        var filter = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(series["dimensionFilters"])));
        Assert.Equal("http.method", (string?)filter["name"]);
        Assert.Equal(["GET", "POST"], Assert.IsType<JsonArray>(filter["values"]).Select(static value => value!.GetValue<string>()).ToArray());

        var dimensions = Assert.IsType<JsonArray>(series["dimensions"]);
        Assert.Equal(2, dimensions.Count);
        Assert.All(dimensions, dimensionNode =>
        {
            var dimension = Assert.IsType<JsonObject>(dimensionNode);
            Assert.Single(Assert.IsType<JsonArray>(dimension["attributes"]));
            Assert.Equal(
                Assert.IsType<JsonArray>(dimension["timestampsMs"]).Count,
                Assert.IsType<JsonArray>(dimension["values"]).Count);
        });

        var exemplar = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(series["exemplars"])));
        Assert.Equal(1, (double?)exemplar["value"]);
        Assert.NotEmpty(exemplar["traceId"]!.GetValue<string>());
        Assert.NotEmpty(exemplar["spanId"]!.GetValue<string>());
        var exemplarAttribute = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(exemplar["attributes"])));
        Assert.Equal("sampled", (string?)exemplarAttribute["key"]);
        Assert.Equal("true", (string?)exemplarAttribute["value"]);

        using var filteredResponse = await httpClient.GetAsync(
            "/api/deck/telemetry/metrics/series?resource=frontend&meter=Stress.Telemetry&instrument=frontend.requests&windowSeconds=60&maxPoints=20&dimension.http.method=s%3AGET").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        var filteredSeries = JsonNode.Parse(await filteredResponse.Content.ReadAsStringAsync().DefaultTimeout())!.AsObject();
        Assert.NotEmpty(Assert.IsType<JsonArray>(filteredSeries["timestampsMs"]));

        using var noDimensionsResponse = await httpClient.GetAsync(
            "/api/deck/telemetry/metrics/series?resource=frontend&meter=Stress.Telemetry&instrument=frontend.requests&windowSeconds=60&maxPoints=20&dimension.http.method=x%3A").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, noDimensionsResponse.StatusCode);
        var noDimensionsSeries = JsonNode.Parse(await noDimensionsResponse.Content.ReadAsStringAsync().DefaultTimeout())!.AsObject();
        Assert.Empty(Assert.IsType<JsonArray>(noDimensionsSeries["timestampsMs"]));

        using var histogramCountResponse = await httpClient.GetAsync(
            "/api/deck/telemetry/metrics/series?resource=frontend&meter=Stress.Telemetry&instrument=frontend.duration&windowSeconds=60&maxPoints=20&showCount=true").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, histogramCountResponse.StatusCode);
        var histogramCountSeries = JsonNode.Parse(await histogramCountResponse.Content.ReadAsStringAsync().DefaultTimeout())!.AsObject();
        Assert.True((bool?)histogramCountSeries["showCount"]);
        Assert.NotEmpty(Assert.IsType<JsonArray>(histogramCountSeries["values"]));
        Assert.Null(histogramCountSeries["p50"]);

        using var missingResponse = await httpClient.GetAsync(
            "/api/deck/telemetry/metrics/series?resource=frontend&meter=Stress.Telemetry&instrument=missing").DefaultTimeout();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        using var invalidResponse = await httpClient.GetAsync("/api/deck/telemetry/metrics/series").DefaultTimeout();
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    [Fact]
    public async Task Interactions_RoundTripInputsDialog()
    {
        var interactions = Channel.CreateUnbounded<DashboardProto.WatchInteractionsResponseUpdate>();
        var responses = Channel.CreateUnbounded<DashboardProto.WatchInteractionsRequestUpdate>();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(
                    isEnabled: true,
                    interactionChannelProvider: () => interactions,
                    sendInteractionUpdateChannel: responses)));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var interaction = new DashboardProto.WatchInteractionsResponseUpdate
        {
            InteractionId = 42,
            Title = "Echo arguments",
            Message = "Provide command values.",
            PrimaryButtonText = "Run",
            SecondaryButtonText = "Cancel",
            ShowSecondaryButton = true,
            ShowDismiss = true,
            InputsDialog = new DashboardProto.InteractionInputsDialog()
        };
        var flavorInput = new DashboardProto.InteractionInput
        {
            Name = "flavor",
            Label = "Flavor",
            InputType = DashboardProto.InputType.Choice,
            Value = "vanilla",
            Description = "Select a flavor.",
            UpdateStateOnChange = true
        };
        flavorInput.Options.Add("vanilla", "Vanilla");
        flavorInput.Options.Add("chocolate", "Chocolate");
        interaction.InputsDialog.InputItems.Add(
            new DashboardProto.InteractionInput
            {
                Name = "message",
                Label = "Message",
                Placeholder = "Hello",
                InputType = DashboardProto.InputType.Text,
                Required = true,
                MaxLength = 80
            });
        interaction.InputsDialog.InputItems.Add(flavorInput);
        await interactions.Writer.WriteAsync(interaction).DefaultTimeout();

        var actual = await GetInteractionsAsync(httpClient, expectedCount: 1);
        var expected = JsonNode.Parse(
            """
            [
              {
                "interactionId": 42,
                "kind": "inputsDialog",
                "title": "Echo arguments",
                "message": "Provide command values.",
                "primaryButtonText": "Run",
                "secondaryButtonText": "Cancel",
                "showSecondaryButton": true,
                "showDismiss": true,
                "enableMessageMarkdown": false,
                "intent": "none",
                "inputs": [
                  {
                    "name": "message",
                    "label": "Message",
                    "placeholder": "Hello",
                    "inputType": "text",
                    "required": true,
                    "options": [],
                    "value": "",
                    "validationErrors": [],
                    "description": "",
                    "enableDescriptionMarkdown": false,
                    "maxLength": 80,
                    "allowCustomChoice": false,
                    "disabled": false,
                    "updateStateOnChange": false
                  },
                  {
                    "name": "flavor",
                    "label": "Flavor",
                    "placeholder": "",
                    "inputType": "choice",
                    "required": false,
                    "options": [
                      ["chocolate", "Chocolate"],
                      ["vanilla", "Vanilla"]
                    ],
                    "value": "vanilla",
                    "validationErrors": [],
                    "description": "Select a flavor.",
                    "enableDescriptionMarkdown": false,
                    "maxLength": 0,
                    "allowCustomChoice": false,
                    "disabled": false,
                    "updateStateOnChange": true
                  }
                ],
                "linkText": "",
                "linkUrl": ""
              }
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");

        using var content = new StringContent(
            """
            {
              "interactionId": 42,
              "action": "submit",
              "values": {
                "message": "Hello from React",
                "flavor": "chocolate"
              }
            }
            """,
            Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/api/deck/interactions/respond", content).DefaultTimeout();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var sent = await responses.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(42, sent.InteractionId);
        Assert.Equal(DashboardProto.WatchInteractionsRequestUpdate.KindOneofCase.InputsDialog, sent.KindCase);
        Assert.True(sent.HasResponseUpdate);
        Assert.False(sent.ResponseUpdate);
        Assert.Collection(
            sent.InputsDialog.InputItems,
            input =>
            {
                Assert.Equal("message", input.Name);
                Assert.Equal("Hello from React", input.Value);
            },
            input =>
            {
                Assert.Equal("flavor", input.Name);
                Assert.Equal("chocolate", input.Value);
            });
        Assert.Empty(await GetInteractionsAsync(httpClient, expectedCount: 0));
    }

    private static async Task<JsonArray> GetInteractionsAsync(HttpClient httpClient, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var response = await httpClient.GetAsync("/api/deck/interactions", cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token))!.AsArray();
            if (actual.Count == expectedCount)
            {
                return actual;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cts.Token);
        }
    }

    private static void AddStructuredLog(
        TelemetryRepository repository,
        DateTime time,
        string message,
        SeverityNumber severity,
        string resourceName = "frontend")
    {
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = TelemetryTestHelpers.CreateResource(name: resourceName, instanceId: "instance-1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = TelemetryTestHelpers.CreateScope("Stress.Telemetry"),
                        LogRecords =
                        {
                            TelemetryTestHelpers.CreateLogRecord(
                                time: time,
                                message: message,
                                severity: severity,
                                attributes: [],
                                traceId: "0123456789abcdef",
                                spanId: "span-001")
                        }
                    }
                }
            }
        });
    }

    private static string? GetStructuredLogMessage(string? json)
    {
        Assert.NotNull(json);
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        return Assert.Single(Assert.Single(data?.ResourceLogs ?? []).ScopeLogs ?? []).LogRecords?.Single().Body?.StringValue;
    }

    private static void AddSpan(
        TelemetryRepository repository,
        DateTime startTime,
        string spanId,
        string? parentSpanId = null,
        string traceId = "0123456789abcdef",
        string resourceName = "frontend")
    {
        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = TelemetryTestHelpers.CreateResource(name: resourceName, instanceId: "instance-1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = TelemetryTestHelpers.CreateScope("Stress.Telemetry"),
                        Spans =
                        {
                            TelemetryTestHelpers.CreateSpan(
                                traceId: traceId,
                                spanId: spanId,
                                startTime: startTime,
                                endTime: startTime.AddMilliseconds(50),
                                parentSpanId: parentSpanId,
                                kind: Span.Types.SpanKind.Server)
                        }
                    }
                }
            }
        });
    }

    private static void AddMetric(
        TelemetryRepository repository,
        string metricName,
        string resourceName,
        DateTime? startTime = null,
        int value = 1,
        IEnumerable<KeyValuePair<string, string>>? attributes = null,
        IEnumerable<Exemplar>? exemplars = null)
    {
        repository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = TelemetryTestHelpers.CreateResource(name: resourceName, instanceId: "instance-1"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = TelemetryTestHelpers.CreateScope("Stress.Telemetry"),
                        Metrics =
                        {
                            TelemetryTestHelpers.CreateSumMetric(
                                metricName: metricName,
                                startTime: startTime ?? new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
                                value: value,
                                attributes: attributes,
                                exemplars: exemplars)
                        }
                    }
                }
            }
        });
    }

    private static void AddHistogramMetric(
        TelemetryRepository repository,
        string metricName,
        string resourceName,
        DateTime startTime)
    {
        repository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = TelemetryTestHelpers.CreateResource(name: resourceName, instanceId: "instance-1"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = TelemetryTestHelpers.CreateScope("Stress.Telemetry"),
                        Metrics = { TelemetryTestHelpers.CreateHistogramMetric(metricName, startTime) }
                    }
                }
            }
        });
    }

    private static string? GetSpanName(string? json)
    {
        Assert.NotNull(json);
        // Each span stream item uses the OTLP shape:
        // {"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"..."}]}]}]}
        var data = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpTelemetryDataJson);
        return Assert.Single(Assert.Single(data?.ResourceSpans ?? []).ScopeSpans ?? []).Spans?.Single().Name;
    }

    private static ResourceViewModel CreateResource()
    {
        var property = new ResourcePropertyViewModel(
            name: "connectionString",
            value: Value.ForString("Server=database"),
            isValueSensitive: true,
            knownProperty: null,
            sortOrder: 20,
            displayName: "Connection string",
            isHighlighted: true);

        return new ResourceViewModel
        {
            Name = "frontend-abc123",
            ResourceType = KnownResourceTypes.Project,
            DisplayName = "frontend",
            Uid = "resource-uid",
            ReplicaIndex = 0,
            State = KnownResourceState.Running.ToString(),
            StateStyle = "success",
            CreationTimeStamp = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            StartTimeStamp = new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            StopTimeStamp = null,
            Environment = [new EnvironmentVariableViewModel("API_KEY", "secret", fromSpec: true)],
            Urls =
            [
                new UrlViewModel(
                    "https",
                    new Uri("https://localhost:7443"),
                    isInternal: false,
                    isInactive: false,
                    new UrlDisplayPropertiesViewModel("HTTPS", 10))
            ],
            Volumes = [],
            Relationships = [new RelationshipViewModel("database", "Reference")],
            Properties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty.Add(property.Name, property),
            Commands =
            [
                new CommandViewModel(
                    "restart",
                    CommandViewModelState.Enabled,
                    "Restart",
                    "Restart this resource.",
                    "Restart frontend?",
                    argumentInputs: [],
                    isHighlighted: true,
                    "ArrowClockwise",
                    IconVariant.Regular)
            ],
            HealthReports = [new HealthReportViewModel("ready", HealthStatus.Healthy, "Ready to serve traffic.", ExceptionText: null)],
            KnownState = KnownResourceState.Running,
            IsHidden = true,
            SupportsDetailedTelemetry = true,
            IconName = "Code",
            IconVariant = IconVariant.Filled
        };
    }
}
