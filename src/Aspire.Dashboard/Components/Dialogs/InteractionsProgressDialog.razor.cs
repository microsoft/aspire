// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class InteractionsProgressDialog
{
    [Parameter]
    public InteractionsProgressDialogViewModel Content { get; set; } = default!;

    [CascadingParameter]
    public IDialogInstance Dialog { get; set; } = default!;

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    private async Task CancelAsync()
    {
        await Dialog.CloseAsync(DialogResult.Ok("cancel"));
    }
}
