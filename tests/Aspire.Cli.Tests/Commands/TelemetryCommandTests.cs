// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class TelemetryCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task TelemetryCommand_WithoutSubcommand_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("otel");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithNoParams_ReturnsBaseUrl()
    {
        var result = DashboardUrls.TelemetryLogsApiUrl("https://localhost:5000");
        Assert.Equal("https://localhost:5000/api/telemetry/logs", result);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithSingleResource_ReturnsCorrectUrl()
    {
        var result = DashboardUrls.TelemetryLogsApiUrl("https://localhost:5000", ["frontend"]);
        Assert.Equal("https://localhost:5000/api/telemetry/logs?resource=frontend", result);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithMultipleResources_ReturnsAllResourceParams()
    {
        var result = DashboardUrls.TelemetryLogsApiUrl("https://localhost:5000", ["frontend-abc123", "frontend-xyz789"]);
        Assert.Equal("https://localhost:5000/api/telemetry/logs?resource=frontend-abc123&resource=frontend-xyz789", result);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithAllParams_CombinesCorrectly()
    {
        var result = DashboardUrls.TelemetryLogsApiUrl("https://localhost:5000", ["frontend"], traceId: "abc123", severity: "Error", limit: 10, follow: true);
        Assert.Equal("https://localhost:5000/api/telemetry/logs?resource=frontend&traceId=abc123&severity=Error&limit=10&follow=true", result);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithNullParams_SkipsNullValues()
    {
        var result = DashboardUrls.TelemetryLogsApiUrl("https://localhost:5000", ["frontend"], traceId: null, limit: 10);
        Assert.Equal("https://localhost:5000/api/telemetry/logs?resource=frontend&limit=10", result);
    }

    [Fact]
    public void TelemetryLogsApiUrl_WithSpecialCharacters_EncodesCorrectly()
    {
        var result = DashboardUrls.TelemetryLogsApiUrl("https://localhost:5000", ["service with spaces"]);
        Assert.Equal("https://localhost:5000/api/telemetry/logs?resource=service%20with%20spaces", result);
    }

    [Fact]
    public void ToShortenedId_WithLongId_ReturnsShortenedVersion()
    {
        var result = OtlpHelpers.ToShortenedId("abc1234567890");
        Assert.Equal("abc1234", result);
        Assert.Equal(7, result.Length);
    }

    [Fact]
    public void ToShortenedId_WithShortId_ReturnsOriginal()
    {
        var result = OtlpHelpers.ToShortenedId("abc");
        Assert.Equal("abc", result);
    }

    [Fact]
    public void FormatConsoleTime_WithValidTimestamp_ReturnsFormattedTime()
    {
        // 2026-01-31 12:00:00.123 UTC
        var dateTime = OtlpHelpers.UnixNanoSecondsToDateTime(1769860800123000000UL);
        var result = FormatHelpers.FormatConsoleTime(TimeProvider.System, dateTime);

        // Result should contain time component (HH:mm:ss.fff)
        Assert.Matches(@"\d{2}:\d{2}:\d{2}\.\d{3}", result);
    }

    [Fact]
    public void GetSeverityColor_ReturnsCorrectColors()
    {
        Assert.Equal(Spectre.Console.Color.Grey, TelemetryCommandHelpers.GetSeverityColor(1)); // Trace
        Assert.Equal(Spectre.Console.Color.Grey, TelemetryCommandHelpers.GetSeverityColor(5)); // Debug
        Assert.Equal(Spectre.Console.Color.Blue, TelemetryCommandHelpers.GetSeverityColor(9)); // Information
        Assert.Equal(Spectre.Console.Color.Yellow, TelemetryCommandHelpers.GetSeverityColor(13)); // Warning
        Assert.Equal(Spectre.Console.Color.Red, TelemetryCommandHelpers.GetSeverityColor(17)); // Error
        Assert.Equal(Spectre.Console.Color.Red, TelemetryCommandHelpers.GetSeverityColor(21)); // Critical/Fatal
    }

    [Fact]
    public void CalculateDuration_WithValidTimestamps_ReturnsCorrectDuration()
    {
        ulong start = 1000000000UL; // 1 second in nanos
        ulong end = 2500000000UL;   // 2.5 seconds in nanos

        var result = OtlpHelpers.CalculateDuration(start, end);

        Assert.Equal(TimeSpan.FromMilliseconds(1500), result);
    }

    [Fact]
    public void FormatTraceLink_WithDashboardUrl_ReturnsHyperlink()
    {
        var result = TelemetryCommandHelpers.FormatTraceLink("http://localhost:18888", "abc123456789");

        Assert.Contains("[link=", result);
        Assert.Contains("/traces/detail/abc123456789", result);
        Assert.Contains("abc1234", result); // Shortened ID
    }

    [Fact]
    public void FormatTraceLink_WithNullDashboardUrl_ReturnsPlainText()
    {
        var result = TelemetryCommandHelpers.FormatTraceLink(null, "abc123456789");

        Assert.DoesNotContain("[link=", result);
        Assert.Equal("abc1234", result); // Just the shortened ID
    }

    [Fact]
    public void ToOtlpResources_ConvertsResourceInfoToOtlpResources()
    {
        var resources = new ResourceInfoJson[]
        {
            new() { Name = "frontend", InstanceId = "abc123" },
            new() { Name = "backend", InstanceId = null },
            new() { Name = "frontend", InstanceId = "xyz789" },
        };

        var result = TelemetryCommandHelpers.ToOtlpResources(resources);

        Assert.Equal(3, result.Count);
        Assert.Equal("frontend", result[0].ResourceName);
        Assert.Equal("abc123", result[0].InstanceId);
        Assert.Equal("backend", result[1].ResourceName);
        Assert.Null(result[1].InstanceId);
        Assert.Equal("frontend", result[2].ResourceName);
        Assert.Equal("xyz789", result[2].InstanceId);

        // Empty input yields empty output
        Assert.Empty(TelemetryCommandHelpers.ToOtlpResources([]));
    }

    [Theory]
    [MemberData(nameof(ResolveResourceNameTestData))]
    internal void ResolveResourceName_ResolvesExpectedName(
        OtlpResourceJson? resource,
        IOtlpResource[] allResources,
        string expectedName)
    {
        var result = TelemetryCommandHelpers.ResolveResourceName(resource, allResources);

        Assert.Equal(expectedName, result);
    }

    public static IEnumerable<object?[]> ResolveResourceNameTestData()
    {
        var guid = Guid.Parse("aabbccdd-1122-3344-5566-778899001122");
        var guidStr = guid.ToString();

        // null resource → "unknown"
        yield return [null, Array.Empty<IOtlpResource>(), "unknown"];
        // no attributes → "unknown"
        yield return [new OtlpResourceJson { Attributes = null }, new IOtlpResource[] { new SimpleOtlpResource("unknown", null) }, "unknown"];
        // unique service name → bare name
        yield return [MakeResource("frontend", "abc123"), new IOtlpResource[] { new SimpleOtlpResource("frontend", "abc123") }, "frontend"];
        // missing instance id, single resource → bare name
        yield return [MakeResource("apiservice", null), new IOtlpResource[] { new SimpleOtlpResource("apiservice", null) }, "apiservice"];
        // replicas with non-GUID instance id → name-instanceId
        yield return [MakeResource("frontend", "abc123"), new IOtlpResource[] { new SimpleOtlpResource("frontend", "abc123"), new SimpleOtlpResource("frontend", "xyz789") }, "frontend-abc123"];
        // replicas with GUID instance id → name-shortened8chars
        yield return [MakeResource("worker", guidStr), new IOtlpResource[] { new SimpleOtlpResource("worker", guidStr), new SimpleOtlpResource("worker", Guid.NewGuid().ToString()) }, $"worker-{guid:N}"[..15]];
    }

    [Theory]
    [MemberData(nameof(InvalidTelemetryApiResponseTestData))]
    public async Task TelemetryCommand_WithDashboardUrl_InvalidTelemetryApiResponse_DisplaysErrorMessage(
        string otelCommand, HttpStatusCode? statusCode, string? contentType, string? body, HttpStatusCode? baseProbeStatusCode, string expectedMessageKey)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var testInteractionService = new TestInteractionService();

        var handler = CreateInvalidResponseHandler(statusCode, contentType, body, baseProbeStatusCode);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.AddSingleton(handler);
        services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new MockHttpClientFactory(handler)));

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"otel {otelCommand} --dashboard-url http://localhost:18888");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.DashboardFailure, exitCode);
        var errorMessage = Assert.Single(testInteractionService.DisplayedErrors);
        var expectedMessage = GetExpectedErrorMessage(expectedMessageKey);
        Assert.Equal(expectedMessage, errorMessage);
    }

    public static IEnumerable<object?[]> InvalidTelemetryApiResponseTestData()
    {
        string[] commands = ["logs", "spans", "traces"];

        (HttpStatusCode? statusCode, string? contentType, string? body, HttpStatusCode? baseProbeStatusCode, string expectedMessageKey)[] cases =
        [
            (null, null, null, null, nameof(TelemetryCommandStrings.DashboardConnectionFailed)),
            (HttpStatusCode.NotFound, "text/plain", "Not Found", HttpStatusCode.OK, nameof(TelemetryCommandStrings.DashboardApiNotEnabled)),
            (HttpStatusCode.NotFound, "text/plain", "Not Found", HttpStatusCode.NotFound, nameof(TelemetryCommandStrings.DashboardUrlNotReachable)),
            (HttpStatusCode.OK, "text/html", "<html></html>", HttpStatusCode.OK, nameof(TelemetryCommandStrings.DashboardApiNotEnabled)),
            (HttpStatusCode.OK, "text/html", "<html></html>", HttpStatusCode.NotFound, nameof(TelemetryCommandStrings.DashboardUrlNotReachable)),
            (HttpStatusCode.OK, "text/plain", "not json", HttpStatusCode.OK, nameof(TelemetryCommandStrings.FailedToFetchTelemetry)),
            (HttpStatusCode.OK, "text/plain", "not json", HttpStatusCode.NotFound, nameof(TelemetryCommandStrings.FailedToFetchTelemetry)),
        ];

        foreach (var cmd in commands)
        {
            foreach (var (statusCode, contentType, body, baseProbeStatusCode, expectedMessageKey) in cases)
            {
                yield return [cmd, statusCode, contentType, body, baseProbeStatusCode, expectedMessageKey];
            }
        }
    }

    private static MockHttpMessageHandler CreateInvalidResponseHandler(
        HttpStatusCode? statusCode, string? contentType, string? body, HttpStatusCode? baseProbeStatusCode)
    {
        return new MockHttpMessageHandler(request =>
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
                if (statusCode is null)
                {
                    throw new HttpRequestException("Connection refused");
                }
                return new HttpResponseMessage(statusCode.Value)
                {
                    Content = new StringContent(body!, System.Text.Encoding.UTF8, contentType!)
                };
            }
            // Base URL probe
            if (baseProbeStatusCode is null)
            {
                throw new HttpRequestException("Connection refused");
            }
            return new HttpResponseMessage(baseProbeStatusCode.Value);
        });
    }

    private static string GetExpectedErrorMessage(string key) => key switch
    {
        nameof(TelemetryCommandStrings.DashboardApiNotEnabled) => string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardApiNotEnabled, "http://localhost:18888"),
        nameof(TelemetryCommandStrings.DashboardUrlNotReachable) => string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardUrlNotReachable, "http://localhost:18888"),
        nameof(TelemetryCommandStrings.DashboardConnectionFailed) => string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardConnectionFailed, "http://localhost:18888"),
        nameof(TelemetryCommandStrings.FailedToFetchTelemetry) => string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.FailedToFetchTelemetry,
            string.Format(CultureInfo.InvariantCulture, TelemetryCommandStrings.UnexpectedContentType, "text/plain")),
        _ => throw new ArgumentException($"Unknown message key: {key}")
    };

    private static OtlpResourceJson MakeResource(string serviceName, string? instanceId)
    {
        var attrs = new List<OtlpKeyValueJson>
        {
            new() { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } },
        };
        if (instanceId is not null)
        {
            attrs.Add(new() { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = instanceId } });
        }
        return new OtlpResourceJson { Attributes = [.. attrs] };
    }
}
