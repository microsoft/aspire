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
internal sealed class DashboardTelemetryManager : IHostedService, IAsyncDisposable
{
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=3be364e3-d9eb-436a-983e-0a681d5af691;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=83cb9aa6-6ebc-4c33-b434-8d348004bde1";
    private const string ApplicationInsightsCloudRoleName = "ddc-cor-prd-usce-ai-aspiredashboard";

    private readonly Lock _lock = new();
    private readonly DashboardTelemetryConfiguration _configuration;
    private readonly ILogger<DashboardTelemetryManager> _logger;
    private readonly Func<string, TracerProvider> _createTraceProvider = CreateTraceProvider;
    private readonly Func<string, LoggerProvider> _configureLogProvider;
    private TracerProvider? _provider;
    private LoggerProvider? _logProvider;
    private bool _initialized;
    private bool _disposed;
    private Task? _shutdownTask;

    public DashboardTelemetryManager(
        DashboardTelemetryConfiguration configuration,
        ILogger<DashboardTelemetryManager> logger,
        LoggerProvider logProvider)
    {
        _configuration = configuration;
        _logger = logger;
        _configureLogProvider = storageDirectory =>
            logProvider.AddAspireAzureMonitorExporter(ApplicationInsightsConnectionString, Path.Combine(storageDirectory, "logs"),
                record => IsEventLog(record.CategoryName, record.LogLevel));
    }

    internal DashboardTelemetryManager(
        DashboardTelemetryConfiguration configuration,
        ILogger<DashboardTelemetryManager> logger,
        LoggerProvider logProvider,
        Func<string, TracerProvider> createTraceProvider,
        Func<string, LoggerProvider> configureLogProvider) : this(configuration, logger, logProvider)
    {
        _createTraceProvider = createTraceProvider;
        _configureLogProvider = configureLogProvider;
    }

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

            if (!_configuration.ReportedTelemetryEnabled)
            {
                _initialized = true;
                _logger.LogDebug("Dashboard product telemetry is disabled.");
                return;
            }

            try
            {
                // Override only Azure Monitor's cloud role, leaving the OpenTelemetry resource
                // identity unchanged for other exporters. This override is process-wide and the
                // Azure exporter caches it on first export, so set it before either provider can
                // export telemetry and keep it unchanged for the dashboard's lifetime.
                // https://github.com/Azure/azure-sdk-for-net/blob/Azure.Monitor.OpenTelemetry.Exporter_1.9.0/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/src/Customizations/Models/TelemetryItem.cs
                Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_CLOUD_ROLE_NAME",
                    ApplicationInsightsCloudRoleName, EnvironmentVariableTarget.Process);

                var storageDirectory = AspireTelemetryExporter.GetTelemetryStoragePath("dashboard");
                var provider = _createTraceProvider(storageDirectory);

                try
                {
                    _logProvider = _configureLogProvider(storageDirectory);
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
            catch (Exception ex)
            {
                // Product telemetry is optional; exporter or storage failures must not
                // prevent users from starting the dashboard.
                _logger.LogWarning(ex, "Failed to initialize dashboard product telemetry. The dashboard will continue without product export.");
            }
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
                    _logger.LogWarning("Timed out flushing dashboard product traces.");
                }
                provider.Dispose();
            }
        }), Task.Run(() =>
        {
            // Flush the application-owned provider, but leave its disposal to DI.
            if (logProvider is not null && !logProvider.Shutdown(timeoutMilliseconds: 5000))
            {
                _logger.LogWarning("Timed out flushing dashboard product usage logs.");
            }
        })).ConfigureAwait(false);
    }

    internal static void ConfigureEventLogging(ILoggingBuilder builder)
    {
        // Preserve the old telemetry-only behavior without adding usage noise to local logs.
        builder.AddFilter(DashboardTelemetryService.EventLogCategoryName, LogLevel.None);
        builder.AddFilter<OpenTelemetryLoggerProvider>(IsEventLog);
        builder.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            // Ambient scopes may contain resource names or application data.
            logging.IncludeScopes = false;
            logging.SetResourceBuilder(CreateResourceBuilder());
        });
    }

    internal static bool IsEventLog(string? category, LogLevel level) =>
        category == DashboardTelemetryService.EventLogCategoryName && level is >= LogLevel.Information and < LogLevel.None;

    private static TracerProvider CreateTraceProvider(string storageDirectory)
    {
        // This trace provider listens only to product instrumentation, never ASP.NET Core,
        // SQLite, or application telemetry ingested by the dashboard.
        return Sdk.CreateTracerProviderBuilder()
            .AddSource(DashboardTelemetryService.ReportedActivitySourceName)
            .SetResourceBuilder(CreateResourceBuilder())
            .AddAspireAzureMonitorExporter(ApplicationInsightsConnectionString, storageDirectory)
            .Build();
    }

    private static ResourceBuilder CreateResourceBuilder() => ResourceBuilder.CreateEmpty().AddService(
        serviceName: "aspire-dashboard",
        serviceVersion: AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly),
        autoGenerateServiceInstanceId: false);
}
