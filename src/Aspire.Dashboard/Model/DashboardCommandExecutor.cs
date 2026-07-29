// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model;

public sealed class DashboardCommandExecutor(
    IDashboardClient dashboardClient,
    DashboardDialogService dialogService,
    IDashboardToastService toastService,
    IStringLocalizer<Dashboard.Resources.Resources> loc,
    NavigationManager navigationManager,
    DashboardTelemetryService telemetryService,
    INotificationService notificationService)
{
    private readonly HashSet<(string ResourceName, string CommandName)> _executingCommands = [];
    private readonly object _lock = new object();

    public bool IsExecuting(string resourceName, string commandName)
    {
        lock (_lock)
        {
            return _executingCommands.Contains((resourceName, commandName));
        }
    }

    public async Task ExecuteAsync(ResourceViewModel resource, CommandViewModel command, Func<ResourceViewModel, string> getResourceName)
    {
        var executingCommandKey = (resource.Name, command.Name);
        lock (_lock)
        {
            _executingCommands.Add(executingCommandKey);
        }

        var startEvent = telemetryService.StartOperation(TelemetryEventKeys.ExecuteCommand,
            new Dictionary<string, AspireTelemetryProperty>
            {
                { TelemetryPropertyKeys.ResourceType, new AspireTelemetryProperty(TelemetryPropertyValues.GetResourceTypeTelemetryValue(resource.ResourceType, resource.SupportsDetailedTelemetry)) },
                { TelemetryPropertyKeys.CommandName, new AspireTelemetryProperty(TelemetryPropertyValues.GetCommandNameTelemetryValue(command.Name)) },
            });

        var operationId = startEvent.Properties.FirstOrDefault();

        try
        {
            await ExecuteAsyncCore(resource, command, getResourceName).ConfigureAwait(false);

            if (operationId is not null)
            {
                telemetryService.EndOperation(operationId, TelemetryResult.Success);
            }
        }
        catch (Exception ex)
        {
            if (operationId is not null)
            {
                telemetryService.EndOperation(operationId, TelemetryResult.Failure, ex.Message);
            }
        }
        finally
        {
            // There may be a delay between a command finishing and the arrival of a new resource state with updated commands sent to the client.
            // For example:
            // 1. Click the stop command on a resource. The command is disabled while running.
            // 2. The stop command finishes, and it is re-enabled.
            // 3. A new resource state arrives in the dashboard, replacing the stop command with the run command.
            //
            // To prevent the stop command from being temporarily enabled, introduce a delay between a command finishing and re-enabling it in the dashboard.
            // This delay is chosen to balance avoiding an incorrect temporary state (since the new resource state should arrive within a second) and maintaining responsiveness.
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            lock (_lock)
            {
                _executingCommands.Remove(executingCommandKey);
            }
        }
    }

    public async Task ExecuteAsyncCore(ResourceViewModel resource, CommandViewModel command, Func<ResourceViewModel, string> getResourceName)
    {
        if (!string.IsNullOrWhiteSpace(command.ConfirmationMessage))
        {
            var dialogReference = await dialogService.ShowConfirmationAsync(command.ConfirmationMessage).ConfigureAwait(false);
            var result = await dialogReference.Result.ConfigureAwait(false);
            if (result.Cancelled)
            {
                return;
            }
        }

        var messageBarStartingTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandStarting)], command.GetDisplayName());
        var toastStartingTitle = $"{getResourceName(resource)} {messageBarStartingTitle}";

        using var executeCommandCts = new CancellationTokenSource();
        var cancelingTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandCanceling)], command.GetDisplayName());
        var cancelRequested = false;
        var cancelLock = new object();

        // When a resource command starts a toast is immediately shown.
        // The toast is open for a certain amount of time and then automatically closed.
        // When the resource command is finished the status is displayed in a toast.
        // Either the open toast is updated and its time is exteneded, or the a new toast is shown with the finished status.
        // Because of this logic we need to manage opening and closing the toasts manually.
        var toastId = Guid.NewGuid().ToString();
        var toast = new DashboardToast
        {
            Id = toastId,
            Title = toastStartingTitle,
            Intent = NotificationIntent.Info,
            IsProgress = true
        };

        // Track whether toast is closed by timeout or user action.
        var toastClosed = false;
        Action<string> closeCallback = (id) =>
        {
            if (id == toastId)
            {
                toastClosed = true;
            }
        };

        string? progressNotificationId = null;
        progressNotificationId = notificationService.AddNotification(new NotificationEntry
        {
            Title = messageBarStartingTitle,
            Intent = NotificationIntent.Info,
            PrimaryAction = CreateCancelNotificationAction(loc, RequestCancelAsync)
        });

        toast.PrimaryAction = new DashboardToastAction
        {
            Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandCancel)],
            OnClick = RequestCancelAsync
        };

        ResourceCommandResponseViewModel response;
        // The CTS intentionally outlives the command execution to ensure we can close the toast in all scenarios
        // e.g., even if the command execution fails or the toast is still open when the command finishes.
        // It's ok to let it be cleaned up by GC when the short CancelAfter completes.
        var closeToastCts = new CancellationTokenSource();
        try
        {
            toastService.OnClose += closeCallback;
            // Show a toast immediately to indicate the command is starting.
            toastService.Show(toast);

            closeToastCts.Token.Register(() =>
            {
                toastService.Close(toastId);
            });
            closeToastCts.CancelAfter(DashboardUIHelpers.ToastTimeout);

            try
            {
                response = await dashboardClient.ExecuteResourceCommandAsync(
                    resource.Name,
                    resource.ResourceType,
                    command,
                    new ExecuteResourceCommandOptions(),
                    executeCommandCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (executeCommandCts.IsCancellationRequested)
            {
                response = new ResourceCommandResponseViewModel
                {
                    Kind = ResourceCommandResponseKind.Cancelled
                };
            }
        }
        finally
        {
            toastService.OnClose -= closeCallback;
        }

        // Update toast and notification with the result.
        ClearToastActions(toast);
        if (response.Kind == ResourceCommandResponseKind.Succeeded)
        {
            var successTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandSuccess)], command.GetDisplayName());
            toast.Title = $"{getResourceName(resource)} {successTitle}";
            toast.Intent = NotificationIntent.Success;
            toast.IsProgress = false;

            if (response.Result is not null)
            {
                toast.PrimaryAction = new DashboardToastAction
                {
                    Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)],
                    OnClick = () => OpenViewResponseDialogAsync(dialogService, command, response)
                };
            }

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = successTitle,
                Body = response.Message,
                Intent = NotificationIntent.Success,
                PrimaryAction = response.Result is not null ? CreateViewResponseNotificationAction(loc, command, response) : null
            });

            if (response.Result?.DisplayImmediately == true)
            {
                await OpenViewResponseDialogAsync(dialogService, command, response).ConfigureAwait(false);
            }
        }
        else if (response.Kind == ResourceCommandResponseKind.Cancelled)
        {
            var canceledTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandCanceled)], command.GetDisplayName());

            // For cancelled commands, just close the existing toast and don't show any success or error message.
            if (!toastClosed)
            {
                toastService.Close(toastId);
            }

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = canceledTitle,
                Body = response.Message,
                Intent = NotificationIntent.Info,
            });
            closeToastCts.Dispose();
            return;
        }
        else
        {
            var failedTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandFailed)], command.GetDisplayName());
            toast.Title = $"{getResourceName(resource)} {failedTitle}";
            toast.Intent = NotificationIntent.Error;
            toast.IsProgress = false;
            toast.PrimaryAction = new DashboardToastAction
            {
                Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandToastViewLogs)],
                OnClick = () =>
                {
                    navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: getResourceName(resource)));
                    return Task.CompletedTask;
                }
            };
            toast.Details = response.Message;

            if (response.Result is not null)
            {
                toast.SecondaryAction = new DashboardToastAction
                {
                    Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)],
                    OnClick = () => OpenViewResponseDialogAsync(dialogService, command, response)
                };
            }

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = failedTitle,
                Body = response.Message,
                Intent = NotificationIntent.Error,
                PrimaryAction = response.Result is not null ? CreateViewResponseNotificationAction(loc, command, response) : null
            });

            if (response.Result?.DisplayImmediately == true)
            {
                await OpenViewResponseDialogAsync(dialogService, command, response).ConfigureAwait(false);
            }
        }

        if (!toastClosed)
        {
            // Extend cancel time.
            closeToastCts.CancelAfter(DashboardUIHelpers.ToastTimeout);

            // Update the open toast to display result. This only works if the toast is still open.
            if (!toastService.Update(toastId, toast))
            {
                toastService.Show(toast, DashboardUIHelpers.ToastTimeout);
                closeToastCts.Dispose();
            }
        }
        else
        {
            // Show toast to display result.
            toastService.Show(toast, DashboardUIHelpers.ToastTimeout);

            closeToastCts.Dispose();
        }

        Task RequestCancelAsync()
        {
            lock (cancelLock)
            {
                if (cancelRequested)
                {
                    return Task.CompletedTask;
                }

                cancelRequested = true;
            }

            executeCommandCts.Cancel();
            ClearToastActions(toast);
            toast.Title = $"{getResourceName(resource)} {cancelingTitle}";
            toast.Intent = NotificationIntent.Info;
            toast.IsProgress = true;

            if (!toastClosed)
            {
                toastService.Update(toastId, toast);
            }

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = cancelingTitle,
                Intent = NotificationIntent.Info,
            });

            return Task.CompletedTask;
        }

        string GetProgressNotificationId()
        {
            return progressNotificationId ?? throw new InvalidOperationException("The progress notification has not been created.");
        }
    }

    private static void ClearToastActions(DashboardToast toast)
    {
        toast.PrimaryAction = null;
        toast.SecondaryAction = null;
    }

    private static NotificationAction CreateCancelNotificationAction(IStringLocalizer<Dashboard.Resources.Resources> loc, Func<Task> onCancelAsync)
    {
        return new NotificationAction
        {
            Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandCancel)],
            OnClick = _ => onCancelAsync()
        };
    }

    private static NotificationAction CreateViewResponseNotificationAction(IStringLocalizer<Dashboard.Resources.Resources> loc, CommandViewModel command, ResourceCommandResponseViewModel response)
    {
        return new NotificationAction
        {
            Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)],
            OnClick = (services) =>
            {
                // Get dialog service from passed in services since this data is long lived.
                // Using the dialog service from executor could cause closure over scoped services.
                var dialogService = services.GetRequiredService<DashboardDialogService>();
                return OpenViewResponseDialogAsync(dialogService, command, response);
            }
        };
    }

    private static async Task OpenViewResponseDialogAsync(DashboardDialogService dialogService, CommandViewModel command, ResourceCommandResponseViewModel response)
    {
        var fixedFormat = response.Result!.Format switch
        {
            CommandResultFormat.Json => DashboardUIHelpers.JsonFormat,
            CommandResultFormat.Markdown => DashboardUIHelpers.MarkdownFormat,
            _ => null
        };

        var reference = await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
        {
            DialogService = dialogService,
            ValueDescription = command.GetDisplayName(),
            Value = response.Result.Value,
            FixedFormat = fixedFormat
        }).ConfigureAwait(true);

        // Await the result to wait here until the dialog is closed.
        await reference.Result.ConfigureAwait(true);
    }
}
