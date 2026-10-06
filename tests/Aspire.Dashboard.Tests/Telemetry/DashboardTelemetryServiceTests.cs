// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryServiceTests
{
    [Theory]
    [InlineData(TelemetryResult.Success, ActivityStatusCode.Ok)]
    [InlineData(TelemetryResult.Failure, ActivityStatusCode.Error)]
    [InlineData(TelemetryResult.UserFault, ActivityStatusCode.Error)]
    [InlineData(TelemetryResult.None, ActivityStatusCode.Unset)]
    [InlineData(TelemetryResult.UserCancel, ActivityStatusCode.Unset)]
    public void Operation_RecordsResultAndCompletesActivity(TelemetryResult result, ActivityStatusCode status)
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;

        using var parent = new Activity("parent").Start();
        Activity activity;
        using (var operation = service.StartOperation(TelemetryEventKeys.ExecuteCommand, new()
        {
            [TelemetryPropertyKeys.CommandName] = new("resource-stop")
        }))
        {
            activity = Assert.IsType<Activity>(operation);
            Assert.False(activity.IsStopped);
            Assert.Same(activity, Activity.Current);
            Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));

            service.SetOperationResult(activity, result);
            Assert.False(activity.IsStopped);
            Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        }

        Assert.Same(parent, Activity.Current);
        Assert.True(fixture.ActivityChannel.Reader.TryRead(out var recorded));
        Assert.Same(activity, recorded);
        Assert.True(activity.IsStopped);
        Assert.Equal(status, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.Equal(result.ToString(), activity.GetTagItem("aspire.dashboard.result"));
        Assert.Equal("resource-stop", activity.GetTagItem(TelemetryPropertyKeys.CommandName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetActivityProperties_PreservesClassificationAndBounds(bool batch)
    {
        using var fixture = new DashboardTelemetryFixture();
        using var activity = fixture.Telemetry.StartReportedActivity("dashboard-operation");
        Assert.NotNull(activity);
        Dictionary<string, AspireTelemetryProperty> properties = new()
        {
            [TelemetryPropertyKeys.CommandName] = new(new string('x', 1100)),
            [TelemetryPropertyKeys.MetricsInstrumentsCount] = new("12", AspireTelemetryPropertyType.Metric),
            [TelemetryPropertyKeys.StructuredLogsFilterCount] = new("NaN", AspireTelemetryPropertyType.Metric),
            [TelemetryPropertyKeys.ResourceType] = new("secret", AspireTelemetryPropertyType.Pii),
            ["Unknown"] = new("secret")
        };

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
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.CommandName, new string('x', 1024)), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.MetricsInstrumentsCount, 12d), tag));
        Assert.Empty(activity.Events);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void SetActivityProperty_DisallowedValue_PreservesExistingTag()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var activity = fixture.Telemetry.StartReportedActivity("dashboard-operation");
        Assert.NotNull(activity);
        fixture.Telemetry.SetActivityProperty(activity, TelemetryPropertyKeys.CommandName, new AspireTelemetryProperty("resource-stop"));

        fixture.Telemetry.SetActivityProperty(activity, TelemetryPropertyKeys.CommandName, new AspireTelemetryProperty("secret", AspireTelemetryPropertyType.Pii));

        Assert.Collection(activity.TagObjects,
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.CommandName, "resource-stop"), tag));
    }

    [Fact]
    public void RecordEvent_UsesAmbientCorrelationWithoutCreatingActivities()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;

        using var parent = new Activity("parent").Start();
        service.RecordEvent(TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);
        service.RecordEvent(TelemetryEventKeys.ParametersSet, TelemetryResult.Success);

        Assert.Same(parent, Activity.Current);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var initializeEvent));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var parametersEvent));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, initializeEvent.Message);
        Assert.Equal(TelemetryEventKeys.ParametersSet, parametersEvent.Message);
        Assert.Equal(TelemetryEventKeys.ComponentInitialize,
            initializeEvent.Attributes.Single(t => t.Key == "microsoft.operation_name").Value);
        Assert.Equal(TelemetryEventKeys.ParametersSet,
            parametersEvent.Attributes.Single(t => t.Key == "microsoft.operation_name").Value);
        Assert.Equal(parent.TraceId, initializeEvent.TraceId);
        Assert.Equal(parent.SpanId, initializeEvent.SpanId);
        Assert.Equal(parent.TraceId, parametersEvent.TraceId);
        Assert.Equal(parent.SpanId, parametersEvent.SpanId);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(TelemetryResult.Success)]
    [InlineData(TelemetryResult.Failure)]
    [InlineData(TelemetryResult.UserFault)]
    [InlineData(TelemetryResult.None)]
    [InlineData(TelemetryResult.UserCancel)]
    public void RecordEvent_RecordsStructuredLogWithoutActivity(TelemetryResult result)
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            service.RecordEvent(TelemetryEventKeys.ComponentInitialize, result);

            Assert.Null(Activity.Current);
            Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
            Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
            Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.EventId.Name);
            Assert.Equal(LogLevel.Information, log.Level);
            Assert.Equal(DashboardTelemetryService.EventLogCategoryName, log.CategoryName);
            Assert.Equal(default, log.TraceId);
            Assert.Equal(default, log.SpanId);
            Assert.Equal(TelemetryEventKeys.ComponentInitialize,
                log.Attributes.Single(p => p.Key == "microsoft.operation_name").Value);
            Assert.Equal(result.ToString(), log.Attributes.Single(p => p.Key == "aspire.dashboard.result").Value);
            Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        }
        finally
        {
            Activity.Current = previous;
        }
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void TelemetryNotSampled_StillRecordsUsageLogs()
    {
        using var fixture = new DashboardTelemetryFixture(sampleResult: ActivitySamplingResult.None);
        var service = fixture.Telemetry;

        var activity = service.StartOperation(TelemetryEventKeys.ExecuteCommand, []);
        Assert.Null(activity);
        service.SetOperationResult(activity, TelemetryResult.Success);
        service.RecordEvent(TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordEvent_FilterExcludesOtherCategoriesAndScopesButPreservesLocalLogging()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        var loggerFactory = Assert.IsAssignableFrom<ILoggerFactory>(fixture.LoggerFactory);

        loggerFactory.CreateLogger<DashboardTelemetryService>().LogError(new InvalidOperationException("secret"), "Ordinary error");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost").LogError("Framework error");
        loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName + ".Other").LogInformation("Other event");
        loggerFactory.CreateLogger("Aspire.Dashboard.Other").LogInformation("Other category");
        using (fixture.EventLogger.BeginScope(new Dictionary<string, object?> { ["secret"] = "workspace path" }))
        {
            service.RecordEvent(TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);
        }

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
        Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>("aspire.dashboard.result", "Success"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ComponentInitialize), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ComponentInitialize), tag));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.Collection(fixture.LocalLogSink.Writes,
            log => Assert.Equal("Ordinary error", log.Message),
            log => Assert.Equal("Framework error", log.Message),
            log => Assert.Equal("Other category", log.Message));
    }

    [Fact]
    public void RecordEvent_InvalidResult_ThrowsWithoutLogging()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;

        Assert.Throws<ArgumentOutOfRangeException>(() => service.RecordEvent(TelemetryEventKeys.ComponentInitialize, (TelemetryResult)int.MaxValue));

        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordEvent_WithReportedOperation_LogsUsageAndSanitizedErrorsImmediately()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;

        Activity activity;
        using (var operation = service.StartOperation(TelemetryEventKeys.ExecuteCommand, []))
        {
            activity = Assert.IsType<Activity>(operation);
            service.RecordError("Local error", new InvalidOperationException("secret error"), writeToLogging: true);
            Assert.True(fixture.LogChannel.Reader.TryRead(out var errorLog));
            Assert.Equal(TelemetryEventKeys.Error, errorLog.Message);
            Assert.Equal(activity.TraceId, errorLog.TraceId);
            Assert.Equal(activity.SpanId, errorLog.SpanId);
            Assert.Empty(activity.Events);
            Assert.Collection(errorLog.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
                tag => Assert.Equal(TelemetryPropertyKeys.ExceptionRuntimeVersion, tag.Key),
                tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ExceptionType, typeof(InvalidOperationException).FullName), tag),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
                tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.Error), tag),
                tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.Error), tag));

            service.RecordEvent(TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);

            Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
            Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
            Assert.Equal(activity.TraceId, log.TraceId);
            Assert.Equal(activity.SpanId, log.SpanId);
            Assert.False(activity.IsStopped);
            Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        }

        Assert.True(fixture.ActivityChannel.Reader.TryRead(out var recorded));
        Assert.Same(activity, recorded);
        Assert.Empty(recorded.Events);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordEvent_WithReportedAncestor_UsesAmbientLogCorrelationWithoutActivityEvents()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        using var diagnosticListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == fixture.DiagnosticsActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(diagnosticListener);
        using var parent = service.StartReportedActivity("dashboard-operation");
        Assert.NotNull(parent);
        using var child = service.StartDiagnosticActivity("diagnostic-operation");
        Assert.NotNull(child);

        service.RecordEvent(TelemetryEventKeys.ParametersSet, TelemetryResult.Success, new()
        {
            [TelemetryPropertyKeys.DashboardComponentId] = new("Metrics")
        });

        Assert.Same(child, Activity.Current);
        Assert.Empty(child.Events);
        Assert.Empty(parent.Events);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(child.TraceId, log.TraceId);
        Assert.Equal(child.SpanId, log.SpanId);
        Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.DashboardComponentId, "Metrics"), tag),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>("aspire.dashboard.result", "Success"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ParametersSet), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ParametersSet), tag));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordEvent_AfterReportedActivityStops_OnlyLogs()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var activity = fixture.Telemetry.StartReportedActivity("dashboard-operation");
        Assert.NotNull(activity);
        activity.Stop();
        Assert.True(fixture.ActivityChannel.Reader.TryRead(out _));

        fixture.Telemetry.RecordEvent(TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);

        Assert.Empty(activity.Events);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData("Array")]
    [InlineData("List")]
    [InlineData("Enumerable")]
    public void RecordEvent_BoundsStringsAndCollections(string collectionType)
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        using var activity = service.StartReportedActivity("dashboard-operation");
        Assert.NotNull(activity);
        var resourceTypes = Enumerable.Repeat(new string('y', 300), 105);
        IEnumerable<string> valuesToRecord = collectionType switch
        {
            "Array" => resourceTypes.ToArray(),
            "List" => resourceTypes.ToList(),
            "Enumerable" => resourceTypes,
            _ => throw new ArgumentOutOfRangeException(nameof(collectionType))
        };

        service.RecordEvent(TelemetryEventKeys.ParametersSet, TelemetryResult.Success, new()
        {
            [TelemetryPropertyKeys.CommandName] = new(new string('x', 1100)),
            [TelemetryPropertyKeys.ResourceTypes] = new(valuesToRecord)
        });

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(new string('x', 1024), log.Attributes.Single(p => p.Key == TelemetryPropertyKeys.CommandName).Value);
        var values = Assert.IsType<string[]>(log.Attributes.Single(p => p.Key == TelemetryPropertyKeys.ResourceTypes).Value);
        Assert.Equal(100, values.Length);
        Assert.All(values, value => Assert.Equal(new string('y', 256), value));
        Assert.Empty(activity.Events);
        Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.CommandName, new string('x', 1024)), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ResourceTypes, values), tag),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>("aspire.dashboard.result", "Success"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ParametersSet), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ParametersSet), tag));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordError_WithoutActivity_LogsSanitizedErrorWithoutCreatingActivity()
    {
        var sink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(sink)));
        using var fixture = new DashboardTelemetryFixture(logger: loggerFactory.CreateLogger<DashboardTelemetryService>());
        var service = fixture.Telemetry;
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var exception = new InvalidOperationException("secret workspace path");
            service.RecordError("Local message", exception);

            Assert.Null(Activity.Current);
            Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
            var log = Assert.Single(sink.Writes);
            Assert.Equal(LogLevel.Error, log.LogLevel);
            Assert.Equal("Local message", log.Message);
            Assert.Same(exception, log.Exception);
            Assert.True(fixture.LogChannel.Reader.TryRead(out var errorLog));
            Assert.Equal(TelemetryEventKeys.Error, errorLog.Message);
            Assert.Equal(LogLevel.Information, errorLog.Level);
            Assert.Equal(default, errorLog.TraceId);
            Assert.Equal(default, errorLog.SpanId);
            Assert.Collection(errorLog.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
                tag => Assert.Equal(TelemetryPropertyKeys.ExceptionRuntimeVersion, tag.Key),
                tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ExceptionType, typeof(InvalidOperationException).FullName), tag),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
                tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.Error), tag),
                tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.Error), tag));
            Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void RecordError_NotSampled_StillRecordsSanitizedLog()
    {
        using var fixture = new DashboardTelemetryFixture(sampleResult: ActivitySamplingResult.None);

        fixture.Telemetry.RecordError("Secret local message", new InvalidOperationException("secret"), writeToLogging: false);

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.Error, log.Message);
        Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
            tag => Assert.Equal(TelemetryPropertyKeys.ExceptionRuntimeVersion, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ExceptionType, typeof(InvalidOperationException).FullName), tag),
            tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.Error), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.Error), tag));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.Empty(fixture.LocalLogSink.Writes);
    }

    [Fact]
    public void RecordError_WithReportedAncestor_UsesAmbientLogCorrelationWithoutCreatingActivity()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        using var diagnosticListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == fixture.ActivitySourceName + ".Diagnostics",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(diagnosticListener);
        using var parent = service.StartReportedActivity("dashboard-operation");
        Assert.NotNull(parent);
        using (var child = service.StartDiagnosticActivity("diagnostic-operation"))
        {
            Assert.NotNull(child);
            service.RecordError("Handled error", new InvalidOperationException("secret"), writeToLogging: false);
            Assert.Same(child, Activity.Current);
            Assert.Empty(child.Events);
            Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
            Assert.Equal(TelemetryEventKeys.Error, log.Message);
            Assert.Equal(child.TraceId, log.TraceId);
            Assert.Equal(child.SpanId, log.SpanId);
            Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        }

        Assert.Empty(parent.Events);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        parent.Stop();
        Assert.True(fixture.ActivityChannel.Reader.TryRead(out var activity));
        Assert.Same(parent, activity);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordError_WithOnlyFrameworkActivity_LogsWithoutCreatingActivity()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        using var frameworkActivity = new Activity("Blazor").Start();

        service.RecordError("Unhandled error", new InvalidOperationException(), writeToLogging: false);

        Assert.Same(frameworkActivity, Activity.Current);
        Assert.Empty(frameworkActivity.Events);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.Error, log.Message);
        Assert.Equal(frameworkActivity.TraceId, log.TraceId);
        Assert.Equal(frameworkActivity.SpanId, log.SpanId);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void RecordError_AfterReportedActivityStops_LogsWithoutCreatingActivity()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        using var previous = service.StartReportedActivity("component-event");
        Assert.NotNull(previous);
        previous.Stop();
        Assert.True(fixture.ActivityChannel.Reader.TryRead(out _));
        var current = Activity.Current;

        service.RecordError("Blazor global error", new InvalidOperationException(), writeToLogging: false);

        Assert.Same(current, Activity.Current);
        Assert.Empty(previous.Events);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.Error, log.Message);
        Assert.Equal(current?.TraceId ?? default, log.TraceId);
        Assert.Equal(current?.SpanId ?? default, log.SpanId);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void TelemetryDisabled_SuppressesUsageAndErrorsButPreservesLocalLogging()
    {
        var sink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(sink)));
        using var fixture = new DashboardTelemetryFixture(reportedTelemetryEnabled: false, logger: loggerFactory.CreateLogger<DashboardTelemetryService>());
        var service = fixture.Telemetry;

        var activity = service.StartOperation(TelemetryEventKeys.ExecuteCommand, []);
        Assert.Null(activity);
        service.SetOperationResult(activity, TelemetryResult.Success);
        service.RecordEvent(TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);
        service.RecordError("Local error", new InvalidOperationException("secret"), writeToLogging: true);

        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
        Assert.Equal("Local error", Assert.Single(sink.Writes).Message);
    }

    [Fact]
    public void Recording_ReportsOnlyAllowedPropertiesAndNumericMetrics()
    {
        using var fixture = new DashboardTelemetryFixture();
        var service = fixture.Telemetry;
        Dictionary<string, AspireTelemetryProperty> properties = new()
        {
            [TelemetryPropertyKeys.DashboardComponentId] = new("Metrics"),
            [TelemetryPropertyKeys.MetricsInstrumentsCount] = new("12", AspireTelemetryPropertyType.Metric),
            [TelemetryPropertyKeys.StructuredLogsFilterCount] = new("NaN", AspireTelemetryPropertyType.Metric),
            [TelemetryPropertyKeys.ResourceType] = new("secret", AspireTelemetryPropertyType.Pii),
            [TelemetryPropertyKeys.ExceptionMessage] = new("secret"),
            [TelemetryPropertyKeys.ExceptionStackTrace] = new("secret"),
            [TelemetryPropertyKeys.ConsoleLogsResourceName] = new("secret"),
            [TelemetryPropertyKeys.UserAgent] = new("secret"),
            [TelemetryPropertyKeys.DashboardVersion] = new("secret"),
            [TelemetryPropertyKeys.DashboardBuildId] = new("secret"),
            [TelemetryPropertyKeys.ExceptionType] = new("secret"),
            [TelemetryPropertyKeys.ExceptionRuntimeVersion] = new("secret"),
            ["aspire.dashboard.result"] = new("secret"),
            ["Unknown"] = new("secret")
        };
        using var activity = service.StartOperation(TelemetryEventKeys.ExecuteCommand, properties);
        Assert.NotNull(activity);
        var defaultVersion = activity.GetTagItem(TelemetryPropertyKeys.DashboardVersion);
        var defaultBuildId = activity.GetTagItem(TelemetryPropertyKeys.DashboardBuildId);
        Assert.Equal(Aspire.Shared.AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly), defaultVersion);
        Assert.Equal(Aspire.Shared.AssemblyVersionHelper.GetFileVersion(typeof(DashboardWebApplication).Assembly), defaultBuildId);
        service.SetOperationResult(activity, TelemetryResult.Success);

        service.RecordEvent(TelemetryEventKeys.ParametersSet, TelemetryResult.Success, properties);

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(activity.TagObjects.Append(new("microsoft.operation_name", TelemetryEventKeys.ParametersSet)).OrderBy(t => t.Key),
            log.Attributes.Where(t => t.Key != "{OriginalFormat}").OrderBy(t => t.Key));
        Assert.Empty(activity.Events);
        Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.DashboardBuildId, defaultBuildId), tag),
            tag =>
            {
                Assert.Equal(TelemetryPropertyKeys.DashboardComponentId, tag.Key);
                Assert.Equal("Metrics", tag.Value);
            },
            tag =>
            {
                Assert.Equal(TelemetryPropertyKeys.MetricsInstrumentsCount, tag.Key);
                Assert.Equal(12d, tag.Value);
            },
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.DashboardVersion, defaultVersion), tag),
            tag =>
            {
                Assert.Equal("aspire.dashboard.result", tag.Key);
                Assert.Equal("Success", tag.Value);
            },
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ParametersSet), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ParametersSet), tag));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }
}
