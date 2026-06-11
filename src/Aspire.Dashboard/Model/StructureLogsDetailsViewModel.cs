// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

public sealed class StructureLogsDetailsViewModel
{
    public required OtlpLogEntry LogEntry { get; init; }

    public static string GetEventName(OtlpLogEntry logEntry, IStringLocalizer<Dashboard.Resources.StructuredLogs> loc)
    {
        if (OtlpHelpers.GetEventName(logEntry) is { Length: > 0 } eventName)
        {
            return eventName;
        }

        return loc[nameof(Dashboard.Resources.StructuredLogs.StructuredLogsEntryDetails)];
    }
}
