// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class NotificationEntryComponent : ComponentBase
{
    [Parameter, EditorRequired]
    public required NotificationEntry Entry { get; set; }

    [Parameter]
    public EventCallback OnDismiss { get; set; }

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    [Inject]
    public required IServiceProvider Services { get; init; }

    private string IntentClass => Entry.Intent switch
    {
        MessageIntent.Success => "intent-success",
        MessageIntent.Error => "intent-error",
        MessageIntent.Warning => "intent-warning",
        _ => "intent-info"
    };

    private DeckIconName IconName => Entry.Intent switch
    {
        MessageIntent.Success => DeckIconName.CheckmarkCircle,
        MessageIntent.Error => DeckIconName.ErrorCircle,
        MessageIntent.Warning => DeckIconName.Warning,
        _ => DeckIconName.Info
    };

    private string IconTone => Entry.Intent switch
    {
        MessageIntent.Success => "icon-success",
        MessageIntent.Error => "icon-error",
        MessageIntent.Warning => "icon-warning",
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
                Dialog.Hide();
                await primaryAction.OnClick(Services);
            }
            finally
            {
                Dialog.Show();
            }
        }
    }
}
