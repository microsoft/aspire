// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private IReadOnlyList<DashboardRunDescriptor> _runs = [];
    private readonly List<MenuButtonItem> _menuItems = [];
    private string RunSelectAriaLabel => Loc[nameof(LayoutResources.DashboardRunSelectAriaLabel)];
    private string SelectedRunText => FormatRunOption(_runs.Single(run => string.Equals(run.RunId, SelectedRunId, StringComparison.Ordinal)));

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    internal IDashboardRunStore RunStore { get; init; } = null!;

    protected override void OnInitialized()
    {
        _runs = RunStore.GetRuns();
    }

    protected override void OnParametersSet()
    {
        _menuItems.Clear();
        foreach (var run in _runs)
        {
            _menuItems.Add(new MenuButtonItem
            {
                Text = FormatRunOption(run),
                Icon = string.Equals(run.RunId, SelectedRunId, StringComparison.Ordinal)
                    ? new Icons.Regular.Size16.Checkmark()
                    : null,
                OnClick = () => SelectedRunIdChanged.InvokeAsync(run.IsCurrent ? null : run.RunId)
            });

            if (run.IsCurrent && _runs.Any(candidate => !candidate.IsCurrent))
            {
                _menuItems.Add(new MenuButtonItem { IsDivider = true });
            }
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