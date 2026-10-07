// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class TelemetryLoggerProviderTests
{
    [Fact]
    public async Task Log_CircuitAggregateException_RecordsChildStackTraceOnce()
    {
        await using var telemetrySender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await telemetrySender.TryStartTelemetrySessionAsync();
        using var serviceProvider = new ServiceCollection()
            .AddSingleton<DashboardTelemetryService>()
            .AddSingleton<IDashboardTelemetrySender>(telemetrySender)
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

        var request = Assert.Single(await TelemetryErrorRecorderTests.ReadFaultRequestsAsync(telemetrySender));
        Assert.NotNull(request.Properties);
        Assert.Equal(new AspireTelemetryProperty(typeof(InvalidOperationException).FullName!), request.Properties[TelemetryPropertyKeys.ExceptionType]);
        Assert.Equal(new AspireTelemetryProperty(exception.Message), request.Properties[TelemetryPropertyKeys.ExceptionMessage]);
        Assert.Equal(new AspireTelemetryProperty(exception.StackTrace!), request.Properties[TelemetryPropertyKeys.ExceptionStackTrace]);
    }

    [Fact]
    public async Task Log_DifferentCategoryAndEventIds_WriteTelemetryForBlazorUnhandedErrorAsync()
    {
        // Arrange
        var telemetrySender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await telemetrySender.TryStartTelemetrySessionAsync();

        var serviceProvider = new ServiceCollection()
            .AddSingleton<DashboardTelemetryService>()
            .AddSingleton<IDashboardTelemetrySender>(telemetrySender)
            .AddLogging()
            .AddSingleton<ILoggerProvider, TelemetryLoggerProvider>()
            .AddSingleton<ITelemetryErrorRecorder, TelemetryErrorRecorder>()
            .BuildServiceProvider();

        var loggerProvider = serviceProvider.GetRequiredService<ILoggerFactory>();

        // Act & assert 1
        var testLogger = loggerProvider.CreateLogger("testLogger");
        testLogger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId, "Test message");
        Assert.False(telemetrySender.ContextChannel.Reader.TryPeek(out _));

        // Act & assert 2
        var circuitHostLogger = loggerProvider.CreateLogger(TelemetryLoggerProvider.CircuitHostLogCategory);
        circuitHostLogger.LogInformation("Test log message");
        Assert.False(telemetrySender.ContextChannel.Reader.TryPeek(out _));

        // Act & assert 3
        circuitHostLogger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId, "Test message");
        Assert.False(telemetrySender.ContextChannel.Reader.TryPeek(out _));

        // Act & assert 4
        circuitHostLogger.Log(LogLevel.Error, TelemetryLoggerProvider.CircuitUnhandledExceptionEventId, new InvalidOperationException("Exception message"), "Test message");
        Assert.True(telemetrySender.ContextChannel.Reader.TryPeek(out var context));
        Assert.Equal("/telemetry/fault - $aspire/dashboard/error", context.Name);
    }
}
