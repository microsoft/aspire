// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

[Collection(DashboardTelemetryEnvironmentCollection.Name)]
public class DashboardTelemetryManagerTests : IDisposable
{
    private readonly string? _originalCloudRoleName = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME");

    public void Dispose() => Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME", _originalCloudRoleName);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_SetsAzureCloudRoleBeforeCreatingExportersOnlyWhenEnabled(bool enabled)
    {
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME", "existing-role");
        var traceCreated = false;
        var logConfigured = false;
        var services = new ServiceCollection();
        ConfigureServices(services, enabled);
        services.AddSingleton(services => new DashboardTelemetryManager(
            services.GetRequiredService<DashboardTelemetryConfiguration>(),
            services.GetRequiredService<ILogger<DashboardTelemetryManager>>(),
            services.GetRequiredService<LoggerProvider>(),
            _ =>
            {
                Assert.Equal("ddc-cor-prd-usce-ai-aspiredashboard",
                    Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME"));
                traceCreated = true;
                return Sdk.CreateTracerProviderBuilder().Build();
            },
            _ =>
            {
                Assert.True(traceCreated);
                Assert.Equal("ddc-cor-prd-usce-ai-aspiredashboard",
                    Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME"));
                logConfigured = true;
                return services.GetRequiredService<LoggerProvider>();
            }));
        await using var serviceProvider = services.BuildServiceProvider();
        var manager = serviceProvider.GetRequiredService<DashboardTelemetryManager>();

        manager.Initialize();

        Assert.True(manager.IsInitialized);
        Assert.Equal(enabled, traceCreated);
        Assert.Equal(enabled, logConfigured);
        Assert.Equal(enabled ? "ddc-cor-prd-usce-ai-aspiredashboard" : "existing-role",
            Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME"));
        var resource = serviceProvider.GetRequiredService<LoggerProvider>().GetResource();
        Assert.Equal("aspire-dashboard", resource.Attributes.Single(attribute => attribute.Key == "service.name").Value);
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
        using var source = new ActivitySource($"Test.Dashboard.Startup.{Guid.NewGuid():N}");
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            ConfigureServices(services, enabled: true);
            services.AddLogging(builder => builder.AddProvider(new TestLoggerProvider(sink)));
            services.AddSingleton(services => new DashboardTelemetryManager(
                services.GetRequiredService<DashboardTelemetryConfiguration>(),
                services.GetRequiredService<ILogger<DashboardTelemetryManager>>(),
                services.GetRequiredService<LoggerProvider>(),
                _ =>
                {
                    if (failTrace && failInitialization)
                    {
                        throw failure;
                    }

                    return Sdk.CreateTracerProviderBuilder().AddSource(source.Name).Build();
                },
                _ =>
                {
                    if (!failTrace && failInitialization)
                    {
                        throw failure;
                    }

                    return services.GetRequiredService<LoggerProvider>();
                }));
        }).Build();
        var manager = host.Services.GetRequiredService<DashboardTelemetryManager>();

        await host.StartAsync(CancellationToken.None);

        Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.False(manager.IsInitialized);
        Assert.False(source.HasListeners());
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
            services.GetRequiredService<LoggerProvider>(),
            _ => Sdk.CreateTracerProviderBuilder().AddSource(DashboardTelemetryService.ReportedActivitySourceName).Build(),
            _ => services.GetRequiredService<LoggerProvider>()));

        return services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services, bool enabled)
    {
        services.AddSingleton(new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = enabled });
        services.AddLogging(DashboardTelemetryManager.ConfigureEventLogging);
        services.AddSingleton<DashboardTelemetryService>();
        services.AddSingleton<DashboardTelemetryManager>();
        services.AddHostedService(services => services.GetRequiredService<DashboardTelemetryManager>());

    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class DashboardTelemetryEnvironmentCollection
{
    public const string Name = "Dashboard telemetry environment";
}
