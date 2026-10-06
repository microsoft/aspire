// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class TelemetryLoggerProviderTests
{
    [Fact]
    public void Log_CircuitAggregateException_RecordsSanitizedChildExceptionOnce()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(fixture.Telemetry)
            .AddLogging()
            .AddSingleton<ILoggerProvider, TelemetryLoggerProvider>()
            .AddSingleton<ITelemetryErrorRecorder, TelemetryErrorRecorder>()
            .BuildServiceProvider();

        var logger = serviceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(TelemetryLoggerProvider.CircuitHostLogCategory);
        var exception = new InvalidOperationException("JavaScript interop calls cannot be issued at this time.");
        ExceptionDispatchInfo.SetRemoteStackTrace(exception, "component disposal stack");

        logger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId,
            new AggregateException(exception, exception), "Unhandled exception in circuit");

        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        TelemetryErrorRecorderTests.AssertError(log, exception);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Log_DifferentCategoryAndEventIds_WriteTelemetryForBlazorUnhandedErrorAsync(bool withActivity)
    {
        // Arrange
        using var fixture = new DashboardTelemetryFixture();
        using var frameworkActivity = withActivity ? new Activity("Blazor").Start() : null;
        var current = Activity.Current;

        using var serviceProvider = new ServiceCollection()
            .AddSingleton(fixture.Telemetry)
            .AddLogging()
            .AddSingleton<ILoggerProvider, TelemetryLoggerProvider>()
            .AddSingleton<ITelemetryErrorRecorder, TelemetryErrorRecorder>()
            .BuildServiceProvider();

        var loggerProvider = serviceProvider.GetRequiredService<ILoggerFactory>();

        // Act & assert 1
        var testLogger = loggerProvider.CreateLogger("testLogger");
        testLogger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId, "Test message");
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));

        // Act & assert 2
        var circuitHostLogger = loggerProvider.CreateLogger(TelemetryLoggerProvider.CircuitHostLogCategory);
        circuitHostLogger.LogInformation("Test log message");
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));

        // Act & assert 3
        circuitHostLogger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId, "Test message");
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));

        // Act & assert 4
        circuitHostLogger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId, new InvalidOperationException("Exception message"), "Test message");
        Assert.Same(current, Activity.Current);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.Error, log.Message);
        Assert.Equal(current?.TraceId ?? default, log.TraceId);
        Assert.Equal(current?.SpanId ?? default, log.SpanId);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }
}
