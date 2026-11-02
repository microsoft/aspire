// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Humanizer;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

internal static class HealthReportFormatter
{
    /// <summary>
    /// Gets the health status of a report with how long ago it last ran, for example "Healthy (2 minutes ago)".
    /// </summary>
    public static string GetStatusWithTime(HealthReportViewModel report, IStringLocalizer<Resources.Resources> loc)
    {
        var statusText = report.HealthStatus?.Humanize() ?? loc[nameof(Resources.Resources.WaitingHealthDataStatusMessage)];

        if (report.LastRunAtTimeStamp.HasValue)
        {
            var duration = DateTime.UtcNow.Subtract(report.LastRunAtTimeStamp.Value);

            // Round duration to seconds to avoid sub-second precision issues
            var roundedDuration = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));

            // Display "just now" for health checks that ran in the last 10 seconds
            if (roundedDuration.TotalSeconds < 10)
            {
                return loc[nameof(Resources.Resources.HealthCheckStatusJustNowFormat), statusText];
            }

            var formattedDuration = DurationFormatter.FormatDuration(roundedDuration, CultureInfo.CurrentCulture);
            return loc[nameof(Resources.Resources.HealthCheckStatusWithTimeFormat), statusText, formattedDuration];
        }

        return statusText;
    }

    /// <summary>
    /// Gets a tooltip with the health status of a report and the local time it last ran.
    /// </summary>
    public static string? GetStatusTooltip(HealthReportViewModel report, IStringLocalizer<Resources.Resources> loc, BrowserTimeProvider timeProvider)
    {
        if (report.LastRunAtTimeStamp.HasValue)
        {
            var statusText = report.HealthStatus?.Humanize() ?? loc[nameof(Resources.Resources.WaitingHealthDataStatusMessage)];
            var localTime = FormatHelpers.FormatTimeWithOptionalDate(timeProvider, report.LastRunAtTimeStamp.Value);
            return loc[nameof(Resources.Resources.HealthCheckStatusWithTimeTooltipFormat), statusText, localTime];
        }

        return null;
    }
}
