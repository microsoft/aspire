// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CancelledCommand_NotificationRemainsWithWarningIntent()
    {
        var notificationService = new TestNotificationService();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, _) => Task.FromResult(new ResourceCommandResponseViewModel
            {
                Kind = ResourceCommandResponseKind.Cancelled,
                Message = "Operation was canceled"
            }));

        var executor = CreateExecutor(dashboardClient, notificationService);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var command = new CommandViewModel("stop", CommandViewModelState.Enabled, "Stop", "Stop the resource", confirmationMessage: "", argumentInputs: [], isHighlighted: false, iconName: string.Empty, iconVariant: IconVariant.Regular);

        await executor.ExecuteAsync(resource, command, r => r.Name);

        var notifications = notificationService.GetNotifications();
        var notification = Assert.Single(notifications);
        Assert.Equal(MessageIntent.Warning, notification.Entry.Intent);
        Assert.Equal("Localized:ResourceCommandCanceled", notification.Entry.Title);
    }

    [Fact]
    public async Task ExecuteAsync_SucceededCommand_NotificationHasSuccessIntent()
    {
        var notificationService = new TestNotificationService();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, _) => Task.FromResult(new ResourceCommandResponseViewModel
            {
                Kind = ResourceCommandResponseKind.Succeeded,
                Message = "Done"
            }));

        var executor = CreateExecutor(dashboardClient, notificationService);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var command = new CommandViewModel("start", CommandViewModelState.Enabled, "Start", "Start the resource", confirmationMessage: "", argumentInputs: [], isHighlighted: false, iconName: string.Empty, iconVariant: IconVariant.Regular);

        await executor.ExecuteAsync(resource, command, r => r.Name);

        var notifications = notificationService.GetNotifications();
        var notification = Assert.Single(notifications);
        Assert.Equal(MessageIntent.Success, notification.Entry.Intent);
    }

    [Fact]
    public async Task ExecuteAsync_FailedCommand_NotificationHasErrorIntent()
    {
        var notificationService = new TestNotificationService();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, _) => Task.FromResult(new ResourceCommandResponseViewModel
            {
                Kind = ResourceCommandResponseKind.Failed,
                ErrorMessage = "Something went wrong"
            }));

        var executor = CreateExecutor(dashboardClient, notificationService);
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-resource", state: KnownResourceState.Running);
        var command = new CommandViewModel("restart", CommandViewModelState.Enabled, "Restart", "Restart the resource", confirmationMessage: "", argumentInputs: [], isHighlighted: false, iconName: string.Empty, iconVariant: IconVariant.Regular);

        await executor.ExecuteAsync(resource, command, r => r.Name);

        var notifications = notificationService.GetNotifications();
        var notification = Assert.Single(notifications);
        Assert.Equal(MessageIntent.Error, notification.Entry.Intent);
    }

    private static DashboardCommandExecutor CreateExecutor(IDashboardClient dashboardClient, INotificationService notificationService)
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));

        var telemetrySender = new TestDashboardTelemetrySender();
        var telemetryService = new DashboardTelemetryService(NullLogger<DashboardTelemetryService>.Instance, telemetrySender);

        var dialogService = new DashboardDialogService(
            new TestDialogService(),
            new TestStringLocalizer<Dialogs>(),
            dimensionManager);

        return new DashboardCommandExecutor(
            dashboardClient,
            dialogService,
            new TestToastService(),
            new TestStringLocalizer<Resources.Resources>(),
            new TestNavigationManager(),
            telemetryService,
            notificationService);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }
    }

    private sealed class TestToastService : IToastService
    {
        public event Action<string?>? OnClose;
        public event Action? OnUpdate;
        public event Action? OnClearAll;

        public void ClearAll() { }
        public void CloseToast(string id) => OnClose?.Invoke(id);
        public void ShowCommunicationToast(ToastParameters<CommunicationToastContent> parameters) { }
        public void ShowConfirmationToast(ToastParameters<ConfirmationToastContent> parameters) { }
        public void ShowProgressToast(ToastParameters<ProgressToastContent> parameters) { }
        public void ShowToast(Type? component, ToastParameters parameters) { }
        public void UpdateToast(string id, ToastParameters parameters) => OnUpdate?.Invoke();
    }

    private sealed class TestNotificationService : INotificationService
    {
        private readonly List<(string Id, NotificationEntry Entry)> _notifications = [];
        private int _nextId;

        public int UnreadCount => _notifications.Count;

        public event Action? OnChange;

        public string AddNotification(NotificationEntry notification)
        {
            var id = (++_nextId).ToString();
            notification.Timestamp = DateTimeOffset.UtcNow;
            _notifications.Add((id, notification));
            OnChange?.Invoke();
            return id;
        }

        public void ReplaceNotification(string id, NotificationEntry notification)
        {
            notification.Timestamp = DateTimeOffset.UtcNow;
            for (var i = 0; i < _notifications.Count; i++)
            {
                if (_notifications[i].Id == id)
                {
                    _notifications[i] = (id, notification);
                    break;
                }
            }

            OnChange?.Invoke();
        }

        public void RemoveNotification(string id)
        {
            _notifications.RemoveAll(n => n.Id == id);
            OnChange?.Invoke();
        }

        public void ClearAll()
        {
            _notifications.Clear();
            OnChange?.Invoke();
        }

        public void ResetUnreadCount() { }

        public IReadOnlyList<NotificationMessage> GetNotifications()
        {
            return _notifications.Select(n => new NotificationMessage { Id = n.Id, Entry = n.Entry }).ToList();
        }
    }
}
