// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class DashboardRunsDialog : IDialogContentComponent<DashboardRunsDialogViewModel>
{
    private IReadOnlyList<DashboardRunDescriptor> _runOptions = [];

    [Parameter]
    public required DashboardRunsDialogViewModel Content { get; set; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    internal IDashboardRunStore RunStore { get; init; } = null!;

    protected override void OnInitialized()
    {
        _runOptions = RunStore.GetRuns();
        if (!_runOptions.Any(run => string.Equals(run.RunId, Content.SelectedRun.RunId, StringComparison.Ordinal)))
        {
            Content.SelectedRun = _runOptions.Single(run => run.IsCurrent);
        }
    }

    private string FormatRunOption(DashboardRunDescriptor run)
    {
        if (run.IsCurrent)
        {
            return Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogDashboardRunCurrent)];
        }

        var localStartedAt = TimeZoneInfo.ConvertTime(run.StartedAtUtc, TimeProvider.LocalTimeZone);
        var startedAtText = localStartedAt.ToString("g", CultureInfo.CurrentCulture);
        return string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(Dashboard.Resources.Dialogs.DashboardRunsDialogStartedAt)],
            startedAtText);
    }

    private string FormatRunsLabel()
    {
        var applicationName = _runOptions.Single(run => run.IsCurrent).ApplicationName
            ?? Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogDashboardRunUnknownApplication)];
        return string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(Dashboard.Resources.Dialogs.SettingsDialogDashboardRun)],
            applicationName);
    }
}

public sealed class DashboardRunsDialogViewModel
{
    internal DashboardRunDescriptor SelectedRun { get; set; } = null!;
}