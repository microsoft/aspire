// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Aspire.Shared.Telemetry;
using OpenTelemetry.Resources;

namespace Aspire.Dashboard.Telemetry;

/// <summary>
/// Owns the dashboard's Azure Monitor reported trace and usage-log providers.
/// </summary>
internal sealed class DashboardTelemetryManager : IHostedService, IAsyncDisposable
{
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=3be364e3-d9eb-436a-983e-0a681d5af691;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=83cb9aa6-6ebc-4c33-b434-8d348004bde1";
    private const int ShutdownTimeoutMilliseconds = 5000;

    private readonly Lock _lock = new();
    private readonly DashboardTelemetryConfiguration _configuration;
    private readonly ILogger<DashboardTelemetryManager> _logger;
    private readonly DashboardTelemetryService _telemetry;
    private readonly Func<ResourceBuilder, string, AzureMonitorTelemetryProvider> _createProvider;
    private AzureMonitorTelemetryProvider? _provider;
    private bool _initialized;
    private bool _disposed;
    private Task? _shutdownTask;

    public DashboardTelemetryManager(
        DashboardTelemetryConfiguration configuration,
        ILogger<DashboardTelemetryManager> logger,
        DashboardTelemetryService telemetry)
    {
        _configuration = configuration;
        _logger = logger;
        _telemetry = telemetry;
        _createProvider = (resource, storageDirectory) => AzureMonitorTelemetryProvider.Create(
            new ServiceCollection(), resource, DashboardTelemetryService.ReportedActivitySourceName,
            DashboardTelemetryService.EventLogCategoryName, ApplicationInsightsConnectionString, storageDirectory);
    }

    internal DashboardTelemetryManager(
        DashboardTelemetryConfiguration configuration,
        ILogger<DashboardTelemetryManager> logger,
        DashboardTelemetryService telemetry,
        Func<ResourceBuilder, string, AzureMonitorTelemetryProvider> createProvider) : this(configuration, logger, telemetry)
    {
        _createProvider = createProvider;
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

            AzureMonitorTelemetryProvider? provider = null;
            try
            {
                var storageDirectory = AspireTelemetryExporter.GetTelemetryStoragePath("dashboard");
                var resource = CreateResourceBuilder();
                provider = _createProvider(resource, storageDirectory);
                _telemetry.SetEventLogger(provider.EventLogger);

                _provider = provider;
                _initialized = true;
            }
            catch (Exception ex)
            {
                provider?.Dispose();
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
    // Cancelling the wait would not stop the synchronous SDK drain. Complete its bounded
    // persistence and disposal even when the host's graceful shutdown deadline has expired.
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
            _telemetry.SetEventLogger(null);
            _shutdownTask = FlushProviderAsync(provider);
            return _shutdownTask;
        }
    }

    private async Task FlushProviderAsync(AzureMonitorTelemetryProvider? provider)
    {
        using (provider)
        {
            // Unlike a short-lived CLI command, retain the exporter's default two-second drain
            // opportunity after persistence, within the five-second provider shutdown window.
            if (provider is not null && !await provider.ShutdownAsync(ShutdownTimeoutMilliseconds).ConfigureAwait(false))
            {
                _logger.LogWarning("Timed out flushing dashboard product telemetry.");
            }
        }
    }

    private static ResourceBuilder CreateResourceBuilder() => ResourceBuilder.CreateEmpty().AddService(
        serviceName: "aspire-dashboard",
        serviceVersion: AssemblyVersionHelper.GetInformationalVersion(typeof(DashboardWebApplication).Assembly));
}
