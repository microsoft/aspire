// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Logs;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryLogExportTests
{
    [Theory]
    [InlineData("OpenTelemetry")]
    [InlineData("OpenTelemetry.Logs.OpenTelemetryLoggerProvider")]
    public void ConfiguredCategoryRules_CannotExportOrdinaryLogsOrVerboseProductLogs(string providerName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Logging:{providerName}:LogLevel:Microsoft.AspNetCore"] = "Trace",
            [$"Logging:{providerName}:LogLevel:{DashboardTelemetryService.EventLogCategoryName}"] = "Trace"
        }).Build();
        var sink = new TestSink();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            DashboardTelemetryManager.ConfigureEventLogging(builder);
            builder.AddProvider(new TestLoggerProvider(sink));
        });
        services.AddSingleton(new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = true });
        services.AddSingleton<DashboardTelemetryService>();
        var exporter = new TestDashboardTelemetryLogExporter();

        using (var serviceProvider = services.BuildServiceProvider())
        {
            var provider = serviceProvider.GetRequiredService<LoggerProvider>();
            var processor = new FilteredBatchLogRecordExportProcessor(exporter,
                record => DashboardTelemetryManager.IsEventLog(record.CategoryName, record.LogLevel));
            provider.AddProcessor(processor);
            Assert.Same(provider, processor.ParentProvider);
            Assert.Same(provider, exporter.ParentProvider);
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var frameworkLogger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Test");
            Assert.True(frameworkLogger.IsEnabled(LogLevel.Information));
            frameworkLogger.LogInformation("Ordinary framework log");
            frameworkLogger.LogError(new InvalidOperationException("secret exception"), "Raw framework error");

            var otherEventLogger = loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName + ".Other");
            Assert.True(otherEventLogger.IsEnabled(LogLevel.Information));
            otherEventLogger.LogInformation("Wrong event category");
            var eventLogger = loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName);
            Assert.True(eventLogger.IsEnabled(LogLevel.Debug));
            eventLogger.LogDebug("Verbose product log");

            using var activity = new Activity("ambient").Start();
            using (eventLogger.BeginScope(new Dictionary<string, object?> { ["secret"] = "workspace path" }))
            {
                serviceProvider.GetRequiredService<DashboardTelemetryService>().RecordEvent(
                    TelemetryEventKeys.ComponentInitialize, TelemetryResult.Success);
            }
            Assert.True(provider.ForceFlush(timeoutMilliseconds: 5000));

            Assert.True(exporter.LogChannel.Reader.TryRead(out var log));
            Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
            Assert.Equal(DashboardTelemetryService.EventLogCategoryName, log.CategoryName);
            Assert.Equal(LogLevel.Information, log.Level);
            Assert.Equal(activity.TraceId, log.TraceId);
            Assert.Equal(activity.SpanId, log.SpanId);
            Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
                tag => Assert.Equal(new KeyValuePair<string, object?>("aspire.dashboard.result", "Success"), tag),
                tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ComponentInitialize), tag));
            Assert.False(exporter.LogChannel.Reader.TryPeek(out _));
            Assert.Collection(sink.Writes,
                local => Assert.Equal("Ordinary framework log", local.Message),
                local => Assert.Equal("Raw framework error", local.Message));

            serviceProvider.GetRequiredService<DashboardTelemetryService>().RecordEvent(
                TelemetryEventKeys.ComponentDispose, TelemetryResult.Success);
            Assert.True(provider.Shutdown(timeoutMilliseconds: 5000));
            Assert.True(exporter.LogChannel.Reader.TryRead(out var shutdownLog));
            Assert.Equal(TelemetryEventKeys.ComponentDispose, shutdownLog.Message);
            Assert.False(exporter.LogChannel.Reader.TryPeek(out _));
            Assert.False(exporter.IsDisposed);
        }

        Assert.True(exporter.IsDisposed);
    }
}
