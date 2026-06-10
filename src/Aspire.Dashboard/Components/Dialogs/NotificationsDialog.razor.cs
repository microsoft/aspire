// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class NotificationsDialog : IDialogContentComponent, IDisposable
{
    private IReadOnlyList<NotificationMessage> _notifications = [];

    [Inject]
    public required INotificationService NotificationService { get; init; }

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    protected override void OnInitialized()
    {
        _notifications = NotificationService.GetNotifications();
        NotificationService.OnChange += HandleNotificationsChanged;
        NotificationService.ResetUnreadCount();
    }

    private void HandleNotificationsChanged()
    {
        _ = InvokeAsync(() =>
        {
            _notifications = NotificationService.GetNotifications();
            StateHasChanged();
        });
    }

    private void DismissAll()
    {
        NotificationService.ClearAll();
    }

    private void Dismiss(string id)
    {
        NotificationService.RemoveNotification(id);
    }

    public void Dispose()
    {
        NotificationService.OnChange -= HandleNotificationsChanged;
    }
}
