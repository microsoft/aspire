// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Tells the user to configure their app to send telemetry, with a button that opens setup instructions. Callers show
/// it while the dashboard hasn't received telemetry.
/// </summary>
public partial class TelemetryHelpMessage
{
    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    private async Task OpenHelpAsync()
    {
        await DialogService.ShowDialogAsync<TelemetryHelpDialog>(new DialogParameters
        {
            Title = Loc[nameof(Resources.Dialogs.TelemetryHelpDialogTitle)],
            PrimaryAction = Loc[nameof(Resources.Dialogs.DialogCloseButtonText)],
            SecondaryAction = string.Empty,
            Width = "700px"
        });
    }
}
