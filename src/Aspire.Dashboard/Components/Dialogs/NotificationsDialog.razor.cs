// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class NotificationsDialog : IDialogContentComponent, IDisposable
{
    private bool _hasNotifications;

    [Inject]
    public required IMessageService MessageService { get; init; }

    [Inject]
    public required INotificationService NotificationService { get; init; }

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    protected override void OnInitialized()
    {
        _hasNotifications = MessageService.Count(DashboardUIHelpers.NotificationSection) > 0;
        MessageService.OnMessageItemsUpdated += HandleNotificationsChanged;
        NotificationService.ResetUnreadCount();
    }

    private void HandleNotificationsChanged()
    {
        InvokeAsync(() =>
        {
            _hasNotifications = MessageService.Count(DashboardUIHelpers.NotificationSection) > 0;
            StateHasChanged();
        });
    }

    private void DismissAll()
    {
        MessageService.Clear(DashboardUIHelpers.NotificationSection);
    }

    public void Dispose()
    {
        MessageService.OnMessageItemsUpdated -= HandleNotificationsChanged;
    }
}
