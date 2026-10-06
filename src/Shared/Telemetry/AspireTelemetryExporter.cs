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

    public static LoggerProvider AddAspireAzureMonitorExporter(this LoggerProvider provider, string connectionString, string storageDirectory, Func<LogRecord, bool> filter)
    {
        var options = new AzureMonitorExporterOptions();
        ConfigureExporter(options, connectionString, storageDirectory);

        // Attach after hosted-service startup checks consent, rather than constructing
        // an exporter while the application's logger factory is being initialized.
        return provider.AddProcessor(new FilteredBatchLogRecordExportProcessor(new AzureMonitorLogExporter(options), filter));
    }

    private static void ConfigureExporter(AzureMonitorExporterOptions options, string connectionString, string storageDirectory)
    {
        options.ConnectionString = connectionString;
        options.EnableLiveMetrics = false;
        options.StorageDirectory = storageDirectory;
    }
}
