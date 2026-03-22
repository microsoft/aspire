// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Manages OpenTelemetry TracerProvider instances for the CLI.
/// Maintains separate providers for Azure Monitor (production telemetry) and debug exporters (OTLP/console).
/// </summary>
internal sealed class TelemetryManager
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

    private readonly TracerProvider? _azureMonitorProvider;
#if DEBUG
    private readonly TracerProvider? _diagnosticProvider;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryManager"/> class.
    /// </summary>
    /// <param name="configuration">The configuration to read telemetry settings from.</param>
    /// <param name="args">The command-line arguments.</param>
    public TelemetryManager(IConfiguration configuration, string[]? args = null)
    {
        // Don't send telemetry for informational commands or if the user has opted out.
        var hasOptOutArg = args?.Any(a => CommonOptionNames.InformationalOptionNames.Contains(a)) ?? false;
        var telemetryOptOut = hasOptOutArg || configuration.GetBool(AspireCliTelemetry.TelemetryOptOutConfigKey, defaultValue: false);

#if DEBUG
        var useOtlpExporter = !string.IsNullOrEmpty(configuration[AspireCliTelemetry.OtlpExporterEndpointConfigKey]);
        var consoleExporterLevel = configuration.GetEnum<ConsoleExporterLevel>(AspireCliTelemetry.ConsoleExporterLevelConfigKey, defaultValue: null);
#else
        var useOtlpExporter = false;
        ConsoleExporterLevel? consoleExporterLevel = null;
#endif

        // Don't create any providers if nothing is enabled
        if (telemetryOptOut && !useOtlpExporter && consoleExporterLevel is null)
        {
            return;
        }

        var resource = ResourceBuilder.CreateDefault().AddService(
            serviceName: "aspire-cli",
            serviceVersion: VersionHelper.GetDefaultTemplateVersion());

        // Create Azure Monitor provider if connection string is provided.
        // The Azure Monitor only exports telemetry from the Reported activity source.
        if (!telemetryOptOut)
        {
            var azureMonitorBuilder = Sdk.CreateTracerProviderBuilder()
                .AddSource(AspireCliTelemetry.ReportedActivitySourceName)
                .SetResourceBuilder(resource)
                .AddAzureMonitorTraceExporter(o =>
                {
                    o.ConnectionString = ApplicationInsightsConnectionString;
                    o.EnableLiveMetrics = false;
                    o.StorageDirectory = GetTelemetryStoragePath();
                });

#if DEBUG
            if (consoleExporterLevel == ConsoleExporterLevel.Reported)
            {
                azureMonitorBuilder.AddConsoleExporter();
            }
#endif

            _azureMonitorProvider = azureMonitorBuilder.Build();
        }

#if DEBUG
        // Create diagnostic provider if any diagnostic exporter is enabled.
        // The diagnostic provider exports diagnostic telemetry.
        if (useOtlpExporter || consoleExporterLevel == ConsoleExporterLevel.Diagnostic)
        {
            var diagnosticBuilder = Sdk.CreateTracerProviderBuilder()
                .AddSource(AspireCliTelemetry.DiagnosticsActivitySourceName)
                .AddSource(AspireCliTelemetry.ReportedActivitySourceName)
                .SetResourceBuilder(resource);

            if (consoleExporterLevel == ConsoleExporterLevel.Diagnostic)
            {
                diagnosticBuilder.AddConsoleExporter();
            }

            if (useOtlpExporter)
            {
                diagnosticBuilder.AddOtlpExporter();
            }

            _diagnosticProvider = diagnosticBuilder.Build();
        }
#endif
    }

    /// <summary>
    /// Gets whether Azure Monitor telemetry is enabled.
    /// </summary>
    public bool HasAzureMonitor => _azureMonitorProvider is not null;

#if DEBUG
    /// <summary>
    /// Gets whether any diagnostic exporter is enabled.
    /// </summary>
    public bool HasDiagnosticProvider => _diagnosticProvider is not null;
#endif

    /// <summary>
    /// Shuts down the telemetry providers, flushing any pending telemetry.
    /// </summary>
    public Task ShutdownAsync()
    {
        return Task.Run(() =>
        {
            _azureMonitorProvider?.Shutdown(ShutDownTimeoutMilliseconds);
#if DEBUG
            _diagnosticProvider?.Shutdown(ShutDownTimeoutMilliseconds);
#endif
        });
    }

    private static string GetTelemetryStoragePath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirectory, ".aspire", "cli", "telemetrystorage");
    }
}
