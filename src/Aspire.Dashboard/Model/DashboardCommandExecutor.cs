// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using FluentMessageIntent = Microsoft.FluentUI.AspNetCore.Components.MessageBarIntent;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

public sealed class DashboardCommandExecutor(
    IDashboardClient dashboardClient,
    DashboardDialogService dialogService,
    Microsoft.FluentUI.AspNetCore.Components.INotificationService toastService,
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
            var result = await dialogService.ShowConfirmationAsync(command.ConfirmationMessage).ConfigureAwait(false);
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

        // Fluent v5 retains the ToastOptions instance, which lets us update an open progress toast in place.
        // Use a manual lifetime because Fluent does not expose a way to restart a rendered toast's lifetime
        // when those options change from progress to a result.
        var toastId = Guid.NewGuid().ToString();
        var toastOptions = new ToastOptions
        {
            Id = toastId,
            Class = "resource-command-toast",
            Width = "350px",
            Intent = ToastIntent.Progress,
            Title = toastStartingTitle,
            Lifetime = TimeSpan.Zero
        };

        string? progressNotificationId = null;
        progressNotificationId = notificationService.AddNotification(new NotificationEntry
        {
            Title = messageBarStartingTitle,
            Intent = FluentMessageIntent.Info,
            PrimaryAction = CreateCancelNotificationAction(loc, RequestCancelAsync)
        });

        toastOptions.QuickAction1.Label = loc[nameof(Dashboard.Resources.Resources.ResourceCommandCancel)];
        toastOptions.QuickAction1.OnClickAsync = _ => RequestCancelAsync();

        ResourceCommandResponseViewModel response;
        using var progressToastCloseCts = new CancellationTokenSource();
        await toastService.ShowToastAsync(toastOptions).ConfigureAwait(false);
        // Keep the progress timeout cancellable so command completion can replace it with a full result timeout.
        var progressToastCloseTask = CloseToastAfterDelayAsync(toastId, progressToastCloseCts.Token);

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

        // Update toast and notification with the result.
        ClearToastActions(toastOptions);
        if (response.Kind == ResourceCommandResponseKind.Succeeded)
        {
            var successTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandSuccess)], command.GetDisplayName());
            toastOptions.Title = $"{getResourceName(resource)} {successTitle}";
            toastOptions.Intent = ToastIntent.Success;
            toastOptions.Icon = GetIntentIcon(ToastIntent.Success);

            if (response.Result is not null)
            {
                toastOptions.QuickAction1.Label = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)];
                toastOptions.QuickAction1.OnClickAsync = _ => OpenViewResponseDialogAsync(dialogService, command, response);
            }

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = successTitle,
                Body = response.Message,
                Intent = FluentMessageIntent.Success,
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
            progressToastCloseCts.Cancel();
            await progressToastCloseTask.ConfigureAwait(false);
            await toastService.CloseAsync(toastId).ConfigureAwait(false);

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = canceledTitle,
                Body = response.Message,
                Intent = FluentMessageIntent.Info,
            });
            return;
        }
        else
        {
            var failedTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandFailed)], command.GetDisplayName());
            toastOptions.Title = $"{getResourceName(resource)} {failedTitle}";
            toastOptions.Intent = ToastIntent.Error;
            toastOptions.Icon = GetIntentIcon(ToastIntent.Error);
            toastOptions.QuickAction1.Label = loc[nameof(Dashboard.Resources.Resources.ResourceCommandToastViewLogs)];
            toastOptions.QuickAction1.OnClickAsync = _ =>
            {
                navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: getResourceName(resource)));
                return Task.CompletedTask;
            };
            toastOptions.Message = response.Message;

            if (response.Result is not null)
            {
                toastOptions.QuickAction2.Label = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)];
                toastOptions.QuickAction2.OnClickAsync = _ => OpenViewResponseDialogAsync(dialogService, command, response);
            }

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = failedTitle,
                Body = response.Message,
                Intent = FluentMessageIntent.Error,
                PrimaryAction = response.Result is not null ? CreateViewResponseNotificationAction(loc, command, response) : null
            });

            if (response.Result?.DisplayImmediately == true)
            {
                await OpenViewResponseDialogAsync(dialogService, command, response).ConfigureAwait(false);
            }
        }

        progressToastCloseCts.Cancel();
        await progressToastCloseTask.ConfigureAwait(false);
        if (IsToastOpen(toastId))
        {
            // ToastInstance references toastOptions, so the notification replacement above causes MainLayout
            // to rerender FluentToastProvider with the result values without replacing the toast component.
            // Restart the close delay so the result remains visible for the full timeout.
            _ = CloseToastAfterDelayAsync(toastId, CancellationToken.None);
        }
        else
        {
            // Fluent keeps dismissed toasts registered during their exit animation. Use a fresh ID when the
            // progress toast is no longer open and let Fluent manage the new result toast's lifetime.
            toastOptions.Id = Guid.NewGuid().ToString();
            toastOptions.Lifetime = DashboardUIHelpers.ToastTimeout;
            await toastService.ShowToastAsync(toastOptions).ConfigureAwait(false);
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
            ClearToastActions(toastOptions);
            toastOptions.Title = $"{getResourceName(resource)} {cancelingTitle}";
            toastOptions.Intent = ToastIntent.Progress;
            toastOptions.Icon = GetIntentIcon(ToastIntent.Progress);
            toastOptions.Lifetime = TimeSpan.Zero;

            notificationService.ReplaceNotification(GetProgressNotificationId(), new NotificationEntry
            {
                Title = cancelingTitle,
                Intent = FluentMessageIntent.Info,
            });

            return Task.CompletedTask;
        }

        bool IsToastOpen(string id)
        {
            // Dismissed instances remain discoverable until Fluent finishes their exit animation, but they
            // can no longer be updated in place.
            return toastService.GetToastInstance(id)?.LifecycleStatus is ToastLifecycleStatus.Queued or ToastLifecycleStatus.Visible;
        }

        async Task CloseToastAfterDelayAsync(string id, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(DashboardUIHelpers.ToastTimeout, cancellationToken).ConfigureAwait(false);
                await toastService.CloseAsync(id).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Command completion cancels the progress timeout before scheduling the result timeout.
            }
        }

        string GetProgressNotificationId()
        {
            return progressNotificationId ?? throw new InvalidOperationException("The progress notification has not been created.");
        }
    }

    // Copied from FluentUI.
    private static Icon? GetIntentIcon(ToastIntent intent)
    {
        return intent switch
        {
            ToastIntent.Success => new Icons.Filled.Size24.CheckmarkCircle(),
            ToastIntent.Warning => new Icons.Filled.Size24.Warning(),
            ToastIntent.Error => new Icons.Filled.Size24.DismissCircle(),
            ToastIntent.Info => new Icons.Filled.Size24.Info(),
            ToastIntent.Progress => new Icons.Regular.Size24.Flash(),
            _ => throw new InvalidOperationException()
        };
    }

    private static void ClearToastActions(ToastOptions toastOptions)
    {
        toastOptions.QuickAction1.Label = null;
        toastOptions.QuickAction1.OnClickAsync = null;
        toastOptions.QuickAction2.Label = null;
        toastOptions.QuickAction2.OnClickAsync = null;
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
