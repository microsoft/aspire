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
        if (duration <= TimeSpan.FromMinutes(5))
        {
            return TimeSpan.FromSeconds(1);
        }

        if (duration <= TimeSpan.FromMinutes(15))
        {
            return TimeSpan.FromSeconds(2);
        }

        if (duration <= TimeSpan.FromMinutes(30))
        {
            return TimeSpan.FromSeconds(5);
        }

        if (duration <= TimeSpan.FromHours(1))
        {
            return TimeSpan.FromSeconds(10);
        }

        if (duration <= TimeSpan.FromHours(3))
        {
            return TimeSpan.FromSeconds(30);
        }

        if (duration <= TimeSpan.FromHours(6))
        {
            return TimeSpan.FromMinutes(1);
        }

        if (duration <= TimeSpan.FromHours(12))
        {
            return TimeSpan.FromMinutes(2);
        }

        return TimeSpan.FromMinutes(5);
    }
}