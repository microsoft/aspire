// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Captures telemetry provider enablement derived from configuration and command-line arguments.
/// </summary>
internal sealed record TelemetryConfiguration
{
    /// <summary>
    /// Gets whether reported telemetry is enabled.
    /// </summary>
    public bool ReportedTelemetryEnabled { get; init; }

    /// <summary>
    /// Gets whether profiling telemetry was requested.
    /// </summary>
    public bool ProfilingEnabled { get; init; }

    /// <summary>
    /// Gets whether an OTLP exporter endpoint was configured.
    /// </summary>
    public bool RequestedOtlpExporter { get; init; }

    /// <summary>
    /// Gets the requested console exporter level.
    /// </summary>
    public ConsoleExporterLevel? ConsoleExporterLevel { get; init; }

    /// <summary>
    /// Gets whether the profiling provider should be enabled.
    /// </summary>
    public bool UseProfilingProvider => ProfilingEnabled && RequestedOtlpExporter;

    /// <summary>
    /// Creates a telemetry configuration from application configuration and command-line arguments.
    /// </summary>
    public static TelemetryConfiguration Create(IConfiguration configuration, string[]? args = null)
    {
        var hasOptOutArg = args?.Any(a => CommonOptionNames.InformationalOptionNames.Contains(a)) ?? false;
        var telemetryOptOut = hasOptOutArg || configuration.GetBool(AspireCliTelemetry.TelemetryOptOutConfigKey, defaultValue: false);
        var profilingEnabled =
            configuration.GetBool(Aspire.Hosting.KnownConfigNames.ProfilingEnabled) ??
            configuration.GetBool(Aspire.Hosting.KnownConfigNames.Legacy.StartupProfilingEnabled, defaultValue: false);
        var requestedOtlpExporter = !string.IsNullOrEmpty(configuration[AspireCliTelemetry.OtlpExporterEndpointConfigKey]);

#if DEBUG
        var consoleExporterLevel = configuration.GetEnum<ConsoleExporterLevel>(AspireCliTelemetry.ConsoleExporterLevelConfigKey, defaultValue: null);
#else
        ConsoleExporterLevel? consoleExporterLevel = null;
#endif

        return new TelemetryConfiguration
        {
            ReportedTelemetryEnabled = !telemetryOptOut,
            ProfilingEnabled = profilingEnabled,
            RequestedOtlpExporter = requestedOtlpExporter,
            ConsoleExporterLevel = consoleExporterLevel
        };
    }
}
