// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryMetadataTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Defaults_EnrichStartupUsageAndActivitiesWithCommonMetadata(bool isCIEnvironment, bool isInternalMicrosoft)
    {
        using var fixture = new DashboardTelemetryFixture();
        var detector = new TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = isInternalMicrosoft,
            Source = "test-source",
            Alias = "test.alias",
            Domain = "TEST"
        };
        var source = DashboardTelemetryService.CreateTagsSource(fixture.Configuration, fixture.LoggerFactory,
            new TestMachineInformationProvider(), detector, isCIEnvironment);
        await source.TagsTask;
        using var telemetry = new DashboardTelemetryService(fixture.LoggerFactory.CreateLogger<DashboardTelemetryService>(),
            fixture.Configuration, source, fixture.ActivitySourceName, fixture.DiagnosticsActivitySourceName);
        fixture.ConfigureLogging(telemetry);
        List<KeyValuePair<string, object?>> expected =
        [
            new("deployment.environment.name", isCIEnvironment ? "ci" : "local"),
            new("os.name", OperatingSystemTelemetry.GetOsName()),
            new("os.type", OperatingSystemTelemetry.GetOsType()),
            new("os.version", Environment.OSVersion.Version.ToString()),
            new("machine.mac_address_hash", "test-mac-hash"),
            new("machine.device_id", "test-device-id"),
            new("aspire.dashboard.version", AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly)),
            new("aspire.dashboard.build_id", AssemblyVersionHelper.GetFileVersion(typeof(DashboardWebApplication).Assembly)),
            new("aspire.dashboard.is_microsoft_internal", isInternalMicrosoft)
        ];
        if (isInternalMicrosoft)
        {
            expected.Add(new("aspire.dashboard.microsoft_internal_source", "test-source"));
            if (!isCIEnvironment)
            {
                expected.Add(new("aspire.dashboard.microsoft_internal_alias", "test.alias"));
                expected.Add(new("aspire.dashboard.microsoft_internal_domain", "TEST"));
            }
        }
        Assert.Equal(expected.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            telemetry.GetDefaultProperties().OrderBy(tag => tag.Key, StringComparer.Ordinal));

        new DashboardStartupTelemetry(telemetry, new DashboardOptions(), Stopwatch.GetTimestamp()).RecordSuccess();
        telemetry.RecordEvent(TelemetryEventKeys.ComponentInitialize);
        using var activity = new Activity("test-operation");
        new ActivityTagEnrichmentProcessor(telemetry).OnEnd(activity);

        Assert.True(fixture.LogChannel.Reader.TryRead(out var startup));
        foreach (var tag in expected)
        {
            Assert.Equal(tag, Assert.Single(startup.Attributes, attribute => attribute.Key == tag.Key));
        }
        Assert.Equal(startup.Attributes.Count, startup.Attributes.Select(tag => tag.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.True(fixture.LogChannel.Reader.TryRead(out var usage));
        Assert.Equal(expected.Concat(
            [
                new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ComponentInitialize),
                new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ComponentInitialize)
            ]).OrderBy(tag => tag.Key, StringComparer.Ordinal),
            usage.Attributes.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        Assert.Equal(expected.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        Assert.Equal(1, detector.InvocationCount);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task Defaults_OptOutSkipsMachineAndInternalDetection()
    {
        using var fixture = new DashboardTelemetryFixture(reportedTelemetryEnabled: false);
        var machineCalls = 0;
        var provider = new TestMachineInformationProvider
        {
            GetMacAddressHashCallback = () =>
            {
                Interlocked.Increment(ref machineCalls);
                return Task.FromResult("unused");
            },
            GetDeviceIdCallback = () =>
            {
                Interlocked.Increment(ref machineCalls);
                return Task.FromResult<string?>("unused");
            }
        };
        var detector = new TestInternalMicrosoftDetector();
        var source = DashboardTelemetryService.CreateTagsSource(fixture.Configuration, fixture.LoggerFactory,
            provider, detector, isCIEnvironment: false);

        Assert.Empty(await source.TagsTask);
        Assert.Equal(0, machineCalls);
        Assert.Equal(0, detector.InvocationCount);
    }

    [Fact]
    public async Task Defaults_FailureIsLoggedWithoutBreakingRecording()
    {
        using var fixture = new DashboardTelemetryFixture();
        var failure = new InvalidOperationException("test detector failure");
        var source = DashboardTelemetryService.CreateTagsSource(fixture.Configuration, fixture.LoggerFactory,
            new TestMachineInformationProvider(), new TestInternalMicrosoftDetector { ExceptionToThrow = failure },
            isCIEnvironment: false);

        Assert.Empty(await source.TagsTask);
        var log = Assert.Single(fixture.LocalLogSink.Writes);
        Assert.Equal(LogLevel.Error, log.LogLevel);
        Assert.Same(failure, log.Exception);
        Assert.Equal("Error occurred initializing dashboard telemetry metadata.", log.Message);

        using var telemetry = new DashboardTelemetryService(fixture.LoggerFactory.CreateLogger<DashboardTelemetryService>(),
            fixture.Configuration, source, fixture.ActivitySourceName, fixture.DiagnosticsActivitySourceName);
        fixture.ConfigureLogging(telemetry);
        telemetry.RecordEvent(TelemetryEventKeys.ComponentInitialize);

        Assert.True(fixture.LogChannel.Reader.TryRead(out var record));
        Assert.Equal(
            new[]
            {
                new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ComponentInitialize),
                new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ComponentInitialize)
            }.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            record.Attributes.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }
}
