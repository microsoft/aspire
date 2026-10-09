// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryLogExportTests
{
    [Theory]
    [InlineData("OpenTelemetry", false)]
    [InlineData("OpenTelemetry", true)]
    [InlineData("OpenTelemetry.Logs.OpenTelemetryLoggerProvider", false)]
    [InlineData("OpenTelemetry.Logs.OpenTelemetryLoggerProvider", true)]
    public async Task ApplicationLoggingConfiguration_CannotControlProductExport(string providerName, bool suppressProductCategory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            [$"Logging:LogLevel:{DashboardTelemetryService.EventLogCategoryName}"] = suppressProductCategory ? "None" : "Trace",
            [$"Logging:{providerName}:LogLevel:Microsoft.AspNetCore"] = "Trace",
            [$"Logging:{providerName}:LogLevel:{DashboardTelemetryService.EventLogCategoryName}"] = "Trace"
        }).Build();
        var sink = new TestSink();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddProvider(new TestLoggerProvider(sink));
        });
        services.AddSingleton(new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = true });
        services.AddSingleton<DashboardTelemetryService>();
        var exporter = new TestDashboardTelemetryLogExporter();

        using (var serviceProvider = services.BuildServiceProvider())
        {
            Assert.Null(serviceProvider.GetService<LoggerProvider>());
            var telemetry = serviceProvider.GetRequiredService<DashboardTelemetryService>();
            var processor = new BatchLogRecordExportProcessor(exporter);
            using var logProvider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), ResourceBuilder.CreateEmpty(),
                DashboardTelemetryService.EventLogCategoryName, () => Sdk.CreateTracerProviderBuilder().Build(), provider =>
            {
                provider.AddProcessor(processor);
                Assert.Same(provider, processor.ParentProvider);
                Assert.Same(provider, exporter.ParentProvider);
            });
            telemetry.SetEventLogger(logProvider.EventLogger);
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var frameworkLogger = loggerFactory.CreateLogger("Microsoft.AspNetCore.Test");
            Assert.True(frameworkLogger.IsEnabled(LogLevel.Information));
            frameworkLogger.LogInformation("Ordinary framework log");
            frameworkLogger.LogError(new InvalidOperationException("secret exception"), "Raw framework error");

            var otherEventLogger = loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName + ".Other");
            Assert.Equal(!suppressProductCategory, otherEventLogger.IsEnabled(LogLevel.Information));
            otherEventLogger.LogInformation("Wrong event category");
            var eventLogger = loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName);
            Assert.Equal(!suppressProductCategory, eventLogger.IsEnabled(LogLevel.Debug));
            eventLogger.LogDebug("Verbose product log");
            eventLogger.LogInformation("Ordinary product-category log");

            using var activity = new Activity("ambient").Start();
            using (eventLogger.BeginScope(new Dictionary<string, object?> { ["secret"] = "workspace path" }))
            {
                telemetry.RecordEvent(
                    TelemetryEventKeys.ComponentInitialize);
            }
            Assert.True(await logProvider.ForceFlushAsync(timeoutMilliseconds: 5000));

            Assert.True(exporter.LogChannel.Reader.TryRead(out var log));
            Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
            Assert.Equal(DashboardTelemetryService.EventLogCategoryName, log.CategoryName);
            Assert.Equal(LogLevel.Information, log.Level);
            Assert.Equal(activity.TraceId, log.TraceId);
            Assert.Equal(activity.SpanId, log.SpanId);
            Assert.Collection(log.Attributes.OrderBy(t => t.Key, StringComparer.Ordinal),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardBuildId, tag.Key),
                tag => Assert.Equal(TelemetryPropertyKeys.DashboardVersion, tag.Key),
                tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", TelemetryEventKeys.ComponentInitialize), tag),
                tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", TelemetryEventKeys.ComponentInitialize), tag));
            Assert.False(exporter.LogChannel.Reader.TryPeek(out _));
            Assert.Equal(suppressProductCategory
                ? ["Ordinary framework log", "Raw framework error"]
                : ["Ordinary framework log", "Raw framework error", "Wrong event category", "Verbose product log", "Ordinary product-category log"],
                sink.Writes.Select(write => write.Message).ToArray());

            telemetry.RecordEvent(
                TelemetryEventKeys.ComponentDispose);
            Assert.True(await logProvider.ShutdownAsync(timeoutMilliseconds: 5000));
            Assert.True(exporter.LogChannel.Reader.TryRead(out var shutdownLog));
            Assert.Equal(TelemetryEventKeys.ComponentDispose, shutdownLog.Message);
            Assert.Equal(TelemetryEventKeys.ComponentDispose,
                shutdownLog.Attributes.Single(t => t.Key == "microsoft.operation_name").Value);
            Assert.False(exporter.LogChannel.Reader.TryPeek(out _));
            Assert.False(exporter.IsDisposed);
        }

        Assert.True(exporter.IsDisposed);
    }

}
