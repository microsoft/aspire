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
}
