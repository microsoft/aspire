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
    private readonly ITelemetryRepository _telemetryRepository;

    public OtlpLogsService(ILogger<OtlpLogsService> logger, ITelemetryRepository telemetryRepository)
    {
        _logger = logger;
        _telemetryRepository = telemetryRepository;
    }

    public ExportLogsServiceResponse Export(ExportLogsServiceRequest request)
    {
        var addContext = new AddContext();
        _telemetryRepository.AddLogs(addContext, request.ResourceLogs);

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
