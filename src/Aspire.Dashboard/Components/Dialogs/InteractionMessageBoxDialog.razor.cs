// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class InteractionMessageBoxDialog
{
    [Parameter, EditorRequired]
    public required InteractionMessageBoxContent Content { get; set; }

    [CascadingParameter]
    public required IDialogInstance Dialog { get; set; }

    private Task SubmitAsync()
    {
        return Dialog.CloseAsync(DialogResult.Ok());
    }

    private Task CancelAsync()
    {
        return Dialog.CloseAsync(DialogResult.Cancel(true));
    }
}