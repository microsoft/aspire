// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class TelemetryEmptyContent
{
    [Parameter, EditorRequired]
    public required RenderFragment ChildContent { get; set; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

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
