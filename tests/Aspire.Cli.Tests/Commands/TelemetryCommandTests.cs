// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

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
    public void BuildResourceQueryString_WithNoResources_ReturnsEmptyString()
    {
        var result = DashboardUrls.BuildResourceQueryString(null);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildResourceQueryString_WithSingleResource_ReturnsCorrectQueryString()
    {
        var result = DashboardUrls.BuildResourceQueryString(["frontend"]);
        Assert.Equal("?resource=frontend", result);
    }

    [Fact]
    public void BuildResourceQueryString_WithMultipleResources_ReturnsAllResourceParams()
    {
        var result = DashboardUrls.BuildResourceQueryString(["frontend-abc123", "frontend-xyz789"]);
        Assert.Equal("?resource=frontend-abc123&resource=frontend-xyz789", result);
    }

    [Fact]
    public void BuildResourceQueryString_WithResourcesAndAdditionalParams_CombinesCorrectly()
    {
        var result = DashboardUrls.BuildResourceQueryString(
            ["frontend"],
            ("traceId", "abc123"),
            ("limit", "10"));
        Assert.Equal("?resource=frontend&traceId=abc123&limit=10", result);
    }

    [Fact]
    public void BuildResourceQueryString_WithNullAdditionalParams_SkipsNullValues()
    {
        var result = DashboardUrls.BuildResourceQueryString(
            ["frontend"],
            ("traceId", null),
            ("limit", "10"));
        Assert.Equal("?resource=frontend&limit=10", result);
    }

    [Fact]
    public void BuildResourceQueryString_WithSpecialCharacters_EncodesCorrectly()
    {
        var result = DashboardUrls.BuildResourceQueryString(["service with spaces"]);
        Assert.Equal("?resource=service%20with%20spaces", result);
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
