// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private static readonly Icon s_checkmarkIcon = new Icons.Regular.Size16.Checkmark();
    private static readonly Icon s_pinIcon = new Icons.Regular.Size16.Pin();
    private static readonly Icon s_pinnedIcon = new Icons.Filled.Size16.Pin();

    private string RunSelectTitle => Loc[nameof(LayoutResources.DashboardRunSelectTitle)];
    private string RunSelectAccessibleLabel => Loc[nameof(LayoutResources.DashboardRunSelectAccessibleLabel), SelectedRunText];
    private string SelectedRunText => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, SelectedRunStartedAtUtc.UtcDateTime);

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public DateTimeOffset SelectedRunStartedAtUtc { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    [Inject]
    public required ILogger<DashboardRunSelect> Logger { get; init; }

    private IList<MenuButtonItem> LoadRuns()
    {
        var runs = RunStore.GetRuns()
            .Where(run => !run.IsPruned)
            .OrderByDescending(run => run.IsCurrent)
            .ThenByDescending(run => run.IsPinned)
            .ThenByDescending(run => run.StartedAtUtc)
            .ToArray();
        var menuItems = new List<MenuButtonItem>();
        foreach (var run in runs)
        {
            var menuItem = new MenuButtonItem
            {
                Text = FormatRunOption(run),
                Role = MenuItemRole.MenuItemRadio,
                Checked = string.Equals(run.RunId, SelectedRunId, StringComparison.Ordinal),
                Icon = s_checkmarkIcon,
                SecondaryActionIcon = run.IsPinned ? s_pinnedIcon : s_pinIcon,
                SecondaryActionAriaLabel = Loc[run.IsPinned
                    ? nameof(LayoutResources.DashboardRunSelectUnpin)
                    : nameof(LayoutResources.DashboardRunSelectPin)],
                IsSecondaryActionSelected = run.IsPinned,
                OnSecondaryActionClick = () =>
                {
                    SetRunPinned(run, !run.IsPinned);
                    return Task.CompletedTask;
                },
                OnClick = () => SelectedRunIdChanged.InvokeAsync(run.IsCurrent ? null : run.RunId)
            };
            menuItems.Add(menuItem);

            if (run.IsCurrent && runs.Any(candidate => !candidate.IsCurrent))
            {
                menuItems.Add(new MenuButtonItem { IsDivider = true });
            }
        }

        return menuItems;
    }

    private void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        try
        {
            RunStore.SetRunPinned(run, isPinned);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to update the pinned state of dashboard run '{RunId}'.", run.RunId);
        }
    }

    private string FormatRunOption(DashboardRunDescriptor run)
    {
        if (run.IsCurrent)
        {
            return Loc[nameof(LayoutResources.DashboardRunSelectCurrent)];
        }

        return FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, run.StartedAtUtc.UtcDateTime);
    }
}