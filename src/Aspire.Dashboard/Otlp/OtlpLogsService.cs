// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Aspire.Dashboard.Otlp;

[SkipStatusCodePages]
public sealed class OtlpLogsService
{
    private readonly ILogger<OtlpLogsService> _logger;
    private readonly ITelemetryRepositoryWriter _telemetryRepositoryWriter;

    public OtlpLogsService(ILogger<OtlpLogsService> logger, ITelemetryRepositoryWriter telemetryRepositoryWriter)
    {
        _logger = logger;
        _telemetryRepositoryWriter = telemetryRepositoryWriter;
    }

    public async Task<ExportLogsServiceResponse> ExportAsync(ExportLogsServiceRequest request)
    {
        var addContext = new AddContext();
        await _telemetryRepositoryWriter.AddLogsAsync(addContext, request.ResourceLogs).ConfigureAwait(false);

        _logger.LogDebug("Processed logs export. Success count: {SuccessCount}, failure count: {FailureCount}", addContext.SuccessCount, addContext.FailureCount);

        return new ExportLogsServiceResponse
        {
            PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = addContext.FailureCount
            }
        };
    }
}
