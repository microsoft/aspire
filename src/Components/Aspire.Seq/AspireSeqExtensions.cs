// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Seq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting a project's OpenTelemetry log events and spans to Seq.
/// </summary>
public static class AspireSeqExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Seq";

    /// <summary>
    /// Registers OTLP log and trace exporters to send to Seq.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate that can be used for customizing options. It's invoked after the settings are read from the configuration.</param>
    public static void AddSeqEndpoint(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<SeqSettings>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new SeqSettings();
        settings.Logs.Protocol = OtlpExportProtocol.HttpProtobuf;
        settings.Traces.Protocol = OtlpExportProtocol.HttpProtobuf;
        settings.Logs.ExportProcessorType = ExportProcessorType.Batch;
        settings.Traces.ExportProcessorType = ExportProcessorType.Batch;

        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var legacyNamedConfigSection = builder.Configuration.GetSection(connectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        // Preserve the legacy root-level named section while preferring the standard integration-specific section.
        legacyNamedConfigSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            settings.ServerUrl = connectionString;
        }

        configureSettings?.Invoke(settings);

        if (!string.IsNullOrEmpty(settings.ServerUrl))
        {
            settings.Logs.Endpoint = new Uri($"{settings.ServerUrl}/ingest/otlp/v1/logs");
            settings.Traces.Endpoint = new Uri($"{settings.ServerUrl}/ingest/otlp/v1/traces");
        }
        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            settings.Logs.Headers = string.IsNullOrEmpty(settings.Logs.Headers) ? $"X-Seq-ApiKey={settings.ApiKey}" : $"{settings.Logs.Headers},X-Seq-ApiKey={settings.ApiKey}";
            settings.Traces.Headers = string.IsNullOrEmpty(settings.Traces.Headers) ? $"X-Seq-ApiKey={settings.ApiKey}" : $"{settings.Traces.Headers},X-Seq-ApiKey={settings.ApiKey}";
        }

        builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddProcessor(
            _ => settings.Logs.ExportProcessorType switch
            {
                ExportProcessorType.Batch => new BatchLogRecordExportProcessor(new OtlpLogExporter(settings.Logs)),
                _ => new SimpleLogRecordExportProcessor(new OtlpLogExporter(settings.Logs))
            }));

        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(
            _ => settings.Traces.ExportProcessorType switch
            {
                ExportProcessorType.Batch => new BatchActivityExportProcessor(new OtlpTraceExporter(settings.Traces)),
                _ => new SimpleActivityExportProcessor(new OtlpTraceExporter(settings.Traces))
            }));

        if (!settings.DisableHealthChecks)
        {
            if (settings.ServerUrl is not null)
            {
                builder.TryAddHealthCheck(new HealthCheckRegistration(
                    "Seq",
                    _ => new SeqHealthCheck(settings.ServerUrl),
                    failureStatus: default,
                    tags: default));
            }
            else
            {
                throw new InvalidOperationException(
                    "Unable to add a Seq health check because the 'ServerUrl' setting is missing.");
            }
        }

    }
}
