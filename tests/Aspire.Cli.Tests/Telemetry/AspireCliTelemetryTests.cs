// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Telemetry;

public class AspireCliTelemetryTests
{
    [Fact]
    public void StartReportedActivity_CreatesActivityWithCorrectName()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartReportedActivity("test-activity", ActivityKind.Internal);

        Assert.NotNull(activity);
        Assert.Equal("test-activity", activity.OperationName);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
    }

    [Fact]
    public void StartDiagnosticActivity_CreatesActivityWithCorrectNameAndDefaultTags()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartDiagnosticActivity("test-diagnostic");

        Assert.NotNull(activity);
        Assert.Equal("test-diagnostic", activity.OperationName);

        // Verify all default tags are included
        var defaultTags = fixture.Telemetry.GetDefaultTags();
        var activityTags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
        foreach (var tag in defaultTags)
        {
            Assert.True(activityTags.ContainsKey(tag.Key), $"Activity is missing tag '{tag.Key}'");
            Assert.Equal(tag.Value?.ToString(), activityTags[tag.Key]);
        }
    }

    [Fact]
    public void StartDiagnosticActivity_WithKind_CreatesActivityWithCorrectKind()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartDiagnosticActivity("test-client", ActivityKind.Client);

        Assert.NotNull(activity);
        Assert.Equal("test-client", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
    }

    [Fact]
    public void StartDiagnosticActivity_UsesCallerMemberName_WhenNoNameProvided()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartDiagnosticActivity();

        Assert.NotNull(activity);
        Assert.Equal(nameof(StartDiagnosticActivity_UsesCallerMemberName_WhenNoNameProvided), activity.OperationName);
    }

    [Fact]
    public void RecordError_LogsError()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        fixture.Telemetry.RecordError("Error occurred", exception);

        var logRecord = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, logRecord.Level);
        Assert.Equal("Error occurred", logRecord.Message);
        Assert.Same(exception, logRecord.Exception);
    }

    [Fact]
    public void RecordError_AddsActivityEventWithDefaultTags_WhenReportedActivityIsActive()
    {
        using var fixture = new TelemetryFixture();
        var exception = new InvalidOperationException("Test exception");

        using var activity = fixture.Telemetry.StartReportedActivity("test-activity", ActivityKind.Internal);
        Assert.NotNull(activity);

        fixture.Telemetry.RecordError("Error occurred", exception);

        var events = activity.Events.ToList();
        var exceptionEvent = Assert.Single(events);
        Assert.Equal(TelemetryConstants.Events.Error, exceptionEvent.Name);

        var eventTags = exceptionEvent.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(typeof(InvalidOperationException).FullName, eventTags[TelemetryConstants.Tags.ExceptionType]);
        Assert.Equal("Test exception", eventTags[TelemetryConstants.Tags.ExceptionMessage]);
        // Note: exception.stacktrace may not be present if the exception was never thrown

        // Verify all default tags are included in the event
        var defaultTags = fixture.Telemetry.GetDefaultTags();
        foreach (var tag in defaultTags)
        {
            Assert.True(eventTags.ContainsKey(tag.Key), $"Event is missing tag '{tag.Key}'");
            Assert.Equal(tag.Value, eventTags[tag.Key]);
        }
    }

    [Fact]
    public void RecordError_DoesNotThrow_WhenNoActivityIsActive()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        // Should not throw even when there's no active activity
        fixture.Telemetry.RecordError("Error occurred", exception);

        // Verify logging still happens
        var logRecord = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, logRecord.Level);
    }

    [Fact]
    public void RecordError_FindsReportedActivity_InHierarchy()
    {
        using var fixture = new TelemetryFixture();
        var otherSourceName = $"Test.{Path.GetRandomFileName()}";

        using var otherListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == otherSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(otherListener);

        var exception = new InvalidOperationException("Test exception");

        // Start a reported activity (parent)
        using var reportedActivity = fixture.Telemetry.StartReportedActivity("parent-activity", ActivityKind.Internal);
        Assert.NotNull(reportedActivity);

        // Start a child activity from a different source
        using var otherSource = new ActivitySource(otherSourceName);
        using var childActivity = otherSource.StartActivity("child-activity");
        Assert.NotNull(childActivity);

        // RecordError should find the reported activity in the hierarchy
        fixture.Telemetry.RecordError("Error in child", exception);

        // The error should be recorded on the reported activity, not the child
        var events = reportedActivity.Events.ToList();
        Assert.Single(events);

        // Child activity should not have the error event
        Assert.Empty(childActivity.Events);
    }

    [Fact]
    public void RecordError_DoesNotRecordEvent_WhenOnlyDiagnosticActivityIsActive()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        using var activity = fixture.Telemetry.StartDiagnosticActivity("test-activity");
        Assert.NotNull(activity);

        fixture.Telemetry.RecordError("Error occurred", exception);

        // FindKnownActivity only looks for ReportedActivitySource, so no event should be added
        Assert.Empty(activity.Events);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);

        // But logging should still happen
        var logRecord = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, logRecord.Level);
    }

    [Fact]
    public void InitializeAsync_AddsMachineInformationTags()
    {
        var machineInfoProvider = new TelemetryFixture.TestMachineInformationProvider
        {
            DeviceId = "test-device-id",
            MacAddressHash = "test-mac-hash"
        };
        using var fixture = new TelemetryFixture(machineInfoProvider);

        var tags = fixture.Telemetry.GetDefaultTags();

        Assert.Contains(tags, t => t.Key == "machine.device_id" && (string?)t.Value == "test-device-id");
        Assert.Contains(tags, t => t.Key == "machine.mac_address_hash" && (string?)t.Value == "test-mac-hash");
    }

    [Fact]
    public void InitializeAsync_AddsOsInformationTags()
    {
        using var fixture = new TelemetryFixture();

        var tags = fixture.Telemetry.GetDefaultTags();

        var expectedOsName = AspireCliTelemetry.GetOsName();
        var expectedOsType = AspireCliTelemetry.GetOsType();
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsName && (string?)t.Value == expectedOsName);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsVersion && t.Value is string s && s == Environment.OSVersion.Version.ToString());
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsType && (string?)t.Value == expectedOsType);
    }

    [Fact]
    public void StartReportedActivity_IncludesAllDefaultTags()
    {
        var machineInfoProvider = new TelemetryFixture.TestMachineInformationProvider
        {
            DeviceId = "test-device-id",
            MacAddressHash = "test-mac-hash"
        };
        using var fixture = new TelemetryFixture(machineInfoProvider, sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartReportedActivity("test-activity");

        Assert.NotNull(activity);

        // Verify all default tags are included
        var defaultTags = fixture.Telemetry.GetDefaultTags();
        var activityTags = activity.Tags.ToDictionary(t => t.Key, t => t.Value);
        foreach (var tag in defaultTags)
        {
            Assert.True(activityTags.ContainsKey(tag.Key), $"Activity is missing tag '{tag.Key}'");
            Assert.Equal(tag.Value?.ToString(), activityTags[tag.Key]);
        }
    }

    [Fact]
    public void StartReportedActivity_ThrowsIfNotInitialized()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider();
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector);

        var exception = Assert.Throws<InvalidOperationException>(() => telemetry.StartReportedActivity("test"));
        Assert.Contains("not been initialized", exception.Message);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider();
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector);

        await telemetry.InitializeAsync().DefaultTimeout();
        var tagsAfterFirstInit = telemetry.GetDefaultTags().Count;
        await telemetry.InitializeAsync(); // Should not throw

        var tags = telemetry.GetDefaultTags();
        Assert.Equal(tagsAfterFirstInit, tags.Count); // Should have the same number of tags after second init
    }
}
