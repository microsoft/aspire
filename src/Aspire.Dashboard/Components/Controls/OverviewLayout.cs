// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.Extensions.Localization;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// How the overview of all, or several, resources is laid out. <see cref="Summary"/> and <see cref="Graph"/> are the
/// stored <see cref="GroupOverview"/> preference. <see cref="List"/> is the resource list across the full page, which
/// is the layout's closed-content state rather than a preference: it's where the dashboard opens, and selecting a
/// resource or a tab leaves it.
/// </summary>
internal enum OverviewLayout
{
    Summary,
    Graph,
    List
}

internal static class OverviewLayoutOptions
{
    /// <summary>
    /// The options of the layout toggle, shared by the overview and the full-page list so both show the same choices.
    /// </summary>
    public static IReadOnlyList<IconToggleOption<OverviewLayout>> Create(
        IStringLocalizer<Dashboard.Resources.Dialogs> dialogsLoc,
        IStringLocalizer<Dashboard.Resources.Resources> resourcesLoc,
        bool isGraphEnabled)
    {
        var options = new List<IconToggleOption<OverviewLayout>>
        {
            new(OverviewLayout.Summary, new Icons.Regular.Size20.ChartMultiple(), dialogsLoc[nameof(Dashboard.Resources.Dialogs.SettingsDialogGroupOverviewSummary)])
        };
        if (isGraphEnabled)
        {
            options.Add(new(OverviewLayout.Graph, new Icons.Regular.Size20.Flowchart(), resourcesLoc[nameof(Dashboard.Resources.Resources.GraphPageTitle)]));
        }
        options.Add(new(OverviewLayout.List, new Icons.Regular.Size20.TextBulletListLtr(), resourcesLoc[nameof(Dashboard.Resources.Resources.ResourceOverviewLayoutList)]));

        return options;
    }

    public static OverviewLayout FromPreference(GroupOverview groupOverview) =>
        groupOverview == GroupOverview.Graph ? OverviewLayout.Graph : OverviewLayout.Summary;

    public static GroupOverview ToPreference(OverviewLayout layout) =>
        layout == OverviewLayout.Graph ? GroupOverview.Graph : GroupOverview.Summary;
}
