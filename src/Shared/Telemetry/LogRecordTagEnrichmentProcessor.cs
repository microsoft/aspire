// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Adds sanitized product-specific default metadata to log records before export.
/// </summary>
internal sealed class LogRecordTagEnrichmentProcessor(AspireTelemetryBase telemetry) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        List<KeyValuePair<string, object?>> attributes = [.. data.Attributes ?? []];
        var keys = new HashSet<string>(attributes.Select(attribute => attribute.Key), StringComparer.Ordinal);
        foreach (var (key, value) in telemetry.GetDefaultProperties())
        {
            // Event properties take precedence over defaults, as they do at recording time.
            if (keys.Add(key))
            {
                attributes.Add(new(key, AspireTelemetryBase.SerializeLogProperty(value)));
            }
        }

        data.Attributes = attributes;
    }
}
