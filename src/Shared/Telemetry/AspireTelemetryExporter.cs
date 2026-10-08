// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Aspire.Shared.Telemetry;

internal static class AspireTelemetryExporter
{
    /// <summary>
    /// Gets the product's telemetry storage path under the current user's profile.
    /// </summary>
    public static string GetTelemetryStoragePath(string productName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire", productName, "telemetrystorage");
    }

    public static TracerProviderBuilder AddAspireAzureMonitorExporter(this TracerProviderBuilder builder, string connectionString, string storageDirectory)
    {
        return builder.AddAzureMonitorTraceExporter(options =>
        {
            ConfigureExporter(options, connectionString, storageDirectory);

            // Explicit instrumentation emits low-volume product events. Rate-limited sampling
            // disproportionately drops short CLI invocations immediately after provider creation.
            // TracesPerSecond takes precedence over SamplingRatio and must be cleared.
            options.TracesPerSecond = null;
            options.SamplingRatio = 1.0f;
        });
    }

    public static OpenTelemetryLoggerOptions AddAspireAzureMonitorExporter(
        this OpenTelemetryLoggerOptions options,
        string connectionString,
        string storageDirectory,
        Action<AzureMonitorExporterOptions>? configure = null)
    {
        var exporterOptions = new AzureMonitorExporterOptions();
        ConfigureExporter(exporterOptions, connectionString, storageDirectory);
        configure?.Invoke(exporterOptions);
        return options.AddProcessor(new BatchLogRecordExportProcessor(new AzureMonitorLogExporter(exporterOptions)));
    }

    internal static void ConfigureExporter(AzureMonitorExporterOptions options, string connectionString, string storageDirectory)
    {
        options.ConnectionString = connectionString;
        options.EnableLiveMetrics = false;
        options.EnableStandardMetrics = false;
        options.EnablePerformanceCounters = false;
        // These options do not disable Statsbeat's separate hosting metadata collection/export.
        // TODO: Disable Statsbeat for product exporters when a public per-exporter API is available,
        // without changing process-wide environment variables: https://github.com/Azure/azure-sdk-for-net/issues/63651.
        options.StorageDirectory = storageDirectory;
    }
}
