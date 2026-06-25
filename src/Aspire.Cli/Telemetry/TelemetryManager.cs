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

    private readonly TracerProvider? _azureMonitorProvider;
    private readonly TracerProvider? _profilingProvider;
    private readonly TracerProvider? _debugDiagnosticProvider;

    private bool _shuttingDown;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="telemetryConfiguration">The telemetry configuration.</param>
    /// <param name="tagsSource">The shared source for background-calculated telemetry tags.</param>
    public TelemetryManager(TelemetryConfiguration telemetryConfiguration, TelemetryTagsSource tagsSource)
    {
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
                });

#if DEBUG
            if (telemetryConfiguration.ConsoleExporterLevel == ConsoleExporterLevel.Reported)
            {
                azureMonitorBuilder.AddConsoleExporter();
            }
#endif

            _azureMonitorProvider = azureMonitorBuilder.Build();
        }

        if (telemetryConfiguration.UseProfilingProvider)
        {
            _profilingProvider = CreateTracerProviderBuilder(ProfilingTelemetry.ActivitySourceName, resource, tagsSource)
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

            _debugDiagnosticProvider = diagnosticBuilder.Build();
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
    public bool HasAzureMonitor => _azureMonitorProvider is not null;

    /// <summary>
    /// Gets whether profiling telemetry export is enabled.
    /// </summary>
    public bool HasProfilingProvider => _profilingProvider is not null;

    /// <summary>
    /// Gets whether DEBUG-only diagnostic telemetry export is enabled.
    /// </summary>
    public bool HasDiagnosticProvider => _debugDiagnosticProvider is not null;

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
        return Task.Run(() =>
        {
            _profilingProvider?.ForceFlush(ProfilingForceFlushTimeoutMilliseconds);
        });
    }

    /// <summary>
    /// Shuts down the telemetry providers, flushing any pending telemetry.
    /// </summary>
    public Task ShutdownAsync()
    {
        _shuttingDown = true;

        return Task.Run(() =>
        {
            _azureMonitorProvider?.Shutdown(ShutDownTimeoutMilliseconds);
            _profilingProvider?.Shutdown(ShutDownTimeoutMilliseconds);
            _debugDiagnosticProvider?.Shutdown(ShutDownTimeoutMilliseconds);
        });
    }

    private static string GetTelemetryStoragePath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".aspire", "cli", "telemetrystorage");
    }

    public void Dispose()
    {
        if (!_shuttingDown)
        {
            // Ensure everything is cleaned up for tests. This covers the situation where the host is disposed without a call to ShutdownAsync.
            // The shutdown timeout is zero so not to wait for telemetry to be flushed. Don't want to delay tests.
            // Dispose isn't used here because it always flushes telemetry and waits for completion.
            _azureMonitorProvider?.Shutdown(0);
            _profilingProvider?.Shutdown(0);
            _debugDiagnosticProvider?.Shutdown(0);
        }
    }
}
