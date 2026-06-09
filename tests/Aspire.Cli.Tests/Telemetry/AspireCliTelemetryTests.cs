// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;
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
    public void StartReportedActivity_WithParentContext_CreatesChildActivity()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);
        var parentContext = ActivityContext.Parse("00-0102030405060708090a0b0c0d0e0f10-1112131415161718-01", null);

        using var activity = fixture.Telemetry.StartReportedActivity("test-activity", ActivityKind.Internal, parentContext);

        Assert.NotNull(activity);
        Assert.Equal(parentContext.TraceId, activity.TraceId);
        Assert.Equal(parentContext.SpanId, activity.ParentSpanId);
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
    public void StartDiagnosticActivity_WithParentContext_CreatesChildActivity()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);
        var parentContext = ActivityContext.Parse("00-1112131415161718191a1b1c1d1e1f20-2122232425262728-01", null);

        using var activity = fixture.Telemetry.StartDiagnosticActivity("test-activity", ActivityKind.Internal, parentContext);

        Assert.NotNull(activity);
        Assert.Equal(parentContext.TraceId, activity.TraceId);
        Assert.Equal(parentContext.SpanId, activity.ParentSpanId);
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
    public void InitializeAsync_AddsCodingAgentTag_WhenCodingAgentIsDetected()
    {
        var codingAgentDetector = new TelemetryFixture.TestCodingAgentDetector
        {
            CodingAgent = "copilot"
        };
        using var fixture = new TelemetryFixture(codingAgentDetector: codingAgentDetector, sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartReportedActivity(TelemetryConstants.Activities.Main);

        Assert.NotNull(activity);
        Assert.Equal("copilot", activity.GetTagItem(TelemetryConstants.Tags.CodingAgent));
    }

    [Fact]
    public void InitializeAsync_DoesNotAddCodingAgentTag_WhenCodingAgentIsNotDetected()
    {
        using var fixture = new TelemetryFixture();

        var tags = fixture.Telemetry.GetDefaultTags();

        Assert.DoesNotContain(tags, t => t.Key == TelemetryConstants.Tags.CodingAgent);
    }

    [Theory]
    [MemberData(nameof(CodingAgentTelemetryTestCases))]
    public void CodingAgentDetector_DetectsKnownCodingAgents(Dictionary<string, string?> environmentVariables, string? expectedCodingAgent)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(environmentVariables)
            .Build();

        var detector = new CodingAgentDetector(configuration);

        Assert.Equal(expectedCodingAgent, detector.GetCodingAgent());
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
        var codingAgentDetector = new TelemetryFixture.TestCodingAgentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector, codingAgentDetector);

        var exception = Assert.Throws<InvalidOperationException>(() => telemetry.StartReportedActivity("test"));
        Assert.Contains("not been initialized", exception.Message);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider();
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector();
        var codingAgentDetector = new TelemetryFixture.TestCodingAgentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector, codingAgentDetector);

        await telemetry.InitializeAsync().DefaultTimeout();
        var tagsAfterFirstInit = telemetry.GetDefaultTags().Count;
        await telemetry.InitializeAsync(); // Should not throw

        var tags = telemetry.GetDefaultTags();
        Assert.Equal(tagsAfterFirstInit, tags.Count); // Should have the same number of tags after second init
    }

    public static TheoryData<Dictionary<string, string?>, string?> CodingAgentTelemetryTestCases => new()
    {
        { new Dictionary<string, string?> { { "CLAUDECODE", "1" } }, "claude" },
        { new Dictionary<string, string?> { { "CLAUDE_CODE", "1" } }, "claude" },
        { new Dictionary<string, string?> { { "CLAUDE_CODE_ENTRYPOINT", "some_value" } }, "claude" },
        { new Dictionary<string, string?> { { "CLAUDE_CODE_IS_COWORK", "1" } }, "cowork" },
        { new Dictionary<string, string?> { { "CURSOR_EDITOR", "1" } }, "cursor" },
        { new Dictionary<string, string?> { { "CURSOR_AI", "1" } }, "cursor" },
        { new Dictionary<string, string?> { { "CURSOR_TRACE_ID", "abc" } }, "cursor" },
        { new Dictionary<string, string?> { { "CURSOR_AGENT", "1" } }, "cursor" },
        { new Dictionary<string, string?> { { "GEMINI_CLI", "true" } }, "gemini" },
        { new Dictionary<string, string?> { { "GEMINI_CLI", "0" } }, "gemini" },
        { new Dictionary<string, string?> { { "GITHUB_COPILOT_CLI_MODE", "true" } }, "copilot" },
        { new Dictionary<string, string?> { { "GH_COPILOT_WORKING_DIRECTORY", "/repo" } }, "copilot" },
        { new Dictionary<string, string?> { { "COPILOT_CLI", "1" } }, "copilot" },
        { new Dictionary<string, string?> { { "COPILOT_AGENT", "1" } }, "copilot" },
        { new Dictionary<string, string?> { { "COPILOT_MODEL", "gpt" } }, "copilot" },
        { new Dictionary<string, string?> { { "COPILOT_ALLOW_ALL", "1" } }, "copilot" },
        { new Dictionary<string, string?> { { "COPILOT_GITHUB_TOKEN", "token" } }, "copilot" },
        { new Dictionary<string, string?> { { "CODEX_CLI", "1" } }, "codex" },
        { new Dictionary<string, string?> { { "CODEX_SANDBOX", "1" } }, "codex" },
        { new Dictionary<string, string?> { { "CODEX_CI", "1" } }, "codex" },
        { new Dictionary<string, string?> { { "CODEX_THREAD_ID", "thread1" } }, "codex" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "Aider" } }, "aider" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "aider" } }, "aider" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "plandex" } }, "plandex" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "Plandex" } }, "plandex" },
        { new Dictionary<string, string?> { { "AMP_HOME", "/path/to/amp" } }, "amp" },
        { new Dictionary<string, string?> { { "QWEN_CODE", "1" } }, "qwen" },
        { new Dictionary<string, string?> { { "DROID_CLI", "true" } }, "droid" },
        { new Dictionary<string, string?> { { "OPENCODE_AI", "1" } }, "opencode" },
        { new Dictionary<string, string?> { { "ZED_ENVIRONMENT", "1" } }, "zed" },
        { new Dictionary<string, string?> { { "ZED_TERM", "1" } }, "zed" },
        { new Dictionary<string, string?> { { "KIMI_CLI", "true" } }, "kimi" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "OpenHands" } }, "openhands" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "openhands" } }, "openhands" },
        { new Dictionary<string, string?> { { "GOOSE_TERMINAL", "1" } }, "goose" },
        { new Dictionary<string, string?> { { "GOOSE_PROVIDER", "openai" } }, "goose" },
        { new Dictionary<string, string?> { { "CLINE_TASK_ID", "task123" } }, "cline" },
        { new Dictionary<string, string?> { { "ROO_CODE_TASK_ID", "task456" } }, "roo" },
        { new Dictionary<string, string?> { { "WINDSURF_SESSION", "session789" } }, "windsurf" },
        { new Dictionary<string, string?> { { "REPL_ID", "repl1" } }, "replit" },
        { new Dictionary<string, string?> { { "AUGMENT_AGENT", "1" } }, "augment" },
        { new Dictionary<string, string?> { { "ANTIGRAVITY_AGENT", "1" } }, "antigravity" },
        { new Dictionary<string, string?> { { "AGENT_CLI", "true" } }, "generic_agent" },
        { new Dictionary<string, string?> { { "CLAUDECODE", "1" }, { "CURSOR_EDITOR", "1" } }, "claude, cursor" },
        { new Dictionary<string, string?> { { "GEMINI_CLI", "true" }, { "GITHUB_COPILOT_CLI_MODE", "true" } }, "gemini, copilot" },
        { new Dictionary<string, string?> { { "CLAUDECODE", "1" }, { "GEMINI_CLI", "true" }, { "AGENT_CLI", "true" } }, "claude, gemini, generic_agent" },
        { new Dictionary<string, string?> { { "CLAUDECODE", "1" }, { "CURSOR_EDITOR", "1" }, { "GEMINI_CLI", "true" }, { "GITHUB_COPILOT_CLI_MODE", "true" }, { "AGENT_CLI", "true" } }, "claude, cursor, gemini, copilot, generic_agent" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "Aider" }, { "CLINE_TASK_ID", "task123" } }, "aider, cline" },
        { new Dictionary<string, string?> { { "CODEX_CLI", "1" }, { "WINDSURF_SESSION", "session789" } }, "codex, windsurf" },
        { new Dictionary<string, string?> { { "GOOSE_TERMINAL", "1" }, { "ROO_CODE_TASK_ID", "task456" } }, "goose, roo" },
        { new Dictionary<string, string?> { { "GEMINI_CLI", "false" } }, "gemini" },
        { new Dictionary<string, string?> { { "GITHUB_COPILOT_CLI_MODE", "false" } }, "copilot" },
        { new Dictionary<string, string?> { { "AGENT_CLI", "false" } }, "generic_agent" },
        { new Dictionary<string, string?> { { "DROID_CLI", "false" } }, "droid" },
        { new Dictionary<string, string?> { { "KIMI_CLI", "false" } }, "kimi" },
        { new Dictionary<string, string?> { { "CLAUDE_CODE_IS_COWORK", "1" }, { "CLAUDE_CODE", "1" } }, "cowork, claude" },
        { new Dictionary<string, string?> { { "OR_APP_NAME", "SomeOtherApp" } }, null },
        { new Dictionary<string, string?>(), null },
    };
}
