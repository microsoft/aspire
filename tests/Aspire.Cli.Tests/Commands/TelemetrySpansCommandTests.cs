// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class TelemetrySpansCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;
    [Fact]
    public async Task TelemetrySpansCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task TelemetrySpansCommand_WithInvalidLimitValue_ReturnsInvalidCommand(int limitValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"telemetry spans --limit {limitValue}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task TelemetrySpansCommand_TableOutput_ResolvesUniqueResourceNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
            resources:
            [
                new ResourceInfoJson { Name = "frontend", InstanceId = null },
                new ResourceInfoJson { Name = "backend", InstanceId = null },
            ],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/spans"] = BuildSpansJson(
                ("frontend", null, "span001", "GET /index", s_testTime, s_testTime.AddMilliseconds(50), false),
                ("backend", null, "span002", "SELECT * FROM users", s_testTime.AddMilliseconds(10), s_testTime.AddMilliseconds(30), true))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Span output format: "timestamp STATUS shortSpanId resourceName     duration spanName"
        var spanLines = outputWriter.Logs.Where(l => l.Contains("frontend") || l.Contains("backend")).ToList();
        Assert.Equal(2, spanLines.Count);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime)} OK      50ms frontend: GET /index span001", spanLines[0]);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime.AddMilliseconds(10))} ERR     20ms backend: SELECT * FROM users span002", spanLines[1]);
    }

    [Fact]
    public async Task TelemetrySpansCommand_TableOutput_ResolvesReplicaResourceNames()
    {
        var guid1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var guid2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
            resources:
            [
                new ResourceInfoJson { Name = "apiservice", InstanceId = guid1.ToString() },
                new ResourceInfoJson { Name = "apiservice", InstanceId = guid2.ToString() },
            ],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/spans"] = BuildSpansJson(
                ("apiservice", guid1.ToString(), "span001", "GET /api/products", s_testTime, s_testTime.AddMilliseconds(75), false),
                ("apiservice", guid2.ToString(), "span002", "POST /api/orders", s_testTime.AddMilliseconds(10), s_testTime.AddMilliseconds(60), true))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Replicas get shortened GUID appended
        var spanLines = outputWriter.Logs.Where(l => l.Contains("apiservice")).ToList();
        Assert.Equal(2, spanLines.Count);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime)} OK      75ms apiservice-11111111: GET /api/products span001", spanLines[0]);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime.AddMilliseconds(10))} ERR     50ms apiservice-aaaaaaaa: POST /api/orders span002", spanLines[1]);
    }

    private static string BuildSpansJson(params (string serviceName, string? instanceId, string spanId, string name, DateTime startTime, DateTime endTime, bool hasError)[] entries)
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
                            SpanId = e.spanId,
                            TraceId = "trace001",
                            Name = e.name,
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

    [Fact]
    public async Task TelemetrySpansCommand_WithDashboardUrl_FetchesSpansDirectly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("/api/telemetry/spans"))
            {
                var json = BuildSpansJson(("frontend", null, "span001", "GET /index", s_testTime, s_testTime.AddMilliseconds(50), false));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        var spanLines = outputWriter.Logs.Where(l => l.Contains("frontend")).ToList();
        Assert.Single(spanLines);
        Assert.Contains("GET /index", spanLines[0]);
    }

    [Fact]
    public async Task TelemetrySpansCommand_WithDashboardUrlAndAppHost_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans --dashboard-url http://localhost:18888 --apphost TestAppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(TelemetryCommandStrings.DashboardUrlAndAppHostExclusive, errorMessage);
    }

    [Fact]
    public async Task TelemetrySpansCommand_WithDashboardUrl_401_DisplaysAuthFailedMessage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(TelemetryCommandStrings.DashboardAuthFailed, errorMessage);
    }

    [Fact]
    public async Task TelemetrySpansCommand_WithDashboardUrl_ResourcesEndpoint404_DisplaysApiNotEnabledMessage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/"))
            {
                // All telemetry API endpoints return 404 when API is not enabled
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            // Base URL probe returns OK (dashboard is running, just no API)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>", System.Text.Encoding.UTF8, "text/html")
            };
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel spans --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardApiNotEnabled, "http://localhost:18888"), errorMessage);
    }
}
