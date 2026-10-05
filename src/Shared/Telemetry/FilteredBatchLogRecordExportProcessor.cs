// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Enforces a log export allowlist before records enter the batch queue.
/// </summary>
internal sealed class FilteredBatchLogRecordExportProcessor(
    BaseExporter<LogRecord> exporter,
    Func<LogRecord, bool> filter) : BatchLogRecordExportProcessor(exporter)
{
    public override void OnEnd(LogRecord data)
    {
        // Logger filters are configurable routing, not a privacy boundary. Keep rejected
        // records out of the export queue even when configuration enables their category.
        if (filter(data))
        {
            base.OnEnd(data);
        }
    }
}
