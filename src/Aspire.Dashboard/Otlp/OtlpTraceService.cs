// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Aspire.Dashboard.Otlp;

[SkipStatusCodePages]
public sealed class OtlpTraceService
{
    private readonly ILogger<OtlpTraceService> _logger;
    private readonly ITelemetryRepositoryWriter _telemetryRepositoryWriter;

    public OtlpTraceService(ILogger<OtlpTraceService> logger, ITelemetryRepositoryWriter telemetryRepositoryWriter)
    {
        _logger = logger;
        _telemetryRepositoryWriter = telemetryRepositoryWriter;
    }

    public async Task<ExportTraceServiceResponse> ExportAsync(ExportTraceServiceRequest request)
    {
        var addContext = new AddContext();
        await _telemetryRepositoryWriter.AddTracesAsync(addContext, request.ResourceSpans).ConfigureAwait(false);

        _logger.LogDebug("Processed trace export. Success count: {SuccessCount}, failure count: {FailureCount}", addContext.SuccessCount, addContext.FailureCount);

        return new ExportTraceServiceResponse
        {
            PartialSuccess = new ExportTracePartialSuccess
            {
                RejectedSpans = addContext.FailureCount
            }
        };
    }
}
