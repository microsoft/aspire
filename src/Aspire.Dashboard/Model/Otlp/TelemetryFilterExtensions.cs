// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Otlp;

public static class TelemetryFilterExtensions
{
    public static bool IsTraceDurationFilter(this TelemetryFilter filter)
        => filter is FieldTelemetryFilter { Field: KnownTraceFields.DurationField };

    public static bool HasNumericMatch(this TelemetryFilter filter, double fieldValue)
        => filter is FieldTelemetryFilter fieldFilter && fieldFilter.HasNumericMatch(fieldValue);
}
