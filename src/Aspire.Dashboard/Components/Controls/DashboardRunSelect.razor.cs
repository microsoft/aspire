// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Shared;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private static readonly Icon s_historyIcon = new Icons.Regular.Size24.History();

    // The button is icon-only: a dot shows whether the live view or a recording is displayed, and the header pill
    // names the recording. The tooltip and accessible label state what is being viewed.
    private string RunSelectTitle => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectTitle)]
        : FormatRunDate(SelectedRunStartedAtUtc);
    private string RunSelectAccessibleLabel => Loc[nameof(LayoutResources.DashboardRunSelectAccessibleLabel), SelectedRunText];
    private string SelectedRunText => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : FormatRunDate(SelectedRunStartedAtUtc);

    [Parameter, EditorRequired]
    public required string ButtonId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public DateTimeOffset SelectedRunStartedAtUtc { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    internal static List<DashboardRunDescriptor> GetSortedRuns(IReadOnlyList<DashboardRunDescriptor> storedRuns)
    {
        var runs = new List<DashboardRunDescriptor>(storedRuns.Count);
        foreach (var run in storedRuns)
        {
            if (!run.IsPruned && (run.IsSelectable || !run.IsCompatible))
            {
                runs.Add(run);
            }
        }
        runs.Sort(CompareRuns);

        return runs;
    }

    private static int CompareRuns(DashboardRunDescriptor left, DashboardRunDescriptor right)
    {
        var result = right.IsCurrent.CompareTo(left.IsCurrent);
        if (result == 0)
        {
            result = right.IsPinned.CompareTo(left.IsPinned);
        }
        if (result == 0)
        {
            result = right.StartedAtUtc.CompareTo(left.StartedAtUtc);
        }
        if (result == 0)
        {
            result = string.Compare(left.RunId, right.RunId, StringComparison.Ordinal);
        }

        return result;
    }

    private string FormatRunDate(DateTimeOffset startedAtUtc) => FormatHelpers.FormatDateTime(TimeProvider, startedAtUtc.UtcDateTime);

    internal static string FormatRunAge(DateTimeOffset startedAtUtc, DateTimeOffset utcNow, IStringLocalizer<LayoutResources> loc)
    {
        // Clamp so a start time slightly in the future (clock skew between processes) reads as "a moment ago".
        var age = utcNow - startedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age switch
        {
            { TotalMinutes: < 1 } => loc[nameof(LayoutResources.DashboardRunSelectMomentAgo)],
            { TotalMinutes: < 2 } => loc[nameof(LayoutResources.DashboardRunSelectMinuteAgo)],
            { TotalHours: < 1 } => loc[nameof(LayoutResources.DashboardRunSelectMinutesAgo), (int)age.TotalMinutes],
            { TotalDays: < 1 } => loc[nameof(LayoutResources.DashboardRunSelectHoursAgo), (int)age.TotalHours],
            { TotalDays: < 2 } => loc[nameof(LayoutResources.DashboardRunSelectDayAgo)],
            _ => loc[nameof(LayoutResources.DashboardRunSelectDaysAgo), (int)age.TotalDays]
        };
    }

    internal static string? FormatRunDuration(DashboardRunDescriptor run)
    {
        // Runs that didn't shut down cleanly have no end time, so their duration is unknown.
        if (run.IsCurrent || run.EndedAtUtc is not { } endedAtUtc || endedAtUtc < run.StartedAtUtc)
        {
            return null;
        }

        // Round to whole seconds: sub-second precision is noise for a run's length.
        var duration = TimeSpan.FromSeconds(Math.Round((endedAtUtc - run.StartedAtUtc).TotalSeconds));
        return $"({DurationFormatter.FormatDuration(duration, CultureInfo.CurrentCulture)})";
    }
}
