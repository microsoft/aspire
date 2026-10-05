// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Aspire.Shared.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Dashboard.Telemetry;

/// <summary>
/// Owns the dashboard's Azure Monitor reported trace and usage-log providers.
/// </summary>
internal sealed class DashboardTelemetryManager(
    DashboardTelemetryConfiguration configuration,
    ILogger<DashboardTelemetryManager> logger,
    LoggerProvider logProvider) : IHostedService, IAsyncDisposable
{
    // TODO: Replace this placeholder with the dashboard's Application Insights connection string.
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://dashboard-telemetry.invalid/";

    private readonly Lock _lock = new();
    private TracerProvider? _provider;
    private LoggerProvider? _logProvider;
    private bool _initialized;
    private bool _disposed;
    private Task? _shutdownTask;

    internal bool IsInitialized
    {
        get
        {
            lock (_lock)
            {
                return _initialized && !_disposed;
            }
        }
    }

    /// <summary>
    /// Creates the reported providers once, before dashboard usage can be recorded.
    /// </summary>
    internal void Initialize()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return;
            }

            if (!configuration.ReportedTelemetryEnabled)
            {
                _initialized = true;
                logger.LogDebug("Dashboard product telemetry is disabled.");
                return;
            }

            var storageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "dashboard", "telemetrystorage");

            // This trace provider listens only to product instrumentation, never ASP.NET Core,
            // SQLite, or application telemetry ingested by the dashboard.
            var provider = Sdk.CreateTracerProviderBuilder()
                .AddSource(DashboardTelemetryService.ReportedActivitySourceName)
                .SetResourceBuilder(CreateResourceBuilder())
                .AddAspireAzureMonitorExporter(ApplicationInsightsConnectionString, storageDirectory)
                .Build();

            try
            {
                _logProvider = logProvider.AddAspireAzureMonitorExporter(ApplicationInsightsConnectionString, Path.Combine(storageDirectory, "logs"));
            }
            catch
            {
                // A failed log exporter must not leave a trace provider alive or mark
                // initialization complete, so a subsequent attempt can safely retry.
                provider.Shutdown(timeoutMilliseconds: 0);
                provider.Dispose();
                throw;
            }

            _provider = provider;
            _initialized = true;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Initialize();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => ShutdownAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private Task ShutdownAsync()
    {
        lock (_lock)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }
            _disposed = true;
            var provider = _provider;
            _provider = null;
            var logProvider = _logProvider;
            _logProvider = null;

            _shutdownTask = FlushProvidersAsync(provider, logProvider);
            return _shutdownTask;
        }
    }

    private async Task FlushProvidersAsync(TracerProvider? provider, LoggerProvider? logProvider)
    {
        // Flush both providers concurrently so the shutdown wait remains bounded.
        await Task.WhenAll(Task.Run(() =>
        {
            if (provider is not null)
            {
                if (!provider.Shutdown(timeoutMilliseconds: 5000))
                {
                    logger.LogWarning("Timed out flushing dashboard product traces.");
                }
                provider.Dispose();
            }
        }), Task.Run(() =>
        {
            // Flush the application-owned provider, but leave its disposal to DI.
            if (logProvider is not null && !logProvider.Shutdown(timeoutMilliseconds: 5000))
            {
                logger.LogWarning("Timed out flushing dashboard product usage logs.");
            }
        })).ConfigureAwait(false);
    }

    internal static void ConfigureEventLogging(ILoggingBuilder builder)
    {
        // Preserve the old telemetry-only behavior without adding usage noise to local logs.
        builder.AddFilter(DashboardTelemetryService.EventLogCategoryName, LogLevel.None);
        builder.AddFilter<OpenTelemetryLoggerProvider>((category, level) =>
            category == DashboardTelemetryService.EventLogCategoryName && level is >= LogLevel.Information and < LogLevel.None);
        builder.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            // Ambient scopes may contain resource names or application data.
            logging.IncludeScopes = false;
            logging.SetResourceBuilder(CreateResourceBuilder());
        });
    }

    private static ResourceBuilder CreateResourceBuilder() => ResourceBuilder.CreateEmpty().AddService(
        serviceName: "aspire-dashboard",
        serviceVersion: AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly),
        autoGenerateServiceInstanceId: false);
}
