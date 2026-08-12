// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Layout;

public partial class NotificationsHeaderButton : ComponentBase, IDisposable
{
    [Parameter, EditorRequired]
    public required Func<Task> OnClick { get; set; }

    [Inject]
    public required INotificationService NotificationService { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    protected override void OnInitialized()
    {
        NotificationService.OnChange += HandleNotificationsChanged;
    }

    private int UnreadCount => NotificationService.UnreadCount;

    private async Task HandleClick()
    {
        await OnClick();
    }

    private void HandleNotificationsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        NotificationService.OnChange -= HandleNotificationsChanged;
    }
}
