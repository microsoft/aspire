// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components;

/// <summary>
/// Selects the metric data point interval for a chart duration.
/// </summary>
internal static class MetricDataPointInterval
{
    /// <summary>
    /// Gets the metric data point interval for the specified chart duration.
    /// </summary>
    public static TimeSpan Get(TimeSpan duration)
    {
        if (duration >= TimeSpan.FromHours(3))
        {
            return TimeSpan.FromMinutes(5);
        }

        return duration <= TimeSpan.FromMinutes(15)
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMinutes(1);
    }
}