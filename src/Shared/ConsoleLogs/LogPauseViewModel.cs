// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if ASPIRE_DASHBOARD
using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Localization;
#endif

namespace Aspire.Shared.ConsoleLogs;

#if ASPIRE_DASHBOARD
public sealed class LogPauseViewModel
#else
internal sealed class LogPauseViewModel
#endif
{
    public required DateTime StartTime { get; init; }
    public required string? ResourcePrefix { get; set; }
    public DateTime? EndTime { get; set; }
    public int FilteredCount { get; set; }

    public bool Contains(DateTime timestamp) => timestamp >= StartTime && (EndTime is null || timestamp < EndTime);

#if ASPIRE_DASHBOARD
    public string GetDisplayText(IStringLocalizer<Aspire.Dashboard.Resources.ConsoleLogs> loc, BrowserTimeProvider timeProvider)
    {
        var pause = this;

        var text = pause.EndTime is null
            ? string.Format(
                CultureInfo.CurrentCulture,
                loc[nameof(Aspire.Dashboard.Resources.ConsoleLogs.ConsoleLogsPauseActive)],
                FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pause.StartTime, MillisecondsDisplay.Truncated),
                pause.FilteredCount)
            : string.Format(
                CultureInfo.CurrentCulture,
                loc[nameof(Aspire.Dashboard.Resources.ConsoleLogs.ConsoleLogsPauseDetails)],
                FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pause.StartTime, MillisecondsDisplay.Truncated),
                FormatHelpers.FormatTimeWithOptionalDate(timeProvider, pause.EndTime.Value, MillisecondsDisplay.Truncated),
                pause.FilteredCount);

        return text;
    }
#endif
}
