// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Controls;

public partial class ResourceOverviewSelector : ComponentBase
{
    [Parameter]
    public ResourcesLayout? Layout { get; set; }

    [Parameter]
    public bool IsTable { get; set; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Inject]
    public required DashboardViewPreferences ViewPreferences { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Dialogs> DialogsLoc { get; init; }

    private IReadOnlyList<IconToggleOption<GroupOverview?>> Options
    {
        get
        {
            var options = new List<IconToggleOption<GroupOverview?>>();
            if (Layout is not null && ViewportInformation.IsDesktop)
            {
                // Table is the layout's full-width picker, not a persisted Summary/Graph preference.
                options.Add(new(null, new Icons.Regular.Size20.Table(), ControlsStringsLoc[nameof(Dashboard.Resources.ControlsStrings.ChartContainerTableTab)], Key: GetOptionKey(null)));
            }
            options.Add(new(GroupOverview.Summary, new Icons.Regular.Size20.DataUsage(), DialogsLoc[nameof(Dashboard.Resources.Dialogs.SettingsDialogGroupOverviewSummary)], Key: GetOptionKey(GroupOverview.Summary)));
            if (ViewPreferences.IsGraphEnabled)
            {
                options.Add(new(GroupOverview.Graph, new Icons.Regular.Size20.Flow(), Loc[nameof(Dashboard.Resources.Resources.GraphPageTitle)], Key: GetOptionKey(GroupOverview.Graph)));
            }
            return options;
        }
    }

    private static string GetOptionKey(GroupOverview? view) => view switch
    {
        null => "overview-table",
        GroupOverview.Graph => "overview-graph",
        _ => "overview-summary"
    };

    private async Task SelectViewAsync(GroupOverview? view)
    {
        // Choosing Table from the page header, or Summary/Graph from the expanded table, replaces this selector with
        // the other one, removing the focused button. Move focus to the same choice in the replacement selector.
        if (Layout is { } focusLayout && (view is null || IsTable))
        {
            focusLayout.FocusOverviewSelectorOptionAfterRender(GetOptionKey(view));
        }

        if (view is { } overview)
        {
            await ViewPreferences.SetGroupOverviewAsync(overview);
            if (Layout is { } layout)
            {
                await layout.ShowGroupOverviewAsync();
            }
        }
        else if (Layout is { } layout)
        {
            layout.SetResourceListVisible(true);
        }
    }
}
