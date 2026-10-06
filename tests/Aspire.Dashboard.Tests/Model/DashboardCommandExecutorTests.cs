// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using DashboardNotificationService = Aspire.Dashboard.Model.NotificationService;
using FluentNotificationService = Microsoft.FluentUI.AspNetCore.Components.NotificationService;
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

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        var commandToken = await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startingNotification = Assert.Single(notificationService.GetNotifications());
        var toast = GetToastInstance(toastService);

        Assert.Equal("Localized:ResourceCommandCancel", startingNotification.Entry.PrimaryAction?.Text);
        Assert.Equal("Localized:ResourceCommandCancel", toast.Options.QuickAction1.Label);

        await startingNotification.Entry.PrimaryAction!.OnClick(new ServiceCollection().BuildServiceProvider());

        Assert.True(commandToken.IsCancellationRequested);
        var cancelingNotification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandCanceling", cancelingNotification.Entry.Title);
        Assert.Null(cancelingNotification.Entry.PrimaryAction);
        Assert.Null(toast.Options.QuickAction1.Label);

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

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        var commandToken = await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startingNotification = Assert.Single(notificationService.GetNotifications());
        var cancelAction = startingNotification.Entry.PrimaryAction;
        var toast = GetToastInstance(toastService);

        Assert.NotNull(cancelAction);

        await cancelAction.OnClick(new ServiceCollection().BuildServiceProvider());
        await cancelAction.OnClick(new ServiceCollection().BuildServiceProvider());

        Assert.True(commandToken.IsCancellationRequested);
    Assert.Equal("api Localized:ResourceCommandCanceling", toast.Options.Title);
        var cancelingNotification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandCanceling", cancelingNotification.Entry.Title);

        finishCommandTcs.SetResult(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Cancelled
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExecuteAsyncCore_OpenProgressToast_UpdatesWithResult()
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

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startingToast = GetToastInstance(toastService);

        Assert.Equal("resource-command-toast", startingToast.Options.Class);
        Assert.Equal("350px", startingToast.Options.Width);
        Assert.Equal(TimeSpan.Zero, startingToast.Options.Lifetime);

        finishCommandTcs.SetResult(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Succeeded
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        var resultToast = GetToastInstance(toastService);
        Assert.Same(startingToast, resultToast);
        Assert.Equal(startingToast.Id, resultToast.Id);
        Assert.Equal(ToastIntent.Success, resultToast.Options.Intent);
        Assert.Equal("api Localized:ResourceCommandSuccess", resultToast.Options.Title);
        Assert.Null(resultToast.Options.QuickAction1.Label);
        Assert.Null(resultToast.Options.QuickAction2.Label);
        Assert.Equal(TimeSpan.Zero, resultToast.Options.Lifetime);
        var notification = Assert.Single(notificationService.GetNotifications());
        Assert.Equal("Localized:ResourceCommandSuccess", notification.Entry.Title);
        Assert.Null(notification.Entry.PrimaryAction);
    }

    [Fact]
    public async Task ExecuteAsyncCore_ClosedProgressToast_ShowsNewResultToast()
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
        var executor = CreateExecutor(dashboardClient, out _, out var toastService);
        var command = CreateCommand();
        var resource = ModelTestHelpers.CreateResource(resourceName: "api", commands: [command]);

        var executeTask = executor.ExecuteAsyncCore(resource, command, r => r.DisplayName);
        await commandStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startingToast = GetToastInstance(toastService);
        await toastService.Service.CloseAsync(startingToast);

        finishCommandTcs.SetResult(new ResourceCommandResponseViewModel
        {
            Kind = ResourceCommandResponseKind.Succeeded
        });

        await executeTask.WaitAsync(TimeSpan.FromSeconds(10));

        var resultToast = GetToastInstance(toastService);
        Assert.NotEqual(startingToast.Id, resultToast.Id);
        Assert.Equal(DashboardUIHelpers.ToastTimeout, resultToast.Options.Lifetime);
    }
    private static DashboardCommandExecutor CreateExecutor(TestDashboardClient dashboardClient, out Aspire.Dashboard.Model.INotificationService notificationService, out TestNotificationService toastService)
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        var dialogService = new DashboardDialogService(
            new TestDialogService(),
            new TestStringLocalizer<Dialogs>(),
            dimensionManager);
        toastService = new TestNotificationService();
        notificationService = new DashboardNotificationService(TimeProvider.System);
        var telemetryService = new DashboardTelemetryService(NullLogger<DashboardTelemetryService>.Instance, new TestDashboardTelemetrySender());

        return new DashboardCommandExecutor(
            dashboardClient,
            dialogService,
            toastService.Service,
            new TestStringLocalizer<Dashboard.Resources.Resources>(),
            new TestNavigationManager(),
            telemetryService,
            notificationService);
    }

    private static IToastInstance GetToastInstance(TestNotificationService toastService)
    {
        return Assert.IsAssignableFrom<IToastInstance>(toastService.LastInstance);
    }

    private sealed class TestNotificationService
    {
        private static readonly MethodInfo s_subscribeMethod = typeof(FluentNotificationService)
            .GetMethod("Subscribe", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public TestNotificationService()
        {
            var services = new ServiceCollection();
            services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJSRuntime>();
            Service = new FluentNotificationService(services.BuildServiceProvider());
            s_subscribeMethod.Invoke(Service, ["Test", (Func<INotificationInstance, Task>)OnUpdatedAsync]);
        }

        public FluentNotificationService Service { get; }

        public IToastInstance? LastInstance { get; private set; }

        private Task OnUpdatedAsync(INotificationInstance instance)
        {
            LastInstance = instance as IToastInstance;
            return Task.CompletedTask;
        }
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
