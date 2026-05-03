// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
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

public class TelemetryTracesCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;
    [Fact]
    public async Task TelemetryTracesCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel traces");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task TelemetryTracesCommand_WithDevLocalhostDashboardApiUrl_UsesLocalhost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var requestedHosts = new List<string>();
        var resourcesJson = JsonSerializer.Serialize(
            new ResourceInfoJson[] { new() { Name = "frontend", InstanceId = null } },
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var resourceSpans = new OtlpResourceSpansJson[]
        {
            new()
            {
                Resource = TelemetryTestHelper.CreateOtlpResource("frontend", null),
                ScopeSpans =
                [
                    new OtlpScopeSpansJson
                    {
                        Spans =
                        [
                            new OtlpSpanJson
                            {
                                TraceId = "abc1234567890def",
                                SpanId = "span001",
                                Name = "GET /home",
                                StartTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime),
                                EndTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime.AddMilliseconds(50)),
                                Status = new OtlpSpanStatusJson { Code = 1 }
                            }
                        ]
                    }
                ]
            }
        };
        var tracesResponse = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = resourceSpans },
            TotalCount = 1,
            ReturnedCount = 1
        };
        var tracesJson = JsonSerializer.Serialize(tracesResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = true,
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TestAppHost", "TestAppHost.csproj"),
                ProcessId = 1234
            },
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "https://nextapp1.dev.localhost:64876",
                ApiToken = "test-token",
                DashboardUrls = ["https://nextapp1.dev.localhost:64876/login?t=test"],
                IsHealthy = true
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var handler = new MockHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.Host);

            return request.RequestUri.AbsolutePath switch
            {
                "/api/telemetry/resources" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesJson, System.Text.Encoding.UTF8, "application/json")
                },
                "/api/telemetry/traces" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tracesJson, System.Text.Encoding.UTF8, "application/json")
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel traces --format json -n 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.All(requestedHosts, host => Assert.Equal("localhost", host));

        var jsonLine = outputWriter.Logs.Single(l => l.TrimStart().StartsWith('['));
        var items = JsonNode.Parse(jsonLine)!.AsArray();
        Assert.Single(items);

        var item = items[0]!;
        Assert.Equal("abc1234567890def", item["traceId"]!.GetValue<string>());

        var dashboardUrl = item["dashboardUrl"]!.GetValue<string>();
        Assert.Equal("https://nextapp1.dev.localhost:64876/traces/detail/abc1234567890def", dashboardUrl);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task TelemetryTracesCommand_WithInvalidLimitValue_ReturnsInvalidCommand(int limitValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

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
        using var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
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
        using var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
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

    [Fact]
    public async Task TelemetryTracesCommand_WithDashboardUrl_FetchesTracesDirectly()
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
            if (url.Contains("/api/telemetry/traces"))
            {
                var json = BuildTracesJson(("abc1234567890def", "frontend", null, "span001", s_testTime, s_testTime.AddMilliseconds(50), false));
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
        var result = command.Parse("otel traces --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        var dataRows = ParseTableDataRows(outputWriter.Logs);
        Assert.Single(dataRows);
        Assert.Contains("frontend", dataRows[0][1]);
    }

    [Fact]
    public async Task TelemetryTracesCommand_WithDashboardUrlAndAppHost_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel traces --dashboard-url http://localhost:18888 --apphost TestAppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(TelemetryCommandStrings.DashboardUrlAndAppHostExclusive, errorMessage);
    }

    [Fact]
    public async Task TelemetryTracesCommand_WithDashboardUrl_401_DisplaysAuthFailedMessage()
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
        var result = command.Parse("otel traces --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(TelemetryCommandStrings.DashboardAuthFailed, errorMessage);
    }

    [Fact]
    public async Task TelemetryTracesCommand_JsonOutput_ProducesExpectedJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);

        var resourceSpans = new OtlpResourceSpansJson[]
        {
            new()
            {
                Resource = TelemetryTestHelper.CreateOtlpResource("frontend", null),
                ScopeSpans =
                [
                    new OtlpScopeSpansJson
                    {
                        Spans =
                        [
                            new OtlpSpanJson
                            {
                                TraceId = "abc1234567890def",
                                SpanId = "span0017890abcde",
                                Name = "GET /home",
                                Kind = 2,
                                StartTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime),
                                EndTimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime.AddMilliseconds(50)),
                                Status = new OtlpSpanStatusJson { Code = 1 },
                                Attributes =
                                [
                                    new OtlpKeyValueJson { Key = "http.method", Value = new OtlpAnyValueJson { StringValue = "GET" } }
                                ]
                            }
                        ]
                    }
                ]
            }
        };
        var apiResponse = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = resourceSpans },
            TotalCount = 1,
            ReturnedCount = 1
        };
        var responseJson = JsonSerializer.Serialize(apiResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

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
            if (url.Contains("/api/telemetry/traces"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
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
        var result = command.Parse("otel traces --format json --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLine = outputWriter.Logs.Single(l => l.TrimStart().StartsWith('['));
        var formattedJson = TelemetryTestHelper.FormatJson(jsonLine);

        var expected = """
            [
              {
                "traceId": "abc1234567890def",
                "durationMs": 50,
                "title": "GET /home",
                "spans": [
                  {
                    "traceId": "abc1234567890def",
                    "spanId": "span0017890abcde",
                    "kind": "Server",
                    "name": "GET /home",
                    "status": "Ok",
                    "source": "frontend",
                    "durationMs": 50,
                    "timestamp": "1970-01-01T00:00:00Z",
                    "attributes": {
                      "http.method": "GET"
                    }
                  }
                ],
                "hasError": false,
                "timestamp": "1970-01-01T00:00:00Z",
                "dashboardUrl": "http://localhost:18888/traces/detail/abc1234567890def"
              }
            ]
            """;

        Assert.Equal(expected, formattedJson, ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task TelemetryTracesCommand_JsonOutput_WithTraceIdAndNoTrace_ReturnsEmptyArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);

        var apiResponse = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = [] },
            TotalCount = 0,
            ReturnedCount = 0
        };
        var responseJson = JsonSerializer.Serialize(apiResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

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
            if (url.Contains("/api/telemetry/traces/abc1234567890def"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
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
        var result = command.Parse("otel traces --trace-id abc1234567890def --format json --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLine = outputWriter.Logs.Single(l => l.TrimStart().StartsWith('['));
        Assert.Equal("[]", jsonLine);
    }
}
