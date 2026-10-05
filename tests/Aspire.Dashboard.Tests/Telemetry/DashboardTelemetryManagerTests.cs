// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryManagerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_IsIdempotentAndIndependentOfConfiguredEnablement(bool enabled)
    {
        await using var services = CreateServices(enabled);
        var manager = services.GetRequiredService<DashboardTelemetryManager>();
        var telemetry = services.GetRequiredService<DashboardTelemetryService>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var eventLogger = loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName);
        Assert.Same(manager, Assert.Single(services.GetServices<IHostedService>()));
        Assert.False(manager.IsInitialized);
        Assert.Equal(enabled, telemetry.IsTelemetryEnabled);

        await manager.StartAsync(CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(manager.Initialize)));

        Assert.True(manager.IsInitialized);
        Assert.Equal(enabled, telemetry.IsTelemetryEnabled);
        Assert.Same(eventLogger, loggerFactory.CreateLogger(DashboardTelemetryService.EventLogCategoryName));
        if (enabled)
        {
            using var source = new ActivitySource(DashboardTelemetryService.ReportedActivitySourceName);
            Assert.True(source.HasListeners());
        }

        var shutdownTask = manager.StopAsync(CancellationToken.None);
        Assert.Same(shutdownTask, manager.StopAsync(CancellationToken.None));
        Assert.Same(shutdownTask, manager.DisposeAsync().AsTask());
        await shutdownTask;

        Assert.False(manager.IsInitialized);
        Assert.Equal(enabled, telemetry.IsTelemetryEnabled);
        Assert.Throws<ObjectDisposedException>(manager.Initialize);
    }

    [Fact]
    public async Task Dispose_BeforeInitialization_PreventsStartup()
    {
        await using var services = CreateServices(enabled: true);
        var manager = services.GetRequiredService<DashboardTelemetryManager>();

        await manager.DisposeAsync();

        Assert.False(manager.IsInitialized);
        Assert.Throws<ObjectDisposedException>(manager.Initialize);
    }

    [Fact]
    public async Task Shutdown_PreservesApplicationLogging()
    {
        await using var services = CreateServices(enabled: true);
        var manager = services.GetRequiredService<DashboardTelemetryManager>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var sink = new TestSink();
        loggerFactory.AddProvider(new TestLoggerProvider(sink));
        manager.Initialize();

        await manager.StopAsync(CancellationToken.None);
        loggerFactory.CreateLogger("Microsoft.AspNetCore").LogWarning("Still logging locally");

        Assert.Equal("Still logging locally", Assert.Single(sink.Writes).Message);
    }

    private static ServiceProvider CreateServices(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = enabled });
        services.AddLogging(DashboardTelemetryManager.ConfigureEventLogging);
        services.AddSingleton<DashboardTelemetryService>();
        services.AddSingleton<DashboardTelemetryManager>();
        services.AddHostedService(services => services.GetRequiredService<DashboardTelemetryManager>());

        return services.BuildServiceProvider();
    }
}
