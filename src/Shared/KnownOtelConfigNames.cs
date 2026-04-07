// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Well-known OpenTelemetry environment variable names.
/// </summary>
/// <seealso href="https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/configuration/sdk-environment-variables.md">OpenTelemetry SDK Environment Variables</seealso>
internal static class KnownOtelConfigNames
{
    // OTLP Exporter
    public const string ExporterOtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ExporterOtlpProtocol = "OTEL_EXPORTER_OTLP_PROTOCOL";
    public const string ExporterOtlpHeaders = "OTEL_EXPORTER_OTLP_HEADERS";

    // Resource
    public const string ResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";
    public const string ServiceName = "OTEL_SERVICE_NAME";

    // Batch processors
    public const string BlrpScheduleDelay = "OTEL_BLRP_SCHEDULE_DELAY";
    public const string BspScheduleDelay = "OTEL_BSP_SCHEDULE_DELAY";
    public const string MetricExportInterval = "OTEL_METRIC_EXPORT_INTERVAL";

    // Sampling & filtering
    public const string TracesSampler = "OTEL_TRACES_SAMPLER";
    public const string MetricsExemplarFilter = "OTEL_METRICS_EXEMPLAR_FILTER";

    // GenAI instrumentation
    public const string InstrumentationGenAiCaptureMessageContent = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    // .NET SDK experimental settings
    public const string DotnetExperimentalOtlpRetry = "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY";
    public const string DotnetExperimentalAspNetCoreDisableUrlQueryRedaction = "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION";
    public const string DotnetExperimentalHttpClientDisableUrlQueryRedaction = "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION";

    // Azure-specific
    /// <summary>Used by the Azure App Service OpenTelemetry sidecar to specify the collector endpoint URL.</summary>
    public const string CollectorUrl = "OTEL_COLLECTOR_URL";
    /// <summary>Used by the Azure App Service OpenTelemetry sidecar to specify the managed identity client ID.</summary>
    public const string ClientId = "OTEL_CLIENT_ID";
}
