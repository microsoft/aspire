// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using DialogsResources = Aspire.Dashboard.Resources.Dialogs;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private IReadOnlyList<DashboardRunDescriptor> _runs = [];

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<DialogsResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    internal IDashboardRunStore RunStore { get; init; } = null!;

    protected override void OnInitialized()
    {
        _runs = RunStore.GetRuns();
    }

    private string FormatRunOption(DashboardRunDescriptor run)
    {
        if (run.IsCurrent)
        {
            return Loc[nameof(DialogsResources.SettingsDialogDashboardRunCurrent)];
        }

        var localStartedAt = TimeZoneInfo.ConvertTime(run.StartedAtUtc, TimeProvider.LocalTimeZone);
        return string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(DialogsResources.DashboardRunsDialogStartedAt)],
            localStartedAt.ToString("g", CultureInfo.CurrentCulture));
    }
}