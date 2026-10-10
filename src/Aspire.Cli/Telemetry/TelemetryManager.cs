// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Telemetry;

// This file is the CLI's OpenTelemetry wiring point. It decides which ActivitySources
// are listened to and where they are exported; the activity creation APIs live in
// AspireCliTelemetry and ProfilingTelemetry.
//
// Keep reported telemetry, profiling telemetry, and debug diagnostics on separate providers.
// Reported telemetry is allowed to leave the machine through Azure Monitor, while
// profiling and diagnostic telemetry are intentionally local and opt-in because they can
// include high-cardinality process, path, and startup timing details.
//
// Enablement is intentionally separate:
// - Reported telemetry is on by default and is disabled with ASPIRE_CLI_TELEMETRY_OPTOUT=true.
// - Profiling telemetry requires ASPIRE_PROFILING_ENABLED=true plus OTEL_EXPORTER_OTLP_ENDPOINT
//   (and typically OTEL_EXPORTER_OTLP_PROTOCOL=grpc). ASPIRE_STARTUP_PROFILING_ENABLED is the
//   legacy alias that remains supported for existing scripts.
// - DEBUG-only diagnostics use ASPIRE_CLI_CONSOLE_EXPORTER_LEVEL=Diagnostic, or OTLP export when
//   OTEL_EXPORTER_OTLP_ENDPOINT is set without profiling enabled.

/// <summary>
/// Manages OpenTelemetry trace and reported-log export for the CLI.
/// Maintains separate providers for reported telemetry, profiling telemetry, and debug diagnostics.
/// </summary>
internal sealed class TelemetryManager : IDisposable
{
    // Remote export connection string for CLI Application Insights. Intentionally hard-coded.
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=e39510fc-95a1-423d-9f33-6121bf0d2113;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=4d8bb9db-b7ab-49f9-978b-80ae1e83f6da";

#if DEBUG
    // No timeout in debug builds
    private const int DiagnosticShutdownTimeoutMilliseconds = -1;
#else
    // Chosen to provide time to send remaining telemetry without noticeably delaying exit.
    private const int DiagnosticShutdownTimeoutMilliseconds = 200;
#endif
    private const int ProfilingForceFlushTimeoutMilliseconds = 5000;
    private const int ReportedCIShutdownTimeoutMilliseconds = 5000;
    private const int ReportedFailureShutdownTimeoutMilliseconds = 300;
    private const string ShutdownDrainBudgetKey = "Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds";
    private const string PersistOnForceFlushSwitchName = "Azure.Monitor.OpenTelemetry.Exporter.PersistOnForceFlush";
    private const string DisablePersistOnShutdownSwitchName = "Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown";

    // Agent hooks flush to durable exporter storage before returning. Give persistence its own
    // budget rather than truncating it to the normal Release shutdown window.
    private const int ReportedForceFlushTimeoutMilliseconds = 3000;

    private readonly TelemetryConfiguration _telemetryConfiguration;
    private readonly TelemetryTagsSource _tagsSource;
    private readonly AspireCliTelemetry _telemetry;
    private readonly Func<ResourceBuilder, string, AzureMonitorTelemetryProvider> _createReportedProvider;
    private readonly ILogger<TelemetryManager> _logger;
    private readonly Lock _lifecycleLock = new();
    private AzureMonitorTelemetryProvider? _azureMonitorProvider;
    private TracerProvider? _profilingProvider;
    private TracerProvider? _debugDiagnosticProvider;
    private Task<bool>? _shutdownTask;
    private LifecycleState _state;

    /// <summary>
    /// Configures exporter persistence before any providers are created in the CLI process.
    /// </summary>
    internal static ReportedTelemetryMode ConfigureExporterForProcess(bool isAgentTelemetryInvocation, bool isCIEnvironment)
    {
        // These switches are process-wide, so configure them only at the entry point, not in DI.
        // CI cannot rely on a later invocation to drain storage. Agent hooks retain their fast
        // persistence-before-uploader contract even in CI, rather than waiting on every tool call.
        // https://github.com/Azure/azure-sdk-for-net/blob/Azure.Monitor.OpenTelemetry.Exporter_1.9.0/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/README.md#telemetry-delivery-on-shutdown
        var mode = (isAgentTelemetryInvocation, isCIEnvironment) switch
        {
            (true, _) => ReportedTelemetryMode.Agent,
            (_, true) => ReportedTelemetryMode.CI,
            _ => ReportedTelemetryMode.Local
        };
        AppContext.SetSwitch(PersistOnForceFlushSwitchName, mode != ReportedTelemetryMode.CI);
        AppContext.SetSwitch(DisablePersistOnShutdownSwitchName, mode == ReportedTelemetryMode.CI);
        AppContext.SetData(ShutdownDrainBudgetKey, 0);
        return mode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="telemetryConfiguration">The telemetry configuration.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    /// <param name="telemetry">The telemetry service applying the CLI property policy.</param>
    /// <param name="logger">The logger for exporter lifecycle failures.</param>
    public TelemetryManager(TelemetryConfiguration telemetryConfiguration, TelemetryTagsSource tagsSource, AspireCliTelemetry telemetry, ILogger<TelemetryManager> logger)
    {
        _telemetryConfiguration = telemetryConfiguration;
        _tagsSource = tagsSource;
        _telemetry = telemetry;
        _logger = logger;
        _createReportedProvider = (resource, storageDirectory) => AzureMonitorTelemetryProvider.Create(
            new ServiceCollection(), resource, AspireCliTelemetry.ReportedActivitySourceName,
            AspireCliTelemetry.EventLogCategoryName, ApplicationInsightsConnectionString, storageDirectory, builder =>
            {
                builder.AddProcessor(new CliTagEnrichmentProcessor(tagsSource, telemetry));
#if DEBUG
                if (telemetryConfiguration.ConsoleExporterLevel == ConsoleExporterLevel.Reported)
                {
                    builder.AddConsoleExporter();
                }
#endif
            },
            logging =>
            {
#if DEBUG
                if (telemetryConfiguration.ConsoleExporterLevel == ConsoleExporterLevel.Reported)
                {
                    logging.AddConsoleExporter();
                }
#endif
            },
            static _ => { });
    }

    internal TelemetryManager(TelemetryConfiguration telemetryConfiguration, TelemetryTagsSource tagsSource, AspireCliTelemetry telemetry, ILogger<TelemetryManager> logger, Func<ResourceBuilder, string, AzureMonitorTelemetryProvider> createReportedProvider)
        : this(telemetryConfiguration, tagsSource, telemetry, logger)
    {
        _createReportedProvider = createReportedProvider;
    }

    internal bool IsInitialized
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _state == LifecycleState.Initialized;
            }
        }
    }

    /// <summary>
    /// Creates telemetry providers once, before any enrichment or activities are emitted.
    /// </summary>
    public void Initialize()
    {
        lock (_lifecycleLock)
        {
            if (_state == LifecycleState.Initialized)
            {
                return;
            }
            if (_state != LifecycleState.Uninitialized)
            {
                throw new InvalidOperationException("Telemetry cannot be initialized after shutdown or disposal.");
            }

            // Provider creation is synchronized so no caller can observe a partially built set.
            // If a later builder fails, shut down providers built earlier before allowing a retry.
            AzureMonitorTelemetryProvider? azureMonitorProvider = null;
            TracerProvider? profilingProvider = null;
            TracerProvider? debugDiagnosticProvider = null;
            try
            {
                CreateProviders(out azureMonitorProvider, out profilingProvider, out debugDiagnosticProvider);
                if (azureMonitorProvider is not null)
                {
                    _telemetry.SetEventLogger(azureMonitorProvider.EventLogger);
                }
            }
            catch
            {
                azureMonitorProvider?.Dispose();
                profilingProvider?.Shutdown(0);
                debugDiagnosticProvider?.Shutdown(0);
                profilingProvider?.Dispose();
                debugDiagnosticProvider?.Dispose();
                throw;
            }

            _azureMonitorProvider = azureMonitorProvider;
            _profilingProvider = profilingProvider;
            _debugDiagnosticProvider = debugDiagnosticProvider;
            _state = LifecycleState.Initialized;
        }
    }

    private void CreateProviders(out AzureMonitorTelemetryProvider? azureMonitorProvider, out TracerProvider? profilingProvider, out TracerProvider? debugDiagnosticProvider)
    {
        azureMonitorProvider = null;
        profilingProvider = null;
        debugDiagnosticProvider = null;
        var telemetryConfiguration = _telemetryConfiguration;
        var tagsSource = _tagsSource;
#if DEBUG
        // Preserve the DEBUG-only diagnostic OTLP path for non-profiling diagnostics. When
        // profiling is enabled, the same OTLP endpoint is reserved for the profiling provider
        // so reported/diagnostic sources do not get mixed into startup profiling exports.
        var useDebugDiagnosticOtlpExporter = telemetryConfiguration.RequestedOtlpExporter && !telemetryConfiguration.ProfilingEnabled;
#else
        var useDebugDiagnosticOtlpExporter = false;
#endif
        var useDebugDiagnosticProvider = useDebugDiagnosticOtlpExporter || telemetryConfiguration.ConsoleExporterLevel == ConsoleExporterLevel.Diagnostic;

        // Don't create any providers if nothing is enabled
        if (!telemetryConfiguration.ReportedTelemetryEnabled && !telemetryConfiguration.UseProfilingProvider && !useDebugDiagnosticProvider)
        {
            return;
        }

        if (telemetryConfiguration.ReportedTelemetryEnabled)
        {
            azureMonitorProvider = _createReportedProvider(CreateReportedResourceBuilder(), AspireTelemetryExporter.GetTelemetryStoragePath("cli"));
        }

        if (!telemetryConfiguration.UseProfilingProvider && !useDebugDiagnosticProvider)
        {
            return;
        }

        var diagnosticResource = AddCliService(ResourceBuilder.CreateDefault());
        if (telemetryConfiguration.UseProfilingProvider)
        {
            profilingProvider = CreateTracerProviderBuilder(ProfilingTelemetry.ActivitySourceName, diagnosticResource, tagsSource, _telemetry)
                .AddOtlpExporter()
                .Build();
        }

        if (useDebugDiagnosticProvider)
        {
            var diagnosticBuilder = CreateTracerProviderBuilder(AspireCliTelemetry.DiagnosticsActivitySourceName, diagnosticResource, tagsSource, _telemetry);

            if (telemetryConfiguration.ConsoleExporterLevel == ConsoleExporterLevel.Diagnostic)
            {
                diagnosticBuilder.AddConsoleExporter();
            }

            if (useDebugDiagnosticOtlpExporter)
            {
                diagnosticBuilder.AddOtlpExporter();
            }

            debugDiagnosticProvider = diagnosticBuilder.Build();
        }
    }

    private static TracerProviderBuilder CreateTracerProviderBuilder(string sourceName, ResourceBuilder resource, TelemetryTagsSource tagsSource, AspireCliTelemetry telemetry)
    {
        return Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetResourceBuilder(resource)
            .AddProcessor(new CliTagEnrichmentProcessor(tagsSource, telemetry));
    }

    // Environment-derived attributes belong only to diagnostics/profiling, not product export.
    internal static ResourceBuilder CreateReportedResourceBuilder() => AddCliService(ResourceBuilder.CreateEmpty());

    private static ResourceBuilder AddCliService(ResourceBuilder resource) => resource.AddService(
        serviceName: "aspire-cli",
        // The resource identifies the physical binary, not an emulated ASPIRE_CLI_VERSION.
        // See docs/specs/cli-identity-sidecar.md; emulated identity is reported as identity.* tags.
        serviceVersion: AssemblyVersionHelper.GetInformationalVersion(typeof(Program).Assembly));

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="configuration">The configuration to read telemetry settings from.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    /// <param name="telemetry">The telemetry service applying the CLI property policy.</param>
    /// <param name="logger">The logger for exporter lifecycle failures.</param>
    /// <param name="args">The command-line arguments.</param>
    internal TelemetryManager(IConfiguration configuration, TelemetryTagsSource tagsSource, AspireCliTelemetry telemetry, ILogger<TelemetryManager> logger, string[]? args = null)
        : this(TelemetryConfiguration.Create(configuration, args), tagsSource, telemetry, logger)
    {
    }

    /// <summary>
    /// Gets whether Azure Monitor telemetry is enabled.
    /// </summary>
    public bool HasAzureMonitor
    {
        get
        {
            lock (_lifecycleLock)
            {
                EnsureInitialized();
                return _azureMonitorProvider is not null;
            }
        }
    }

    /// <summary>
    /// Gets whether profiling telemetry export is enabled.
    /// </summary>
    public bool HasProfilingProvider
    {
        get
        {
            lock (_lifecycleLock)
            {
                EnsureInitialized();
                return _profilingProvider is not null;
            }
        }
    }

    /// <summary>
    /// Gets whether DEBUG-only diagnostic telemetry export is enabled.
    /// </summary>
    public bool HasDiagnosticProvider
    {
        get
        {
            lock (_lifecycleLock)
            {
                EnsureInitialized();
                return _debugDiagnosticProvider is not null;
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_state == LifecycleState.Uninitialized)
        {
            throw new InvalidOperationException("TelemetryManager has not been initialized.");
        }
        if (_state != LifecycleState.Initialized)
        {
            throw new InvalidOperationException("TelemetryManager has already shut down or been disposed.");
        }
    }

    /// <summary>
    /// Flushes profiling telemetry without shutting down other telemetry providers.
    /// </summary>
    public Task ForceFlushProfilingAsync()
    {
        // OpenTelemetry's TracerProvider flush API is the synchronous
        // ForceFlush(int timeoutMilliseconds) extension method. It can block until the batch
        // exporter drains or the timeout expires, so keep the CLI profile export path async by
        // running that bounded wait on the thread pool; callers still await this so export does not
        // race ahead of pending spans. Adding cancellation here would either skip the flush before
        // it starts or stop waiting while the synchronous flush keeps running; the provider timeout
        // is the actual bound for this best-effort drain.
        TracerProvider? provider;
        lock (_lifecycleLock)
        {
            EnsureInitialized();
            provider = _profilingProvider;
        }
        return Task.Run(() =>
        {
            provider?.ForceFlush(ProfilingForceFlushTimeoutMilliseconds);
        });
    }

    /// <summary>
    /// Flushes reported telemetry without shutting down other telemetry providers.
    /// </summary>
    /// <remarks>
    /// Used by <c>aspire agent telemetry</c> to persist pending events before returning to the hook.
    /// The exporter uploads from storage asynchronously, including on subsequent invocations.
    /// </remarks>
    public async Task<bool> ForceFlushReportedAsync()
    {
        // See ForceFlushProfilingAsync for why this runs the synchronous, bounded
        // ForceFlush(int) on the thread pool rather than taking a CancellationToken.
        AzureMonitorTelemetryProvider? provider;
        lock (_lifecycleLock)
        {
            EnsureInitialized();
            provider = _azureMonitorProvider;
        }
        return provider is null || await provider.ForceFlushAsync(ReportedForceFlushTimeoutMilliseconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Shuts down initialized telemetry providers, or returns false when initialization was skipped.
    /// </summary>
    public Task<bool> TryShutdownAsync(int exitCode, ReportedTelemetryMode mode)
    {
        lock (_lifecycleLock)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }
            if (_state == LifecycleState.Uninitialized || _state == LifecycleState.Disposed)
            {
                return Task.FromResult(false);
            }

            _state = LifecycleState.ShuttingDown;
            var azureMonitorProvider = _azureMonitorProvider;
            _azureMonitorProvider = null;
            var profilingProvider = _profilingProvider;
            var debugDiagnosticProvider = _debugDiagnosticProvider;
            _telemetry.SetEventLogger(null);
            _shutdownTask = Task.Run(async () =>
            {
                // Flush signals independently so adding log export does not extend the exit budget.
                await Task.WhenAll(
                    Task.Run(() => profilingProvider?.Shutdown(DiagnosticShutdownTimeoutMilliseconds)),
                    Task.Run(() => debugDiagnosticProvider?.Shutdown(DiagnosticShutdownTimeoutMilliseconds)),
                    ShutdownReportedProviderAsync(azureMonitorProvider, exitCode, mode)).ConfigureAwait(false);
                return true;
            });
            return _shutdownTask;
        }
    }

    private async Task ShutdownReportedProviderAsync(AzureMonitorTelemetryProvider? provider, int exitCode, ReportedTelemetryMode mode)
    {
        using (provider)
        {
            if (provider is null)
            {
                return;
            }

            var isLocalFailure = mode == ReportedTelemetryMode.Local &&
                exitCode is not (CliExitCodes.Success or CliExitCodes.Cancelled);
            var timeout = (mode, isLocalFailure) switch
            {
                (ReportedTelemetryMode.CI, _) => ReportedCIShutdownTimeoutMilliseconds,
                (_, true) => ReportedFailureShutdownTimeoutMilliseconds,
                _ => Timeout.Infinite
            };
            var previousDrainBudget = AppContext.GetData(ShutdownDrainBudgetKey);
            if (isLocalFailure)
            {
                // Match the SDK's initial failure budget: persistence consumes part of this window,
                // and any remaining time allows an upload attempt. This is not a delivery guarantee.
                // https://github.com/dotnet/sdk/pull/56274
                AppContext.SetData(ShutdownDrainBudgetKey, ReportedFailureShutdownTimeoutMilliseconds);
            }
            try
            {
                // An infinite provider timeout lets local queue persistence finish; the zero drain
                // budget configured at startup still prevents waiting for network delivery.
                if (!await provider.ShutdownAsync(timeout).ConfigureAwait(false))
                {
                    _logger.LogWarning("Timed out shutting down CLI reported telemetry.");
                }
            }
            finally
            {
                if (isLocalFailure)
                {
                    AppContext.SetData(ShutdownDrainBudgetKey, previousDrainBudget);
                }
            }
        }

    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_state != LifecycleState.Initialized)
            {
                _state = LifecycleState.Disposed;
                return;
            }
            _state = LifecycleState.Disposed;
            // Tests may dispose the host without an explicit shutdown. Avoid a blocking flush.
            _azureMonitorProvider?.Dispose();
            _azureMonitorProvider = null;
            _profilingProvider?.Shutdown(0);
            _debugDiagnosticProvider?.Shutdown(0);
            _telemetry.SetEventLogger(null);
        }
    }

    private enum LifecycleState
    {
        Uninitialized,
        Initialized,
        ShuttingDown,
        Disposed
    }
}

internal enum ReportedTelemetryMode
{
    Local,
    CI,
    Agent
}
