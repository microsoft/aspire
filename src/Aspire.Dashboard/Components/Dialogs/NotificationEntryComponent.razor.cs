// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class NotificationEntryComponent : ComponentBase
{
    [Parameter, EditorRequired]
    public required NotificationEntry Entry { get; set; }

    [Parameter]
    public EventCallback OnDismiss { get; set; }

    [CascadingParameter]
    public IDialogInstance Dialog { get; set; } = default!;

    [Inject]
    public required IServiceProvider Services { get; init; }

    private string IntentClass => Entry.Intent switch
    {
        MessageBarIntent.Success => "intent-success",
        MessageBarIntent.Error => "intent-error",
        MessageBarIntent.Warning => "intent-warning",
        _ => "intent-info"
    };

    private Icon Icon => Entry.Intent switch
    {
        MessageBarIntent.Success => new Icons.Filled.Size20.CheckmarkCircle(),
        MessageBarIntent.Error => new Icons.Filled.Size20.DismissCircle(),
        MessageBarIntent.Warning => new Icons.Filled.Size20.Warning(),
        _ => new Icons.Filled.Size20.Info()
    };

    private Color IconColor => Entry.Intent switch
    {
        MessageBarIntent.Success => Color.Success,
        MessageBarIntent.Error => Color.Error,
        MessageBarIntent.Warning => Color.Warning,
        _ => Color.Info
    };

    private async Task HandleDismiss()
    {
        await OnDismiss.InvokeAsync();
    }

    private async Task HandlePrimaryAction()
    {
        if (Entry.PrimaryAction is { } primaryAction)
        {
            await primaryAction.OnClick(Services);
        }
    }
}
