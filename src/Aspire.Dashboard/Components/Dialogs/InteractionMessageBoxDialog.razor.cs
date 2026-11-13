// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class InteractionMessageBoxDialog
{
    [Parameter, EditorRequired]
    public required InteractionMessageBoxContent Content { get; set; }

    [CascadingParameter]
    public required IDialogInstance Dialog { get; set; }

    private Icon? Icon => Content.Intent switch
    {
        MessageIntent.Success => new Icons.Filled.Size24.CheckmarkCircle(),
        MessageIntent.Warning => new Icons.Filled.Size24.Warning(),
        MessageIntent.Error => new Icons.Filled.Size24.DismissCircle(),
        MessageIntent.Information => new Icons.Filled.Size24.Info(),
        MessageIntent.Confirmation => new Icons.Filled.Size24.QuestionCircle(),
        _ => null
    };

    private Color IconColor => Content.Intent switch
    {
        MessageIntent.Success or MessageIntent.Confirmation => Color.Success,
        MessageIntent.Warning => Color.Warning,
        MessageIntent.Error => Color.Error,
        MessageIntent.Information => Color.Info,
        _ => Color.Default
    };

    private Task SubmitAsync()
    {
        return Dialog.CloseAsync(DialogResult.Ok());
    }

    private Task CancelAsync()
    {
        return Dialog.CloseAsync(DialogResult.Cancel(true));
    }
}