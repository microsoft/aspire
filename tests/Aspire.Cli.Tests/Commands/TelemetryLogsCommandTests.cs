// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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

public class TelemetryLogsCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTime s_testTime = TelemetryTestHelper.s_testTime;
    [Fact]
    public async Task TelemetryLogsCommand_WhenNoAppHostRunning_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task TelemetryLogsCommand_WithInvalidLimitValue_ReturnsInvalidCommand(int limitValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

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
        var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
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
        var provider = TelemetryTestHelper.CreateTelemetryTestServices(workspace, outputHelper, outputWriter,
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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
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

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel logs --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardConnectionFailed, "http://localhost:18888"), errorMessage);
    }
}
