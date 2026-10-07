// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryManagerTests
{
    [Fact]
    public async Task ManagerDisposal_ReleasesProductLoggingWithoutDisposingApplicationLogging()
    {
        var sink = new TestSink();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(sink)));
        using var telemetry = new DashboardTelemetryService(factory.CreateLogger<DashboardTelemetryService>(),
            new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = true });
        var exporter = new TestDashboardTelemetryLogExporter();
        var configured = 0;
        using var source = new ActivitySource($"Test.Dashboard.Ownership.{Guid.NewGuid():N}");
        await using var manager = new DashboardTelemetryManager(
            new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = true },
            factory.CreateLogger<DashboardTelemetryManager>(), telemetry,
            (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource,
                DashboardTelemetryService.EventLogCategoryName,
                () => Sdk.CreateTracerProviderBuilder().AddSource(source.Name).Build(), provider =>
                {
                    configured++;
                    provider.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
                }));
        manager.Initialize();
        manager.Initialize();
        Assert.Equal(1, configured);
        Assert.True(source.HasListeners());

        telemetry.RecordEvent(TelemetryEventKeys.ComponentInitialize);
        Assert.True(exporter.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.ComponentInitialize, log.Message);
        Assert.Empty(sink.Writes);

        telemetry.Dispose();
        telemetry.Dispose();
        Assert.False(exporter.IsDisposed);
        Assert.True(source.HasListeners());
        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.True(exporter.IsDisposed);
        Assert.False(source.HasListeners());
        Assert.Throws<ObjectDisposedException>(manager.Initialize);
        factory.CreateLogger("Microsoft.AspNetCore").LogWarning("Still logging locally");
        Assert.Equal("Still logging locally", Assert.Single(sink.Writes).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_UsesAzureResourceOnlyWhenEnabled(bool enabled)
    {
        Resource? traceResource = null;
        Resource? logResource = null;
        var services = new ServiceCollection();
        ConfigureServices(services, enabled);
        services.AddSingleton(services => new DashboardTelemetryManager(
            services.GetRequiredService<DashboardTelemetryConfiguration>(),
            services.GetRequiredService<ILogger<DashboardTelemetryManager>>(),
            services.GetRequiredService<DashboardTelemetryService>(),
            (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource, DashboardTelemetryService.EventLogCategoryName,
                () =>
                {
                    var provider = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).Build();
                    traceResource = provider.GetResource();
                    return provider;
                },
                provider =>
                {
                    Assert.NotNull(traceResource);
                    logResource = provider.GetResource();
                })));
        await using var serviceProvider = services.BuildServiceProvider();
        var manager = serviceProvider.GetRequiredService<DashboardTelemetryManager>();

        manager.Initialize();

        Assert.True(manager.IsInitialized);
        if (enabled)
        {
            Assert.NotNull(traceResource);
            Assert.NotNull(logResource);
            Assert.Equal(traceResource.Attributes.ToArray(), logResource.Attributes.ToArray());
            var expectedVersion = AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly);
            Assert.NotEmpty(expectedVersion);
            Assert.Collection(traceResource.Attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal),
                attribute =>
                {
                    Assert.Equal("service.instance.id", attribute.Key);
                    Assert.True(Guid.TryParse(Assert.IsType<string>(attribute.Value), out var instanceId));
                    Assert.NotEqual(Guid.Empty, instanceId);
                },
                attribute => Assert.Equal(new KeyValuePair<string, object>("service.name", "aspire-dashboard"), attribute),
                attribute => Assert.Equal(new KeyValuePair<string, object>("service.version", expectedVersion), attribute));
        }
        else
        {
            Assert.Null(traceResource);
            Assert.Null(logResource);
        }
        Assert.Null(serviceProvider.GetService<LoggerProvider>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_ConcurrentFirstCalls_AreIdempotentAndHonorEnablement(bool enabled)
    {
        await using var services = CreateServices(enabled);
        var manager = services.GetRequiredService<DashboardTelemetryManager>();
        var telemetry = services.GetRequiredService<DashboardTelemetryService>();
        Assert.Same(manager, Assert.Single(services.GetServices<IHostedService>()));
        Assert.False(manager.IsInitialized);
        Assert.Equal(enabled, telemetry.IsTelemetryEnabled);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializations = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            manager.Initialize();
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(initializations);

        Assert.True(manager.IsInitialized);
        Assert.Equal(enabled, telemetry.IsTelemetryEnabled);

        await manager.StartAsync(CancellationToken.None);

        Assert.True(manager.IsInitialized);
        Assert.Equal(enabled, telemetry.IsTelemetryEnabled);
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StartAsync_ExporterInitializationFails_LogsAndAllowsHostStartup(bool failTrace, bool retry)
    {
        var sink = new TestSink();
        var failure = new UnauthorizedAccessException("Telemetry storage is not writable.");
        var failInitialization = true;
        var logExporters = new List<TestDashboardTelemetryLogExporter>();
        using var source = new ActivitySource($"Test.Dashboard.Startup.{Guid.NewGuid():N}");
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            ConfigureServices(services, enabled: true);
            services.AddLogging(builder => builder.AddProvider(new TestLoggerProvider(sink)));
            services.AddSingleton(services => new DashboardTelemetryManager(
                services.GetRequiredService<DashboardTelemetryConfiguration>(),
                services.GetRequiredService<ILogger<DashboardTelemetryManager>>(),
                services.GetRequiredService<DashboardTelemetryService>(),
                (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource, DashboardTelemetryService.EventLogCategoryName,
                    () =>
                    {
                        if (failTrace && failInitialization)
                        {
                            throw failure;
                        }

                        return Sdk.CreateTracerProviderBuilder().AddSource(source.Name).Build();
                    },
                    provider =>
                    {
                        var exporter = new TestDashboardTelemetryLogExporter();
                        logExporters.Add(exporter);
                        provider.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
                        if (!failTrace && failInitialization)
                        {
                            throw failure;
                        }
                    })));
        }).Build();
        var manager = host.Services.GetRequiredService<DashboardTelemetryManager>();

        await host.StartAsync(CancellationToken.None);

        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.False(manager.IsInitialized);
        Assert.False(source.HasListeners());
        Assert.All(logExporters, exporter => Assert.True(exporter.IsDisposed));
        Assert.True(host.Services.GetRequiredService<DashboardTelemetryService>().IsTelemetryEnabled);
        var warning = Assert.Single(sink.Writes, write => write.LoggerName == typeof(DashboardTelemetryManager).FullName);
        Assert.Equal(LogLevel.Warning, warning.LogLevel);
        Assert.Same(failure, warning.Exception);
        Assert.Equal("Failed to initialize dashboard product telemetry. The dashboard will continue without product export.", warning.Message);

        if (retry)
        {
            failInitialization = false;
            manager.Initialize();
            Assert.True(manager.IsInitialized);
            Assert.True(source.HasListeners());
        }

        await host.StopAsync(CancellationToken.None);
        Assert.False(manager.IsInitialized);
        Assert.False(source.HasListeners());
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore");
        logger.LogWarning("Still logging locally");
        Assert.Single(sink.Writes, write => write.Message == "Still logging locally");
        Assert.Throws<ObjectDisposedException>(manager.Initialize);
    }

    private static ServiceProvider CreateServices(bool enabled)
    {
        var services = new ServiceCollection();
        ConfigureServices(services, enabled);

        // Lifecycle tests exercise real SDK providers without creating Azure exporters.
        services.AddSingleton(services => new DashboardTelemetryManager(
            services.GetRequiredService<DashboardTelemetryConfiguration>(),
            services.GetRequiredService<ILogger<DashboardTelemetryManager>>(),
            services.GetRequiredService<DashboardTelemetryService>(),
            (resource, _) => AzureMonitorTelemetryProvider.Create(new ServiceCollection(), resource, DashboardTelemetryService.EventLogCategoryName,
                () => Sdk.CreateTracerProviderBuilder().AddSource(DashboardTelemetryService.ReportedActivitySourceName).Build(), _ => { })));

        return services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services, bool enabled)
    {
        services.AddSingleton(new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = enabled });
        services.AddLogging();
        services.AddSingleton<DashboardTelemetryService>();
        services.AddSingleton<DashboardTelemetryManager>();
        services.AddHostedService(services => services.GetRequiredService<DashboardTelemetryManager>());

    }
}
