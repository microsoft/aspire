// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace Aspire.Dashboard.Otlp;

[SkipStatusCodePages]
public sealed class OtlpMetricsService
{
    private readonly ILogger<OtlpMetricsService> _logger;
    private readonly ITelemetryRepositoryWriter _telemetryRepositoryWriter;

    public OtlpMetricsService(ILogger<OtlpMetricsService> logger, ITelemetryRepositoryWriter telemetryRepositoryWriter)
    {
        _logger = logger;
        _telemetryRepositoryWriter = telemetryRepositoryWriter;
    }

    public ExportMetricsServiceResponse Export(ExportMetricsServiceRequest request)
    {
        var addContext = new AddContext();
        _telemetryRepositoryWriter.AddMetrics(addContext, request.ResourceMetrics);

        _logger.LogDebug("Processed metrics export. Success count: {SuccessCount}, failure count: {FailureCount}", addContext.SuccessCount, addContext.FailureCount);

        return new ExportMetricsServiceResponse
        {
            PartialSuccess = new ExportMetricsPartialSuccess
            {
                RejectedDataPoints = addContext.FailureCount
            }
        };
    }
}
