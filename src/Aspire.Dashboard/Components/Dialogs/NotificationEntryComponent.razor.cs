// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class NotificationEntryComponent : ComponentBase
{
    [Parameter, EditorRequired]
    public required NotificationEntry Entry { get; set; }

    [Parameter]
    public EventCallback OnDismiss { get; set; }

    [CascadingParameter]
    public DeckDialogInstance Dialog { get; set; } = default!;

    [Inject]
    public required IServiceProvider Services { get; init; }

    private string IntentClass => Entry.Intent switch
    {
        NotificationIntent.Success => "intent-success",
        NotificationIntent.Error => "intent-error",
        NotificationIntent.Warning => "intent-warning",
        _ => "intent-info"
    };

    private DeckIconName IconName => Entry.Intent switch
    {
        NotificationIntent.Success => DeckIconName.CheckmarkCircle,
        NotificationIntent.Error => DeckIconName.ErrorCircle,
        NotificationIntent.Warning => DeckIconName.Warning,
        _ => DeckIconName.Info
    };

    private string IconTone => Entry.Intent switch
    {
        NotificationIntent.Success => "icon-success",
        NotificationIntent.Error => "icon-error",
        NotificationIntent.Warning => "icon-warning",
        _ => "icon-muted"
    };

    private async Task HandleDismiss()
    {
        await OnDismiss.InvokeAsync();
    }

    private async Task HandlePrimaryAction()
    {
        if (Entry.PrimaryAction is { } primaryAction)
        {
            try
            {
                await Dialog.Hide();
                await primaryAction.OnClick(Services);
            }
            finally
            {
                await Dialog.Show();
            }
        }
    }
}
