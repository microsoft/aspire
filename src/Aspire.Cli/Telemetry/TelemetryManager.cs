// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
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
/// Manages OpenTelemetry TracerProvider instances for the CLI.
/// Maintains separate providers for reported telemetry, profiling telemetry, and debug diagnostics.
/// </summary>
internal sealed class TelemetryManager : IDisposable
{
    // Remote export connection string for Application Insights. Intentionally hard-coded.
    private const string ApplicationInsightsConnectionString = "InstrumentationKey=e39510fc-95a1-423d-9f33-6121bf0d2113;IngestionEndpoint=https://centralus-2.in.applicationinsights.azure.com/;LiveEndpoint=https://centralus.livediagnostics.monitor.azure.com/;ApplicationId=4d8bb9db-b7ab-49f9-978b-80ae1e83f6da";

#if DEBUG
    // No timeout in debug builds
    private const int ShutDownTimeoutMilliseconds = -1;
#else
    // Chosen to provide time to send remaining telemetry without noticeably delaying exit.
    private const int ShutDownTimeoutMilliseconds = 200;
#endif
    private const int ProfilingForceFlushTimeoutMilliseconds = 5000;

    // Agent hooks flush to durable exporter storage before returning. Give persistence its own
    // budget rather than truncating it to the normal Release shutdown window.
    private const int ReportedForceFlushTimeoutMilliseconds = 3000;

    private readonly TelemetryConfiguration _telemetryConfiguration;
    private readonly TelemetryTagsSource _tagsSource;
    private readonly Lock _lifecycleLock = new();
    private TracerProvider? _azureMonitorProvider;
    private TracerProvider? _profilingProvider;
    private TracerProvider? _debugDiagnosticProvider;
    private Task<bool>? _shutdownTask;
    private LifecycleState _state;

    /// <summary>
    /// Configures exporter persistence before any providers are created in the CLI process.
    /// </summary>
    internal static void ConfigureExporterForProcess(bool isAgentTelemetryInvocation)
    {
        // These switches are process-wide, so configure them only at the entry point, not in DI.
        // Agent hooks must persist before exiting without waiting for ingestion. Keep ordinary
        // commands' existing shutdown behavior; the exporter owns storage, leases, and retries.
        // https://github.com/Azure/azure-sdk-for-net/blob/Azure.Monitor.OpenTelemetry.Exporter_1.9.0/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/README.md#telemetry-delivery-on-shutdown
        AppContext.SetSwitch("Azure.Monitor.OpenTelemetry.Exporter.PersistOnForceFlush", isAgentTelemetryInvocation);
        AppContext.SetSwitch("Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown", !isAgentTelemetryInvocation);
        if (isAgentTelemetryInvocation)
        {
            AppContext.SetData("Azure.Monitor.OpenTelemetry.Exporter.ShutdownDrainBudgetMilliseconds", 0);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="telemetryConfiguration">The telemetry configuration.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    public TelemetryManager(TelemetryConfiguration telemetryConfiguration, TelemetryTagsSource tagsSource)
    {
        _telemetryConfiguration = telemetryConfiguration;
        _tagsSource = tagsSource;
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
            TracerProvider? azureMonitorProvider = null;
            TracerProvider? profilingProvider = null;
            TracerProvider? debugDiagnosticProvider = null;
            try
            {
                CreateProviders(out azureMonitorProvider, out profilingProvider, out debugDiagnosticProvider);
            }
            catch
            {
                azureMonitorProvider?.Shutdown(0);
                profilingProvider?.Shutdown(0);
                debugDiagnosticProvider?.Shutdown(0);
                throw;
            }

            _azureMonitorProvider = azureMonitorProvider;
            _profilingProvider = profilingProvider;
            _debugDiagnosticProvider = debugDiagnosticProvider;
            _state = LifecycleState.Initialized;
        }
    }

    private void CreateProviders(out TracerProvider? azureMonitorProvider, out TracerProvider? profilingProvider, out TracerProvider? debugDiagnosticProvider)
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

        var resource = ResourceBuilder.CreateDefault().AddService(
            serviceName: "aspire-cli",
            // physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md):
            // the OTel service version identifies the actual running binary that produced the
            // telemetry, so it must NOT be replaced by an emulated ASPIRE_CLI_VERSION identity.
            // The emulated identity is emitted separately as identity.* tags (AspireCliTelemetry).
            serviceVersion: VersionHelper.GetDefaultTemplateVersion());

        // Create Azure Monitor provider if connection string is provided.
        // The Azure Monitor only exports telemetry from the Reported activity source.
        if (telemetryConfiguration.ReportedTelemetryEnabled)
        {
            var azureMonitorBuilder = CreateTracerProviderBuilder(AspireCliTelemetry.ReportedActivitySourceName, resource, tagsSource)
                .AddAzureMonitorTraceExporter(o =>
                {
                    o.ConnectionString = ApplicationInsightsConnectionString;
                    o.EnableLiveMetrics = false;
                    o.StorageDirectory = GetTelemetryStoragePath();

                    // Capture 100% of reported telemetry. The exporter defaults to a RateLimitedSampler
                    // (TracesPerSecond = 5), which keeps a span with probability
                    // min(elapsed_since_provider_built * tracesPerSecond, 1). That model assumes a
                    // long-lived, high-volume process; the CLI is the opposite (fire-and-forget, often a
                    // single span per process), so every span is judged at cold-start probability and
                    // ~half are silently dropped as a startup-timing artifact rather than a deliberate
                    // policy. Reported volume is a handful of spans per run, so full capture is cheap and
                    // is what adoption analytics needs. TracesPerSecond takes precedence over SamplingRatio
                    // in the exporter, so it must be nulled for the 100% ratio to take effect.
                    o.TracesPerSecond = null;
                    o.SamplingRatio = 1.0f;
                });

#if DEBUG
            if (telemetryConfiguration.ConsoleExporterLevel == ConsoleExporterLevel.Reported)
            {
                azureMonitorBuilder.AddConsoleExporter();
            }
#endif

            azureMonitorProvider = azureMonitorBuilder.Build();
        }

        if (telemetryConfiguration.UseProfilingProvider)
        {
            profilingProvider = CreateTracerProviderBuilder(ProfilingTelemetry.ActivitySourceName, resource, tagsSource)
                .AddOtlpExporter()
                .Build();
        }

        if (useDebugDiagnosticProvider)
        {
            var diagnosticBuilder = CreateTracerProviderBuilder(AspireCliTelemetry.DiagnosticsActivitySourceName, resource, tagsSource);

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

    private static TracerProviderBuilder CreateTracerProviderBuilder(string sourceName, ResourceBuilder resource, TelemetryTagsSource tagsSource)
    {
        return Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .SetResourceBuilder(resource)
            .AddProcessor(new CliTagEnrichmentProcessor(tagsSource));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="configuration">The configuration to read telemetry settings from.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    /// <param name="args">The command-line arguments.</param>
    internal TelemetryManager(IConfiguration configuration, TelemetryTagsSource tagsSource, string[]? args = null)
        : this(TelemetryConfiguration.Create(configuration, args), tagsSource)
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
    public Task<bool> ForceFlushReportedAsync()
    {
        // See ForceFlushProfilingAsync for why this runs the synchronous, bounded
        // ForceFlush(int) on the thread pool rather than taking a CancellationToken.
        TracerProvider? provider;
        lock (_lifecycleLock)
        {
            EnsureInitialized();
            provider = _azureMonitorProvider;
        }
        return Task.Run(() =>
        {
            return provider?.ForceFlush(ReportedForceFlushTimeoutMilliseconds) ?? true;
        });
    }

    /// <summary>
    /// Shuts down initialized telemetry providers, or returns false when initialization was skipped.
    /// </summary>
    public Task<bool> TryShutdownAsync()
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
            var profilingProvider = _profilingProvider;
            var debugDiagnosticProvider = _debugDiagnosticProvider;
            _shutdownTask = Task.Run(() =>
            {
                azureMonitorProvider?.Shutdown(ShutDownTimeoutMilliseconds);
                profilingProvider?.Shutdown(ShutDownTimeoutMilliseconds);
                debugDiagnosticProvider?.Shutdown(ShutDownTimeoutMilliseconds);
                return true;
            });
            return _shutdownTask;
        }
    }

    internal static string GetTelemetryStoragePath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".aspire", "cli", "telemetrystorage");
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
            _azureMonitorProvider?.Shutdown(0);
            _profilingProvider?.Shutdown(0);
            _debugDiagnosticProvider?.Shutdown(0);
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
