// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using ProtoInteractionInput = Aspire.DashboardService.Proto.V1.InteractionInput;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncCore_CancelNotificationAction_CancelsCommandAndUpdatesNotification()
    {
        var commandStartedTcs = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCommandTcs = new TaskCompletionSource<ResourceCommandResponseViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, cancellationToken) =>
            {
                commandStartedTcs.SetResult(cancellationToken);
                return finishCommandTcs.Task;
            });
        var executor = CreateExecutor(dashboardClient, out var notificationService, out var toastService);
        var command = CreateCommand();
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);
        ToastParameters? shownToast = null;
        ToastParameters? updatedToast = null;
        toastService.OnShow += (_, parameters, _) => shownToast = parameters;
        toastService.OnUpdate += (_, parameters) => updatedToast = parameters;

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        var commandToken = await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startingNotification = Assert.Single(notificationService.GetNotifications());

        Assert.Equal("Localized:ResourceCommandCancel", startingNotification.Entry.PrimaryAction?.Text);
        Assert.Equal("Localized:ResourceCommandCancel", shownToast?.PrimaryAction);

        await startingNotification.Entry.PrimaryAction!.OnClick(new ServiceCollection().BuildServiceProvider());

        Assert.True(commandToken.IsCancellationRequested);
        var cancelingNotification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandCanceling", cancelingNotification.Entry.Title);
        Assert.Null(cancelingNotification.Entry.PrimaryAction);
        Assert.Null(updatedToast?.PrimaryAction);

        finishCommandTcs.SetResult(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Cancelled
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        var canceledNotification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandCanceled", canceledNotification.Entry.Title);
        Assert.Null(canceledNotification.Entry.PrimaryAction);
    }

    [Fact]
    public async Task ExecuteAsyncCore_CancelNotificationAction_IsIdempotent()
    {
        var commandStartedTcs = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCommandTcs = new TaskCompletionSource<ResourceCommandResponseViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, cancellationToken) =>
            {
                commandStartedTcs.SetResult(cancellationToken);
                return finishCommandTcs.Task;
            });
        var executor = CreateExecutor(dashboardClient, out var notificationService, out var toastService);
        var command = CreateCommand();
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);
        var toastUpdateCount = 0;
        toastService.OnUpdate += (_, _) => toastUpdateCount++;

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        var commandToken = await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startingNotification = Assert.Single(notificationService.GetNotifications());
        var cancelAction = startingNotification.Entry.PrimaryAction;

        Assert.NotNull(cancelAction);

        await cancelAction.OnClick(new ServiceCollection().BuildServiceProvider());
        await cancelAction.OnClick(new ServiceCollection().BuildServiceProvider());

        Assert.True(commandToken.IsCancellationRequested);
        Assert.Equal(1, toastUpdateCount);
        var cancelingNotification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandCanceling", cancelingNotification.Entry.Title);

        finishCommandTcs.SetResult(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Cancelled
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExecuteAsyncCore_SuccessfulCommandWithoutResult_ClearsCancelToastAction()
    {
        var commandStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCommandTcs = new TaskCompletionSource<ResourceCommandResponseViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            executeResourceCommand: (_, _, _, _, _) =>
            {
                commandStartedTcs.SetResult();
                return finishCommandTcs.Task;
            });
        var executor = CreateExecutor(dashboardClient, out var notificationService, out var toastService);
        var command = CreateCommand();
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);
        ToastParameters? updatedToast = null;
        toastService.OnUpdate += (_, parameters) => updatedToast = parameters;

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        finishCommandTcs.SetResult(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Succeeded
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(updatedToast?.PrimaryAction);
        Assert.Null(updatedToast?.SecondaryAction);
        var notification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandSuccess", notification.Entry.Title);
        Assert.Null(notification.Entry.PrimaryAction);
    }

    private static DashboardCommandExecutor CreateExecutor(TestDashboardClient dashboardClient, out INotificationService notificationService, out ToastService toastService)
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        var dialogService = new DashboardDialogService(
            new TestDialogService(),
            new TestStringLocalizer<Dialogs>(),
            dimensionManager);
        toastService = new ToastService();
        notificationService = new NotificationService(TimeProvider.System);
        var telemetryService = new DashboardTelemetryService(NullLogger<DashboardTelemetryService>.Instance, new TestDashboardTelemetrySender());

        return new DashboardCommandExecutor(
            dashboardClient,
            dialogService,
            toastService,
            new TestStringLocalizer<Dashboard.Resources.Resources>(),
            new TestNavigationManager(),
            telemetryService,
            notificationService);
    }

    private static CommandViewModel CreateCommand()
    {
        return new CommandViewModel(
            "test-command",
            CommandViewModelState.Enabled,
            "Test command",
            "Test command description",
            confirmationMessage: "",
            ImmutableArray<ProtoInteractionInput>.Empty,
            isHighlighted: false,
            iconName: string.Empty,
            IconVariant.Regular);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}
