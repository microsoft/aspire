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
    public void CodingAgentDetector_DetectsKnownCodingAgents((string, string?)[] environmentVariables, string? expectedCodingAgent)
    {
        var configurationValues = new Dictionary<string, string?>();
        foreach (var environmentVariable in environmentVariables)
        {
            if (environmentVariable.Item1.Length > 0)
            {
                configurationValues.Add(environmentVariable.Item1, environmentVariable.Item2);
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
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
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector, codingAgentDetector, Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)));

        var exception = Assert.Throws<InvalidOperationException>(() => telemetry.StartReportedActivity("test"));
        Assert.Contains("not been initialized", exception.Message);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider();
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector();
        var codingAgentDetector = new TelemetryFixture.TestCodingAgentDetector();
        var telemetry = new AspireCliTelemetry(NullLogger<AspireCliTelemetry>.Instance, provider, ciDetector, codingAgentDetector, Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)));

        await telemetry.InitializeAsync().DefaultTimeout();
        var tagsAfterFirstInit = telemetry.GetDefaultTags().Count;
        await telemetry.InitializeAsync(); // Should not throw

        var tags = telemetry.GetDefaultTags();
        Assert.Equal(tagsAfterFirstInit, tags.Count); // Should have the same number of tags after second init
    }

    [Fact]
    public void InitializeAsync_AddsIdentityTags_WhenExecutionContextProvided()
    {
        // The execution context only needs a valid root for path composition; telemetry init
        // does not touch the filesystem for identity, so reuse the test base directory.
        var executionContext = Utils.TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(AppContext.BaseDirectory),
            identityChannel: "daily",
            identityVersion: "13.5.0-preview.1.26310.9",
            identityCommit: "95f0d2968",
            identityOverridden: true);

        using var fixture = new TelemetryFixture(executionContext: executionContext);

        var tags = fixture.Telemetry.GetDefaultTags();

        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityVersion && (string?)t.Value == "13.5.0-preview.1.26310.9");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityChannel && (string?)t.Value == "daily");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityCommit && (string?)t.Value == "95f0d2968");

        // The binary tags must remain distinct from the identity tags so an emulated run is
        // distinguishable from the physical binary that produced the telemetry.
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.CliVersion);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.CliBuildId);
    }

    [Fact]
    public void InitializeAsync_OmitsIdentityCommitTag_WhenCommitIsEmpty()
    {
        var executionContext = Utils.TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(AppContext.BaseDirectory),
            identityChannel: "stable",
            identityVersion: "13.5.0",
            identityCommit: null,
            identityOverridden: true);

        using var fixture = new TelemetryFixture(executionContext: executionContext);

        var tags = fixture.Telemetry.GetDefaultTags();

        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityVersion && (string?)t.Value == "13.5.0");
        Assert.DoesNotContain(tags, t => t.Key == TelemetryConstants.Tags.IdentityCommit);
    }

    public static TheoryData<(string, string?)[], string?> CodingAgentTelemetryTestCases => new()
    {
        { [("CLAUDECODE", "1")], "claude" },
        { [("CLAUDE_CODE", "1")], "claude" },
        { [("CLAUDE_CODE_ENTRYPOINT", "some_value")], "claude" },
        { [("CLAUDE_CODE_IS_COWORK", "1")], "cowork" },
        { [("CURSOR_EDITOR", "1")], "cursor" },
        { [("CURSOR_AI", "1")], "cursor" },
        { [("CURSOR_TRACE_ID", "abc")], "cursor" },
        { [("CURSOR_AGENT", "1")], "cursor" },
        { [("GEMINI_CLI", "true")], "gemini" },
        { [("GEMINI_CLI", "0")], "gemini" },
        { [("GITHUB_COPILOT_CLI_MODE", "true")], "copilot-cli" },
        { [("GH_COPILOT_WORKING_DIRECTORY", "/repo")], "copilot-cli" },
        { [("COPILOT_CLI", "1")], "copilot-cli" },
        { [("COPILOT_MODEL", "gpt")], "copilot-cli" },
        { [("COPILOT_ALLOW_ALL", "1")], "copilot-cli" },
        { [("COPILOT_GITHUB_TOKEN", "token")], "copilot-cli" },
        { [("AI_AGENT", "github_copilot_vscode_agent")], "copilot-vscode" },
        { [("COPILOT_AGENT", "1")], "copilot-vscode" },
        { [("CODEX_CLI", "1")], "codex" },
        { [("CODEX_SANDBOX", "1")], "codex" },
        { [("CODEX_CI", "1")], "codex" },
        { [("CODEX_THREAD_ID", "thread1")], "codex" },
        { [("OR_APP_NAME", "Aider")], "aider" },
        { [("OR_APP_NAME", "aider")], "aider" },
        { [("OR_APP_NAME", "plandex")], "plandex" },
        { [("OR_APP_NAME", "Plandex")], "plandex" },
        { [("AMP_HOME", "/path/to/amp")], "amp" },
        { [("QWEN_CODE", "1")], "qwen" },
        { [("DROID_CLI", "true")], "droid" },
        { [("OPENCODE_AI", "1")], "opencode" },
        { [("ZED_ENVIRONMENT", "1")], "zed" },
        { [("ZED_TERM", "1")], "zed" },
        { [("KIMI_CLI", "true")], "kimi" },
        { [("OR_APP_NAME", "OpenHands")], "openhands" },
        { [("OR_APP_NAME", "openhands")], "openhands" },
        { [("GOOSE_TERMINAL", "1")], "goose" },
        { [("GOOSE_PROVIDER", "openai")], "goose" },
        { [("CLINE_TASK_ID", "task123")], "cline" },
        { [("ROO_CODE_TASK_ID", "task456")], "roo" },
        { [("WINDSURF_SESSION", "session789")], "windsurf" },
        { [("REPL_ID", "repl1")], "replit" },
        { [("AUGMENT_AGENT", "1")], "augment" },
        { [("ANTIGRAVITY_AGENT", "1")], "antigravity" },
        { [("AGENT_CLI", "true")], "generic_agent" },
        { [("CLAUDECODE", "1"), ("CURSOR_EDITOR", "1") ], "claude, cursor" },
        { [("GEMINI_CLI", "true"), ("GITHUB_COPILOT_CLI_MODE", "true") ], "gemini, copilot-cli" },
        { [("CLAUDECODE", "1"), ("GEMINI_CLI", "true"), ("AGENT_CLI", "true") ], "claude, gemini, generic_agent" },
        { [("CLAUDECODE", "1"), ("CURSOR_EDITOR", "1"), ("GEMINI_CLI", "true"), ("GITHUB_COPILOT_CLI_MODE", "true"), ("AGENT_CLI", "true") ], "claude, cursor, gemini, copilot-cli, generic_agent" },
        { [("OR_APP_NAME", "Aider"), ("CLINE_TASK_ID", "task123") ], "aider, cline" },
        { [("CODEX_CLI", "1"), ("WINDSURF_SESSION", "session789") ], "codex, windsurf" },
        { [("GOOSE_TERMINAL", "1"), ("ROO_CODE_TASK_ID", "task456") ], "goose, roo" },
        { [("GEMINI_CLI", "false")], "gemini" },
        { [("GITHUB_COPILOT_CLI_MODE", "false")], "copilot-cli" },
        { [("AGENT_CLI", "false")], "generic_agent" },
        { [("DROID_CLI", "false")], "droid" },
        { [("KIMI_CLI", "false")], "kimi" },
        { [("CLAUDE_CODE_IS_COWORK", "1"), ("CLAUDE_CODE", "1")], "cowork, claude" },
        { [("OR_APP_NAME", "SomeOtherApp")], null },
        { [("", "")], null }
    };
}
