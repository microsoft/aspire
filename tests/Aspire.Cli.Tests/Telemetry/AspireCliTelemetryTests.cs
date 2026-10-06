// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Telemetry;

public class AspireCliTelemetryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetActivityProperties_PreservesCliValuesAndSupportsRemovingTags(bool batch)
    {
        using var fixture = new TelemetryFixture(initialize: false);
        using var activity = fixture.Telemetry.StartReportedActivity("test-activity");
        Assert.NotNull(activity);
        fixture.Telemetry.SetActivityProperty(activity, "test.removed", "original");
        string[] values = ["one", "two"];
        KeyValuePair<string, object?>[] properties =
        [
            new("test.string", "value"),
            new("test.number", 42L),
            new("test.array", values),
            new("test.removed", null)
        ];

        if (batch)
        {
            fixture.Telemetry.SetActivityProperties(activity, properties);
        }
        else
        {
            foreach (var (key, value) in properties)
            {
                fixture.Telemetry.SetActivityProperty(activity, key, value);
            }
        }

        Assert.Collection(activity.TagObjects.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(new KeyValuePair<string, object?>("test.array", values), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("test.number", 42L), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("test.string", "value"), tag));
        Assert.Empty(activity.Events);
    }

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
    public async Task StartDiagnosticActivity_CreatesActivityWithCorrectNameAndDefaultTags()
    {
        using var fixture = new TelemetryFixture(sampleResult: ActivitySamplingResult.AllData);

        using var activity = fixture.Telemetry.StartDiagnosticActivity("test-diagnostic");

        Assert.NotNull(activity);
        Assert.Equal("test-diagnostic", activity.OperationName);

        // Verify all default tags are included
        var defaultTags = await fixture.Telemetry.GetDefaultTagsAsync();
        var activityTags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        foreach (var tag in defaultTags)
        {
            Assert.True(activityTags.ContainsKey(tag.Key), $"Activity is missing tag '{tag.Key}'");
            Assert.Equal(tag.Value, activityTags[tag.Key]);
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
    public void RecordError_LogsLocalAndStructuredErrors()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        fixture.Telemetry.RecordError("Error occurred", exception);

        Assert.Collection(logger.Collector.GetSnapshot(),
            local =>
            {
                Assert.Equal(LogLevel.Error, local.Level);
                Assert.Equal("Error occurred", local.Message);
                Assert.Same(exception, local.Exception);
            },
            reported =>
            {
                Assert.Equal(LogLevel.Information, reported.Level);
                Assert.Equal(TelemetryConstants.Events.Error, reported.Message);
                Assert.Equal(TelemetryConstants.Events.Error, reported.Id.Name);
                Assert.Null(reported.Exception);
                Assert.NotNull(reported.StructuredState);
                Assert.Equal(TelemetryConstants.Events.Error,
                    reported.StructuredState.Single(t => t.Key == "microsoft.operation_name").Value);
                Assert.Equal(typeof(InvalidOperationException).FullName,
                    reported.StructuredState.Single(t => t.Key == TelemetryConstants.Tags.ExceptionType).Value);
                Assert.Equal("Test exception",
                    reported.StructuredState.Single(t => t.Key == TelemetryConstants.Tags.ExceptionMessage).Value);
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecordEvent_LogsWithDefaultTagsWithoutAddingActivityEvents(bool withActivity)
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        using var activity = withActivity ? fixture.Telemetry.StartReportedActivity("test-activity") : null;
        var current = Activity.Current;

        fixture.Telemetry.RecordEvent("test-event", [new("test.property", 42)]);

        Assert.Same(current, Activity.Current);
        var log = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal("test-event", log.Message);
        Assert.Equal("test-event", log.Id.Name);
        Assert.Null(log.Exception);
        Assert.NotNull(log.StructuredState);
        Assert.Equal("test-event", log.StructuredState.Single(t => t.Key == "microsoft.operation_name").Value);
        Assert.Equal("42", log.StructuredState.Single(t => t.Key == "test.property").Value);
        var defaultTags = await fixture.Telemetry.GetDefaultTagsAsync();
        Assert.NotEmpty(defaultTags);
        foreach (var tag in defaultTags)
        {
            Assert.Equal(tag.Value?.ToString(), log.StructuredState.Single(t => t.Key == tag.Key).Value);
        }
        if (withActivity)
        {
            Assert.NotNull(activity);
            Assert.Empty(activity.Events);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RecordEvent_InvalidName_ThrowsWithoutLogging(string? eventName)
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);

        Assert.ThrowsAny<ArgumentException>(() => fixture.Telemetry.RecordEvent(eventName!));

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void TelemetryOptOut_SuppressesStructuredEventsAndErrorsButPreservesLocalErrors()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger,
            telemetryConfiguration: new TelemetryConfiguration { ReportedTelemetryEnabled = false });
        using var activity = fixture.Telemetry.StartReportedActivity("test-activity");
        Assert.NotNull(activity);
        var exception = new InvalidOperationException("Test exception");

        fixture.Telemetry.RecordEvent("test-event");
        fixture.Telemetry.RecordError("Local error", exception);

        Assert.Empty(activity.Events);
        var log = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Equal("Local error", log.Message);
        Assert.Same(exception, log.Exception);
    }

    [Fact]
    public async Task RecordError_LogsWithDefaultTagsWithoutActivityEvents_WhenReportedActivityIsActive()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        using var activity = fixture.Telemetry.StartReportedActivity("test-activity", ActivityKind.Internal);
        Assert.NotNull(activity);

        fixture.Telemetry.RecordError("Error occurred", exception);

        Assert.Same(activity, Activity.Current);
        Assert.Empty(activity.Events);
        var log = Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Information);
        Assert.Equal(TelemetryConstants.Events.Error, log.Message);
        Assert.Null(log.Exception);
        Assert.NotNull(log.StructuredState);
        Assert.Equal(typeof(InvalidOperationException).FullName,
            log.StructuredState.Single(t => t.Key == TelemetryConstants.Tags.ExceptionType).Value);
        Assert.Equal("Test exception",
            log.StructuredState.Single(t => t.Key == TelemetryConstants.Tags.ExceptionMessage).Value);
        Assert.Equal(TelemetryConstants.Events.Error,
            log.StructuredState.Single(t => t.Key == "microsoft.operation_name").Value);
        var defaultTags = await fixture.Telemetry.GetDefaultTagsAsync();
        Assert.NotEmpty(defaultTags);
        foreach (var tag in defaultTags)
        {
            Assert.Equal(tag.Value?.ToString(), log.StructuredState.Single(t => t.Key == tag.Key).Value);
        }
    }

    [Fact]
    public void RecordError_LogsStructuredEvent_WhenNoActivityIsActive()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        var current = Activity.Current;
        fixture.Telemetry.RecordError("Error occurred", exception);

        Assert.Same(current, Activity.Current);
        Assert.Collection(logger.Collector.GetSnapshot(),
            local => Assert.Equal(LogLevel.Error, local.Level),
            reported =>
            {
                Assert.Equal(LogLevel.Information, reported.Level);
                Assert.Equal(TelemetryConstants.Events.Error, reported.Message);
            });
    }

    [Fact]
    public void RecordError_LogsWithoutActivityEvents_WhenReportedAncestorIsActive()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var otherSourceName = $"Test.{Path.GetRandomFileName()}";

        using var otherListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == otherSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(otherListener);

        var exception = new InvalidOperationException("Test exception");

        using var reportedActivity = fixture.Telemetry.StartReportedActivity("parent-activity", ActivityKind.Internal);
        Assert.NotNull(reportedActivity);

        using var otherSource = new ActivitySource(otherSourceName);
        using var childActivity = otherSource.StartActivity("child-activity");
        Assert.NotNull(childActivity);

        fixture.Telemetry.RecordError("Error in child", exception);

        Assert.Same(childActivity, Activity.Current);
        Assert.Empty(reportedActivity.Events);
        Assert.Empty(childActivity.Events);
        Assert.Collection(logger.Collector.GetSnapshot(),
            local => Assert.Equal(LogLevel.Error, local.Level),
            reported =>
            {
                Assert.Equal(LogLevel.Information, reported.Level);
                Assert.Equal(TelemetryConstants.Events.Error, reported.Message);
                Assert.Null(reported.Exception);
            });
    }

    [Fact]
    public void RecordError_LogsWithoutActivityEvent_WhenOnlyDiagnosticActivityIsActive()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger);
        var exception = new InvalidOperationException("Test exception");

        using var activity = fixture.Telemetry.StartDiagnosticActivity("test-activity");
        Assert.NotNull(activity);

        fixture.Telemetry.RecordError("Error occurred", exception);

        Assert.Empty(activity.Events);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);

        Assert.Collection(logger.Collector.GetSnapshot(),
            local => Assert.Equal(LogLevel.Error, local.Level),
            reported =>
            {
                Assert.Equal(LogLevel.Information, reported.Level);
                Assert.Equal(TelemetryConstants.Events.Error, reported.Message);
            });
    }

    [Fact]
    public async Task Initialize_AddsMachineInformationTags()
    {
        var machineInfoProvider = new TelemetryFixture.TestMachineInformationProvider
        {
            DeviceId = "test-device-id",
            MacAddressHash = "test-mac-hash"
        };
        using var fixture = new TelemetryFixture(machineInfoProvider);

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Contains(tags, t => t.Key == "machine.device_id" && (string?)t.Value == "test-device-id");
        Assert.Contains(tags, t => t.Key == "machine.mac_address_hash" && (string?)t.Value == "test-mac-hash");
    }

    [Fact]
    public async Task Initialize_AddsOsInformationTags()
    {
        using var fixture = new TelemetryFixture();

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        var expectedOsName = AspireCliTelemetry.GetOsName();
        var expectedOsType = AspireCliTelemetry.GetOsType();
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsName && (string?)t.Value == expectedOsName);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsVersion && t.Value is string s && s == Environment.OSVersion.Version.ToString());
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsType && (string?)t.Value == expectedOsType);
    }

    [Fact]
    public void Initialize_AddsCodingAgentTag_WhenCodingAgentIsDetected()
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
    public async Task Initialize_DoesNotAddCodingAgentTag_WhenCodingAgentIsNotDetected()
    {
        using var fixture = new TelemetryFixture();

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.DoesNotContain(tags, t => t.Key == TelemetryConstants.Tags.CodingAgent);
    }

    [Fact]
    public async Task Initialize_AddsInternalMicrosoftTag()
    {
        Assert.Equal("aspire.cli.microsoft_internal_source", TelemetryConstants.Tags.InternalMicrosoftSource);
        Assert.Equal("aspire.cli.microsoft_internal_alias", TelemetryConstants.Tags.InternalMicrosoftAlias);
        Assert.Equal("aspire.cli.microsoft_internal_domain", TelemetryConstants.Tags.InternalMicrosoftDomain);
        Assert.Equal("aspire.cli.microsoft_internal_detector.outcome", TelemetryConstants.Tags.InternalMicrosoftDetectorOutcome);
        Assert.Equal("aspire.cli.microsoft_internal_probe.failure_code", TelemetryConstants.Tags.InternalMicrosoftProbeFailureCode);
        Assert.Equal("aspire.cli.microsoft_internal_probe.failure_stage", TelemetryConstants.Tags.InternalMicrosoftProbeFailureStage);
        Assert.Equal("aspire.cli.microsoft_internal_probe.exception_type", TelemetryConstants.Tags.InternalMicrosoftProbeExceptionType);
        Assert.Equal("aspire.cli.microsoft_internal_probe.process_exit_code", TelemetryConstants.Tags.InternalMicrosoftProbeProcessExitCode);
        Assert.Equal("aspire.cli.microsoft_internal_probe.http_status_code", TelemetryConstants.Tags.InternalMicrosoftProbeHttpStatusCode);

        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = true,
            Source = "test source",
            Alias = "test.alias",
            Domain = "TEST"
        };
        using var fixture = new TelemetryFixture(internalMicrosoftDetector: internalMicrosoftDetector);

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.InternalMicrosoft && t.Value is true);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.InternalMicrosoftSource && (string?)t.Value == "test source");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.InternalMicrosoftAlias && (string?)t.Value == "test.alias");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.InternalMicrosoftDomain && (string?)t.Value == "TEST");
    }

    [Fact]
    public async Task Initialize_AddsExplicitFalseInternalMicrosoftTag_WhenNotDetected()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = false
        };
        using var fixture = new TelemetryFixture(internalMicrosoftDetector: internalMicrosoftDetector);

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Collection(
            GetInternalMicrosoftTags(tags),
            tag =>
            {
                Assert.Equal(TelemetryConstants.Tags.InternalMicrosoft, tag.Key);
                Assert.False((bool?)tag.Value);
            });
    }

    [Fact]
    public async Task Initialize_DoesNotAddOptionalInternalMicrosoftTags_WhenValuesAreNotDetected()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = true,
            Source = "test source"
        };
        using var fixture = new TelemetryFixture(internalMicrosoftDetector: internalMicrosoftDetector);

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Collection(
            GetInternalMicrosoftTags(tags),
            tag =>
            {
                Assert.Equal(TelemetryConstants.Tags.InternalMicrosoft, tag.Key);
                Assert.True((bool?)tag.Value);
            },
            tag =>
            {
                Assert.Equal(TelemetryConstants.Tags.InternalMicrosoftSource, tag.Key);
                Assert.Equal("test source", tag.Value);
            });
    }

    [Fact]
    public async Task Initialize_SuppressesAliasAndDomainInCI()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = true,
            Source = "test source",
            Alias = "test.alias",
            Domain = "TEST"
        };
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector
        {
            IsCIEnvironmentResult = true
        };
        using var fixture = new TelemetryFixture(ciEnvironmentDetector: ciDetector, internalMicrosoftDetector: internalMicrosoftDetector);

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.InternalMicrosoft && t.Value is true);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.InternalMicrosoftSource && (string?)t.Value == "test source");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.DeploymentEnvironmentName && (string?)t.Value == "ci");
        Assert.Collection(
            GetInternalMicrosoftTags(tags),
            tag =>
            {
                Assert.Equal(TelemetryConstants.Tags.InternalMicrosoft, tag.Key);
                Assert.True((bool?)tag.Value);
            },
            tag =>
            {
                Assert.Equal(TelemetryConstants.Tags.InternalMicrosoftSource, tag.Key);
                Assert.Equal("test source", tag.Value);
            });
    }

    [Fact]
    public async Task Initialize_DoesNotRunInternalMicrosoftDetectorWhenReportedTelemetryIsDisabled()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider();
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector();
        var codingAgentDetector = new TelemetryFixture.TestCodingAgentDetector();
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = true
        };
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
        var telemetry = new AspireCliTelemetry(
            NullLogger<AspireCliTelemetry>.Instance,
            provider,
            ciDetector,
            codingAgentDetector,
            internalMicrosoftDetector,
            new TelemetryConfiguration { ReportedTelemetryEnabled = false },
            AspireCliTelemetry.ReportedActivitySourceName,
            AspireCliTelemetry.DiagnosticsActivitySourceName,
            Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)),
            tagsSource);

        telemetry.Initialize();
        await tagsSource.TagsTask;

        Assert.Equal(0, internalMicrosoftDetector.InvocationCount);
        Assert.Empty(GetInternalMicrosoftTags(await telemetry.GetDefaultTagsAsync()));
    }

    [Fact]
    public async Task CompleteInternalMicrosoftDiagnosticsAsync_DoesNotWaitForTagsWhenReportedTelemetryIsDisabled()
    {
        var blockedTag = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machineInformationProvider = new TelemetryFixture.TestMachineInformationProvider
        {
            GetDeviceIdCallback = () => blockedTag.Task
        };
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
        var telemetry = new AspireCliTelemetry(
            NullLogger<AspireCliTelemetry>.Instance,
            machineInformationProvider,
            new TelemetryFixture.TestCIEnvironmentDetector(),
            new TelemetryFixture.TestCodingAgentDetector(),
            new TelemetryFixture.TestInternalMicrosoftDetector(),
            new TelemetryConfiguration
            {
                ReportedTelemetryEnabled = false,
                EmitInternalMicrosoftDiagnostics = true
            },
            AspireCliTelemetry.ReportedActivitySourceName,
            AspireCliTelemetry.DiagnosticsActivitySourceName,
            Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)),
            tagsSource);

        telemetry.Initialize();
        await telemetry.CompleteInternalMicrosoftDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(tagsSource.TagsTask.IsCompleted);
        blockedTag.TrySetResult("test-device-id");
    }

    [Fact]
    public async Task Initialize_BoundsInternalMicrosoftDetectorForAgentTelemetryInvocation()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            DetectionCallback = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            }
        };
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
        var telemetry = new AspireCliTelemetry(
            NullLogger<AspireCliTelemetry>.Instance,
            new TelemetryFixture.TestMachineInformationProvider(),
            new TelemetryFixture.TestCIEnvironmentDetector(),
            new TelemetryFixture.TestCodingAgentDetector(),
            internalMicrosoftDetector,
            new TelemetryConfiguration
            {
                ReportedTelemetryEnabled = true,
                EmitInternalMicrosoftDiagnostics = false,
                InternalMicrosoftDetectionTimeout = TimeSpan.FromMilliseconds(25)
            },
            AspireCliTelemetry.ReportedActivitySourceName,
            AspireCliTelemetry.DiagnosticsActivitySourceName,
            Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)),
            tagsSource);

        telemetry.Initialize();
        await tagsSource.TagsTask;

        Assert.Equal(1, internalMicrosoftDetector.InvocationCount);
        Assert.Contains(
            await telemetry.GetDefaultTagsAsync(),
            tag => tag.Key == TelemetryConstants.Tags.InternalMicrosoft && (bool?)tag.Value == false);
    }

    [Fact]
    public async Task Initialize_AddsDefaultTags_WhenInternalMicrosoftDetectorFails()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider
        {
            DeviceId = "test-device-id",
            MacAddressHash = "test-mac-hash"
        };
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            ExceptionToThrow = new NotSupportedException("Unexpected probe failure.")
        };
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
        var telemetry = new AspireCliTelemetry(
            NullLogger<AspireCliTelemetry>.Instance,
            provider,
            new TelemetryFixture.TestCIEnvironmentDetector(),
            new TelemetryFixture.TestCodingAgentDetector(),
            internalMicrosoftDetector,
            new TelemetryConfiguration { ReportedTelemetryEnabled = true },
            AspireCliTelemetry.ReportedActivitySourceName,
            AspireCliTelemetry.DiagnosticsActivitySourceName,
            Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)),
            tagsSource);

        telemetry.Initialize();
        await tagsSource.TagsTask;

        var tags = await telemetry.GetDefaultTagsAsync();
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.MacAddressHash && (string?)t.Value == "test-mac-hash");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.DeviceId && (string?)t.Value == "test-device-id");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.CliVersion);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.OsName);
        Assert.Collection(
            GetInternalMicrosoftTags(tags),
            tag =>
            {
                Assert.Equal(TelemetryConstants.Tags.InternalMicrosoft, tag.Key);
                Assert.False((bool?)tag.Value);
            });
    }

    [Fact]
    public async Task Initialize_RecordsElapsedDurationWhenInternalMicrosoftDetectorFails()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            DetectionCallback = async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                throw new NotSupportedException("Unexpected probe failure.");
            }
        };
        using var fixture = new TelemetryFixture(internalMicrosoftDetector: internalMicrosoftDetector);
        await fixture.Telemetry.CompleteInternalMicrosoftDiagnosticsAsync();

        var activity = Assert.IsType<Activity>(fixture.CapturedActivity);
        Assert.Equal(InternalMicrosoftDetectorOutcome.Failed, activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDetectorOutcome));
        Assert.True((long?)activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDetectorDurationMs) > 0);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(500)]
    public async Task GetInternalMicrosoftResultAsync_RecordsActualElapsedDurationWhenDetectorTimesOut(int elapsedMilliseconds)
    {
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromMilliseconds(25);
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            DetectionCallback = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            }
        };
        using var fixture = new TelemetryFixture(
            internalMicrosoftDetector: internalMicrosoftDetector,
            initialize: false);
        var resultTask = fixture.Telemetry.GetInternalMicrosoftResultAsync(timeoutSource, timeProvider);

        Assert.False(resultTask.IsCompleted);
        // Cancellation can be observed later than the budget under contention. Keep virtual time
        // fixed until the wrapper finishes so scheduling cannot affect the recorded duration.
        var elapsed = TimeSpan.FromMilliseconds(elapsedMilliseconds);
        timeProvider.Advance(elapsed);
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(30));

        // Completing unrelated tag calculation later must not extend the already captured duration.
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(InternalMicrosoftDetectorOutcome.TimedOut, result.Outcome);
        Assert.Equal(elapsed, result.Duration);
    }

    [Fact]
    public async Task CompleteInternalMicrosoftDiagnosticsAsync_ContainsListenerFailureDuringShutdown()
    {
        var logger = new FakeLogger<AspireCliTelemetry>();
        using var fixture = new TelemetryFixture(logger: logger, initialize: false);
        var exception = new InvalidOperationException("Simulated detector activity listener failure.");
        var listenerCalled = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == fixture.ReportedSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == TelemetryConstants.Activities.InternalMicrosoftDetector)
                {
                    listenerCalled = true;
                    throw exception;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        fixture.Telemetry.Initialize();
        await fixture.Telemetry.CompleteInternalMicrosoftDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await fixture.Telemetry.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(listenerCalled);
        var log = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.Equal("Failed to complete internal Microsoft diagnostics.", log.Message);
        Assert.Same(exception, log.Exception);
    }

    [Fact]
    public async Task CompleteInternalMicrosoftDiagnosticsAsync_PreservesCallerCancellation()
    {
        var deviceId = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new TelemetryFixture(
            machineInfoProvider: new TelemetryFixture.TestMachineInformationProvider
            {
                GetDeviceIdCallback = () => deviceId.Task
            },
            initialize: false);
        using var cancellationSource = new CancellationTokenSource();
        fixture.Telemetry.Initialize();
        var completionTask = fixture.Telemetry.CompleteInternalMicrosoftDiagnosticsAsync();
        var stopTask = fixture.Telemetry.StopAsync(cancellationSource.Token);

        try
        {
            await cancellationSource.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopTask);
            Assert.False(completionTask.IsCompleted);
        }
        finally
        {
            deviceId.TrySetResult("test-device-id");
            await completionTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void CompleteInternalMicrosoftDiagnosticsAsync_ReusesOneCompletionWait()
    {
        using var fixture = new TelemetryFixture();

        var firstWait = fixture.Telemetry.CompleteInternalMicrosoftDiagnosticsAsync();
        var secondWait = fixture.Telemetry.CompleteInternalMicrosoftDiagnosticsAsync();

        Assert.Same(firstWait, secondWait);
    }

    [Fact]
    public void Initialize_EmitsBoundedInternalMicrosoftDiagnosticActivity()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            IsInternalMicrosoft = true,
            Source = "test source",
            Alias = "test.alias",
            Domain = "TEST",
            ProbeDiagnostics =
            [
                new InternalMicrosoftProbeDiagnostic("test source", InternalMicrosoftProbeOutcome.Detected, TimeSpan.FromMilliseconds(12), HasAlias: true, HasDomain: true)
            ]
        };
        using var fixture = new TelemetryFixture(internalMicrosoftDetector: internalMicrosoftDetector);

        var activity = fixture.CapturedActivity;

        Assert.NotNull(activity);
        Assert.Equal(TelemetryConstants.Activities.InternalMicrosoftDetector, activity.OperationName);
        Assert.Equal(fixture.ReportedSourceName, activity.Source.Name);
        Assert.Equal(InternalMicrosoftDetectorOutcome.Detected, activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDetectorOutcome));
        Assert.Equal(InternalMicrosoftDetectorCacheStatus.Miss, activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDetectorCacheStatus));
        Assert.Equal("test source", activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftSource));
        Assert.True((bool?)activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDetectorHasAlias));
        Assert.True((bool?)activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDetectorHasDomain));
        Assert.Null(activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftAlias));
        Assert.Null(activity.GetTagItem(TelemetryConstants.Tags.InternalMicrosoftDomain));
        var probeEvent = Assert.Single(activity.Events);
        Assert.Equal(TelemetryConstants.Events.InternalMicrosoftProbe, probeEvent.Name);
        Assert.Contains(probeEvent.Tags, tag => tag.Key == TelemetryConstants.Tags.InternalMicrosoftProbeOutcome && (string?)tag.Value == InternalMicrosoftProbeOutcome.Detected);
    }

    [Fact]
    public void Initialize_EmitsOnlyAllowListedProbeFailureMetadata()
    {
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector
        {
            ProbeDiagnostics =
            [
                new InternalMicrosoftProbeDiagnostic(
                    "test source",
                    InternalMicrosoftProbeOutcome.Failed,
                    TimeSpan.FromMilliseconds(18),
                    HasAlias: false,
                    HasDomain: false,
                    Failure: new InternalMicrosoftProbeFailure(
                        InternalMicrosoftProbeFailureCode.ProcessExit,
                        InternalMicrosoftProbeFailureStage.ProcessExit,
                        ExceptionType: InternalMicrosoftProbeExceptionType.Other,
                        ProcessExitCode: 7,
                        HttpStatusCode: 503))
            ]
        };
        using var fixture = new TelemetryFixture(internalMicrosoftDetector: internalMicrosoftDetector);

        var probeEvent = Assert.Single(fixture.CapturedActivity!.Events);
        var tags = probeEvent.Tags.ToDictionary(tag => tag.Key, tag => tag.Value);

        Assert.Equal(InternalMicrosoftProbeFailureCode.ProcessExit, tags[TelemetryConstants.Tags.InternalMicrosoftProbeFailureCode]);
        Assert.Equal(InternalMicrosoftProbeFailureStage.ProcessExit, tags[TelemetryConstants.Tags.InternalMicrosoftProbeFailureStage]);
        Assert.Equal(InternalMicrosoftProbeExceptionType.Other, tags[TelemetryConstants.Tags.InternalMicrosoftProbeExceptionType]);
        Assert.Equal(7, tags[TelemetryConstants.Tags.InternalMicrosoftProbeProcessExitCode]);
        Assert.Equal(503, tags[TelemetryConstants.Tags.InternalMicrosoftProbeHttpStatusCode]);
        Assert.DoesNotContain(tags, tag => tag.Key.Contains("message", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tags, tag => tag.Key.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("CLAUDECODE", null, "1", null)]
    [InlineData("CLAUDECODE", "1", "", "claude")]
    [InlineData("CLAUDECODE", "1", null, "claude")]
    [InlineData("AI_AGENT", null, "github_copilot_app_agent", null)]
    [InlineData("AI_AGENT", "github_copilot_app_agent", "github_copilot_vscode_agent", "copilot-app")]
    [InlineData("OR_APP_NAME", "Aider", "plandex", "aider")]
    public void CodingAgentDetector_IgnoresConfigurationValues(string variableName, string? environmentValue, string? configuredValue, string? expectedCodingAgent)
    {
        var environmentVariables = new Dictionary<string, string?>
        {
            [variableName] = environmentValue
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(environmentVariables)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [variableName] = configuredValue
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IEnvironment>(new TestEnvironment(environmentVariables));
        services.AddTelemetryServices();
        using var serviceProvider = services.BuildServiceProvider();

        var detector = serviceProvider.GetRequiredService<ICodingAgentDetector>();

        Assert.Equal(expectedCodingAgent, detector.GetCodingAgent());
    }

    [Theory]
    [MemberData(nameof(CodingAgentTelemetryTestCases))]
    public void CodingAgentDetector_DetectsKnownCodingAgents((string, string?)[] environmentVariables, string? expectedCodingAgent)
    {
        var environment = new TestEnvironment(environmentVariables.ToDictionary(variable => variable.Item1, variable => variable.Item2, StringComparer.Ordinal));
        var detector = new CodingAgentDetector(environment);

        Assert.Equal(expectedCodingAgent, detector.GetCodingAgent());
    }

    [Theory]
    [InlineData(true, "claude")]
    [InlineData(false, null)]
    public void CodingAgentDetector_PreservesEnvironmentVariableNameComparison(bool ignoreCase, string? expectedCodingAgent)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new TestEnvironment(new Dictionary<string, string?>(comparer)
        {
            ["claudecode"] = "1"
        });
        var detector = new CodingAgentDetector(environment);

        Assert.Equal(expectedCodingAgent, detector.GetCodingAgent());
    }

    [Fact]
    public void CodingAgentDetector_ReadsCurrentEnvironmentValues()
    {
        var environmentVariables = new Dictionary<string, string?>();
        var detector = new CodingAgentDetector(new TestEnvironment(environmentVariables));

        Assert.Null(detector.GetCodingAgent());

        environmentVariables["AI_AGENT"] = "github_copilot_app_agent";
        Assert.Equal("copilot-app", detector.GetCodingAgent());

        environmentVariables["AI_AGENT"] = "github_copilot_vscode_agent";
        Assert.Equal("copilot-vscode", detector.GetCodingAgent());

        environmentVariables.Clear();
        Assert.Null(detector.GetCodingAgent());
    }

    [Fact]
    public async Task StartReportedActivity_IncludesAllDefaultTags()
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
        var defaultTags = await fixture.Telemetry.GetDefaultTagsAsync();
        var activityTags = activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        foreach (var tag in defaultTags)
        {
            Assert.True(activityTags.ContainsKey(tag.Key), $"Activity is missing tag '{tag.Key}'");
            Assert.Equal(tag.Value, activityTags[tag.Key]);
        }
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        var provider = new TelemetryFixture.TestMachineInformationProvider();
        var ciDetector = new TelemetryFixture.TestCIEnvironmentDetector();
        var codingAgentDetector = new TelemetryFixture.TestCodingAgentDetector();
        var internalMicrosoftDetector = new TelemetryFixture.TestInternalMicrosoftDetector();
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);
        var telemetry = new AspireCliTelemetry(
            NullLogger<AspireCliTelemetry>.Instance,
            provider,
            ciDetector,
            codingAgentDetector,
            internalMicrosoftDetector,
            new TelemetryConfiguration { ReportedTelemetryEnabled = true },
            AspireCliTelemetry.ReportedActivitySourceName,
            AspireCliTelemetry.DiagnosticsActivitySourceName,
            Utils.TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory)),
            tagsSource);

        telemetry.Initialize();
        await tagsSource.TagsTask;
        var tagsAfterFirstInit = (await telemetry.GetDefaultTagsAsync()).Count;
        telemetry.Initialize(); // Should not throw

        var tags = await telemetry.GetDefaultTagsAsync();
        Assert.Equal(tagsAfterFirstInit, tags.Count); // Should have the same number of tags after second init
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> GetInternalMicrosoftTags(IReadOnlyList<KeyValuePair<string, object?>> tags)
    {
        return [.. tags.Where(t => t.Key == TelemetryConstants.Tags.InternalMicrosoft ||
            t.Key is TelemetryConstants.Tags.InternalMicrosoftSource or TelemetryConstants.Tags.InternalMicrosoftAlias or TelemetryConstants.Tags.InternalMicrosoftDomain)];
    }

    [Fact]
    public async Task Initialize_AddsIdentityTags_WhenExecutionContextProvided()
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

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityVersion && (string?)t.Value == "13.5.0-preview.1.26310.9");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityChannel && (string?)t.Value == "daily");
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityCommit && (string?)t.Value == "95f0d2968");

        // The binary tags must remain distinct from the identity tags so an emulated run is
        // distinguishable from the physical binary that produced the telemetry.
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.CliVersion);
        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.CliBuildId);
    }

    [Fact]
    public async Task Initialize_OmitsIdentityCommitTag_WhenCommitIsEmpty()
    {
        var executionContext = Utils.TestExecutionContextHelper.CreateExecutionContext(
            new DirectoryInfo(AppContext.BaseDirectory),
            identityChannel: "stable",
            identityVersion: "13.5.0",
            identityCommit: null,
            identityOverridden: true);

        using var fixture = new TelemetryFixture(executionContext: executionContext);

        var tags = await fixture.Telemetry.GetDefaultTagsAsync();

        Assert.Contains(tags, t => t.Key == TelemetryConstants.Tags.IdentityVersion && (string?)t.Value == "13.5.0");
        Assert.DoesNotContain(tags, t => t.Key == TelemetryConstants.Tags.IdentityCommit);
    }

    public static TheoryData<(string, string?)[], string?> CodingAgentTelemetryTestCases => new()
    {
        { [("CLAUDECODE", "1")], "claude" },
        { [("CLAUDECODE", null)], null },
        { [("CLAUDECODE", "")], null },
        { [("CLAUDECODE", " ")], "claude" },
        { [("CLAUDECODE", "1"), ("CLAUDE_CODE", "1")], "claude" },
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
        { [("AI_AGENT", "github_copilot_app_agent")], "copilot-app" },
        { [("AI_AGENT", "GITHUB_COPILOT_APP_AGENT")], "copilot-app" },
        { [("AI_AGENT", " github_copilot_app_agent ")], null },
        { [("AI_AGENT", "unknown_agent")], null },
        { [("AI_AGENT", "")], null },
        { [("AI_AGENT", null)], null },
        { [("AI_AGENT", "github_copilot_vscode_agent")], "copilot-vscode" },
        { [("COPILOT_AGENT", "1")], "copilot-vscode" },
        { [("AI_AGENT", "github_copilot_vscode_agent"), ("COPILOT_AGENT", "1")], "copilot-vscode" },
        { [("CODEX_CLI", "1")], "codex" },
        { [("CODEX_SANDBOX", "1")], "codex" },
        { [("CODEX_CI", "1")], "codex" },
        { [("CODEX_THREAD_ID", "thread1")], "codex" },
        { [("OR_APP_NAME", "Aider")], "aider" },
        { [("OR_APP_NAME", "aider")], "aider" },
        { [("OR_APP_NAME", " Aider ")], null },
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
        { [("COPILOT_CLI", "1"), ("AI_AGENT", "github_copilot_app_agent")], "copilot-cli, copilot-app" },
        { [("GEMINI_CLI", "false")], "gemini" },
        { [("GITHUB_COPILOT_CLI_MODE", "false")], "copilot-cli" },
        { [("AGENT_CLI", "false")], "generic_agent" },
        { [("DROID_CLI", "false")], "droid" },
        { [("KIMI_CLI", "false")], "kimi" },
        { [("CLAUDE_CODE_IS_COWORK", "1"), ("CLAUDE_CODE", "1")], "cowork, claude" },
        { [("OR_APP_NAME", "SomeOtherApp")], null },
        { [], null }
    };
}
