// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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

public class TelemetryLogsCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;
    [Fact]
    public async Task TelemetryLogsCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDevLocalhostDashboardApiUrl_UsesLocalhost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var requestedHosts = new List<string>();
        var resourcesJson = JsonSerializer.Serialize(
            new ResourceInfoJson[] { new() { Name = "redis", InstanceId = null } },
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var resourceLogs = new OtlpResourceLogsJson[]
        {
            new()
            {
                Resource = TelemetryTestHelper.CreateOtlpResource("redis", null),
                ScopeLogs =
                [
                    new OtlpScopeLogsJson
                    {
                        LogRecords =
                        [
                            new OtlpLogRecordJson
                            {
                                TimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime),
                                SeverityNumber = 9,
                                SeverityText = "Information",
                                Body = new OtlpAnyValueJson { StringValue = "Ready to accept connections" },
                                Attributes =
                                [
                                    new OtlpKeyValueJson { Key = "aspire.log_id", Value = new OtlpAnyValueJson { StringValue = "7" } }
                                ]
                            }
                        ]
                    }
                ]
            }
        };
        var logsResponse = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = resourceLogs },
            TotalCount = 1,
            ReturnedCount = 1
        };
        var logsJson = JsonSerializer.Serialize(logsResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

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
                "/api/telemetry/logs" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(logsJson, System.Text.Encoding.UTF8, "application/json")
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
        var result = command.Parse("otel logs --format json -n 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.All(requestedHosts, host => Assert.Equal("localhost", host));

        var jsonLine = outputWriter.Logs.Single(l => l.TrimStart().StartsWith('['));
        var items = JsonNode.Parse(jsonLine)!.AsArray();
        Assert.Single(items);

        var item = items[0]!;
        Assert.Equal("Ready to accept connections", item["message"]!.GetValue<string>());
        Assert.Equal("redis", item["resourceName"]!.GetValue<string>());

        var dashboardUrl = item["dashboardUrl"]!.GetValue<string>();
        Assert.Equal("https://nextapp1.dev.localhost:64876/structuredlogs?logEntryId=7", dashboardUrl);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task TelemetryLogsCommand_WithInvalidLimitValue_ReturnsInvalidCommand(int limitValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"telemetry logs --limit {limitValue}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task TelemetryLogsCommand_TableOutput_ResolvesUniqueResourceNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
            resources:
            [
                new ResourceInfoJson { Name = "redis", InstanceId = null },
                new ResourceInfoJson { Name = "apiservice", InstanceId = null },
            ],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = BuildLogsJson(
                ("redis", null, 9, "Information", "Ready to accept connections", s_testTime),
                ("apiservice", null, 9, "Information", "Request received", s_testTime.AddSeconds(1)))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // With ANSI disabled, output is plain text: "timestamp severity resourceName body"
        var logLines = outputWriter.Logs.Where(l => l.Contains("redis") || l.Contains("apiservice")).ToList();
        Assert.Equal(2, logLines.Count);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime)} INFO redis Ready to accept connections", logLines[0]);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime.AddSeconds(1))} INFO apiservice Request received", logLines[1]);
    }

    [Fact]
    public async Task TelemetryLogsCommand_TableOutput_ResolvesReplicaResourceNames()
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
                ["/api/telemetry/logs"] = BuildLogsJson(
                ("apiservice", guid1.ToString(), 9, "Information", "Hello from replica 1", s_testTime),
                ("apiservice", guid2.ToString(), 13, "Warning", "Slow response from replica 2", s_testTime.AddSeconds(1)))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // Replicas get shortened GUID appended: apiservice-11111111 and apiservice-aaaaaaaa
        var logLines = outputWriter.Logs.Where(l => l.Contains("apiservice")).ToList();
        Assert.Equal(2, logLines.Count);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime)} INFO apiservice-11111111 Hello from replica 1", logLines[0]);
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime.AddSeconds(1))} WARN apiservice-aaaaaaaa Slow response from replica 2", logLines[1]);
    }

    [Fact]
    public async Task TelemetryLogsCommand_TableOutput_StripsAnsiControlSequencesFromBody()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        using var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
            resources:
            [
                new ResourceInfoJson { Name = "apiservice", InstanceId = null },
            ],
            telemetryEndpoints: new Dictionary<string, string>
            {
                ["/api/telemetry/logs"] = BuildLogsJson(
                    ("apiservice", null, 9, "Information", "\u001b[38;5;252mRequest received\u001b[0m", s_testTime))
            });

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var logLine = Assert.Single(outputWriter.Logs, l => l.Contains("apiservice", StringComparison.Ordinal));
        Assert.Equal($"{FormatHelpers.FormatConsoleTime(TimeProvider.System, s_testTime)} INFO apiservice Request received", logLine);
        Assert.DoesNotContain("\u001b", logLine, StringComparison.Ordinal);
    }

    private static string BuildLogsJson(params (string serviceName, string? instanceId, int severityNumber, string severityText, string body, DateTime time)[] entries)
    {
        var resourceLogs = entries
            .GroupBy(e => (e.serviceName, e.instanceId))
            .Select(g => new OtlpResourceLogsJson
            {
                Resource = TelemetryTestHelper.CreateOtlpResource(g.Key.serviceName, g.Key.instanceId),
                ScopeLogs =
                [
                    new OtlpScopeLogsJson
                    {
                        LogRecords = g.Select(e => new OtlpLogRecordJson
                        {
                            TimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(e.time),
                            SeverityNumber = e.severityNumber,
                            SeverityText = e.severityText,
                            Body = new OtlpAnyValueJson { StringValue = e.body }
                        }).ToArray()
                    }
                ]
            }).ToArray();

        var response = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = resourceLogs },
            TotalCount = entries.Length,
            ReturnedCount = entries.Length
        };

        return JsonSerializer.Serialize(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrl_FetchesLogsDirectly()
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
            if (url.Contains("/api/telemetry/logs"))
            {
                var json = BuildLogsJson(("redis", null, 9, "Information", "Ready to accept connections", s_testTime));
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
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        var logLines = outputWriter.Logs.Where(l => l.Contains("redis")).ToList();
        Assert.Single(logLines);
        Assert.Contains("Ready to accept connections", logLines[0]);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrlAndApiKey_SendsApiKeyHeader()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        string? capturedApiKey = null;

        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.Headers.TryGetValues("X-API-Key", out var values))
            {
                capturedApiKey = values.FirstOrDefault();
            }
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/resources"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("/api/telemetry/logs"))
            {
                var json = BuildLogsJson(("redis", null, 9, "Information", "Connected", s_testTime));
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
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888 --api-key my-secret-key");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("my-secret-key", capturedApiKey);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrlAndAppHost_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888 --apphost TestAppHost.csproj");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(TelemetryCommandStrings.DashboardUrlAndAppHostExclusive, errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithInvalidDashboardUrl_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs --dashboard-url not-a-url");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardUrlInvalid, "not-a-url"), errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrl_401_DisplaysAuthFailedMessage()
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
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(TelemetryCommandStrings.DashboardAuthFailed, errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrl_404WithReachableBase_DisplaysApiNotEnabledMessage()
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
            if (url.Contains("/api/telemetry/"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            // Base URL probe returns OK
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardApiNotEnabled, "http://localhost:18888"), errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrl_ConnectionRefused_DisplaysConnectionFailedMessage()
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
            // Simulate connection refused (HttpRequestException with no status code)
            throw new HttpRequestException("Connection refused");
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardConnectionFailed, "http://localhost:18888"), errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithLoginUrl_ConnectionRefused_DisplaysConnectionFailedMessage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var handler = new MockHttpMessageHandler(request =>
        {
            throw new HttpRequestException("Connection refused");
        });

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888/login?t=sometoken");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardConnectionFailed, "http://localhost:18888"), errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithDashboardUrl_ResourcesEndpoint404_DisplaysApiNotEnabledMessage()
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
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardApiNotEnabled, "http://localhost:18888"), errorMessage);
    }

    [Fact]
    public async Task TelemetryLogsCommand_WithLoginUrl_NormalizesToBaseUrl()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        string? capturedBaseUrl = null;

        var handler = new MockHttpMessageHandler(request =>
        {
            capturedBaseUrl ??= request.RequestUri!.GetLeftPart(UriPartial.Authority);
            var url = request.RequestUri!.ToString();
            if (url.Contains("/api/telemetry/validateToken"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"apiKey":"test-key"}""", System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("/api/telemetry/resources"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("/api/telemetry/logs"))
            {
                var json = BuildLogsJson(("redis", null, 9, "Information", "Ready", s_testTime));
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
        // Pass a login URL — should be normalized to base URL
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888/login?t=abc123");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        // Verify the request went to the base URL (without /login path)
        Assert.NotNull(capturedBaseUrl);
        Assert.DoesNotContain("/login", capturedBaseUrl);
    }

    [Fact]
    public async Task TelemetryLogsCommand_JsonOutput_ProducesExpectedJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);

        var resourceLogs = new OtlpResourceLogsJson[]
        {
            new()
            {
                Resource = TelemetryTestHelper.CreateOtlpResource("apiservice", null),
                ScopeLogs =
                [
                    new OtlpScopeLogsJson
                    {
                        LogRecords =
                        [
                            new OtlpLogRecordJson
                            {
                                TimeUnixNano = TelemetryTestHelper.DateTimeToUnixNanoseconds(s_testTime),
                                SeverityNumber = 9,
                                SeverityText = "Information",
                                Body = new OtlpAnyValueJson { StringValue = "Request started" },
                                Attributes =
                                [
                                    new OtlpKeyValueJson { Key = "aspire.log_id", Value = new OtlpAnyValueJson { StringValue = "42" } },
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
            Data = new OtlpTelemetryDataJson { ResourceLogs = resourceLogs },
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
            if (url.Contains("/api/telemetry/logs"))
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
        var result = command.Parse("otel logs --format json --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonLine = outputWriter.Logs.Single(l => l.TrimStart().StartsWith('['));
        var formattedJson = TelemetryTestHelper.FormatJson(jsonLine);

        var expected = """
            [
              {
                "logId": 42,
                "message": "Request started",
                "severity": "Information",
                "resourceName": "apiservice",
                "attributes": {
                  "http.method": "GET"
                },
                "dashboardUrl": "http://localhost:18888/structuredlogs?logEntryId=42"
              }
            ]
            """;

        Assert.Equal(expected, formattedJson, ignoreLineEndingDifferences: true);
    }
}
