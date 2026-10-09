// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class TelemetryErrorRecorderTests
{
    [Fact]
    public void RecordError_Exception_RecordsOriginalException()
    {
        using var fixture = new DashboardTelemetryFixture();
        var recorder = CreateRecorder(fixture);
        var exception = CreateException(new InvalidOperationException("Test error"), "original stack");

        recorder.RecordError("Test message", exception);

        AssertError(Assert.Single(ReadErrors(fixture)), exception);
    }

    [Fact]
    public void RecordError_AggregateException_RecordsDistinctLeafExceptions()
    {
        using var fixture = new DashboardTelemetryFixture();
        var recorder = CreateRecorder(fixture);
        var exception = CreateException(new InvalidOperationException("Test error"), "original stack");
        var duplicate = CreateException(new InvalidOperationException("Test error"), "original stack");
        var differentType = CreateException(new ArgumentException("Test error"), "original stack");
        var differentMessage = CreateException(new InvalidOperationException("Different error"), "original stack");
        var differentStack = CreateException(new InvalidOperationException("Test error"), "different stack");
        var aggregate = new AggregateException(
            "Exceptions were encountered while disposing components.",
            exception,
            duplicate,
            differentType,
            differentMessage,
            differentStack,
            new AggregateException(duplicate, new AggregateException(exception)));
        Assert.Null(aggregate.StackTrace);

        recorder.RecordError("Test message", aggregate);

        Assert.Collection(ReadErrors(fixture),
            log => AssertError(log, exception),
            log => AssertError(log, differentType),
            log => AssertError(log, differentMessage),
            log => AssertError(log, differentStack));
    }

    [Fact]
    public void RecordError_AggregateExceptionWithoutStacks_DeduplicatesExceptions()
    {
        using var fixture = new DashboardTelemetryFixture();
        var recorder = CreateRecorder(fixture);
        var exception = new InvalidOperationException("Test error");
        var aggregate = new AggregateException(exception, new InvalidOperationException("Test error"));

        recorder.RecordError("Test message", aggregate);

        AssertError(Assert.Single(ReadErrors(fixture)), exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordError_EmptyAggregateException_RecordsOriginalException(bool nested)
    {
        using var fixture = new DashboardTelemetryFixture();
        var recorder = CreateRecorder(fixture);
        var exception = nested ? new AggregateException(new AggregateException()) : new AggregateException();

        recorder.RecordError("Test message", exception);

        AssertError(Assert.Single(ReadErrors(fixture)), exception);
    }

    [Fact]
    public void RecordError_RepeatedCalls_RecordsEachOccurrence()
    {
        using var fixture = new DashboardTelemetryFixture();
        var recorder = CreateRecorder(fixture);
        var exception = CreateException(new InvalidOperationException("Test error"), "original stack");
        var aggregate = new AggregateException(exception, exception);

        recorder.RecordError("Test message", aggregate);
        recorder.RecordError("Test message", aggregate);

        Assert.Collection(ReadErrors(fixture),
            log => AssertError(log, exception),
            log => AssertError(log, exception));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordError_WriteToLogging_LogsOriginalExceptionOnce(bool writeToLogging)
    {
        using var fixture = new DashboardTelemetryFixture();
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var logger = new TestLogger<TelemetryErrorRecorder>(loggerFactory);
        var recorder = new TelemetryErrorRecorder(fixture.Telemetry, logger);
        var exception = new InvalidOperationException("Test error");
        var aggregate = new AggregateException(exception, exception);

        recorder.RecordError("Test message", aggregate, writeToLogging);

        var logs = sink.Writes;
        if (writeToLogging)
        {
            var log = Assert.Single(logs);
            Assert.Same(aggregate, log.Exception);
            Assert.Equal("Test message", log.Message);
        }
        else
        {
            Assert.Empty(logs);
        }

        Assert.Empty(fixture.LocalLogSink.Writes);
        AssertError(Assert.Single(ReadErrors(fixture)), exception);
    }

    [Fact]
    public void RecordError_TelemetryDisabled_DoesNotExportLogs()
    {
        using var fixture = new DashboardTelemetryFixture(reportedTelemetryEnabled: false);
        var recorder = CreateRecorder(fixture);

        recorder.RecordError("Test message", new AggregateException(new InvalidOperationException("Test error")));

        Assert.Empty(ReadErrors(fixture));
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
    }

    private static TelemetryErrorRecorder CreateRecorder(DashboardTelemetryFixture fixture) =>
        new(fixture.Telemetry, NullLogger<TelemetryErrorRecorder>.Instance);

    private static Exception CreateException(Exception exception, string stackTrace)
    {
        ExceptionDispatchInfo.SetRemoteStackTrace(exception, stackTrace);

        return exception;
    }

    internal static void AssertError(TestDashboardTelemetryLog log, Exception exception)
    {
        Assert.Equal(TelemetryEventKeys.Error, log.Message);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Equal(DashboardTelemetryService.EventLogCategoryName, log.CategoryName);
        Assert.Collection(log.Attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal),
            attribute => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, attribute.Key),
            attribute => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ExceptionRuntimeVersion, VersionHelpers.RuntimeVersion?.ToString() ?? string.Empty), attribute),
            attribute => Assert.Equal(new KeyValuePair<string, object?>(TelemetryPropertyKeys.ExceptionType, exception.GetType().FullName), attribute),
            attribute => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, attribute.Key),
            attribute => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.Error), attribute),
            attribute => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.Error), attribute));
    }

    private static List<TestDashboardTelemetryLog> ReadErrors(DashboardTelemetryFixture fixture)
    {
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        var logs = new List<TestDashboardTelemetryLog>();
        while (fixture.LogChannel.Reader.TryRead(out var log))
        {
            logs.Add(log);
        }

        return logs;
    }
}
