// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using DialogResources = Aspire.Dashboard.Resources.Dialogs;
using FluentMessageIntent = Microsoft.FluentUI.AspNetCore.Components.MessageIntent;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

public sealed class DashboardCommandExecutor(
    IDashboardClient dashboardClient,
    DashboardDialogService dialogService,
    IToastService toastService,
    IStringLocalizer<Dashboard.Resources.Resources> loc,
    IStringLocalizer<DialogResources> dialogsLoc,
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

        var (argumentsCanceled, arguments) = await GetCommandArgumentsAsync(command).ConfigureAwait(false);
        if (argumentsCanceled)
        {
            return;
        }

        var messageBarStartingTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandStarting)], command.GetDisplayName());
        var toastStartingTitle = $"{getResourceName(resource)} {messageBarStartingTitle}";

        // Add a notification to the notification center for the in-progress command.
        var progressNotificationId = notificationService.AddNotification(new NotificationEntry
        {
            Title = messageBarStartingTitle,
            Intent = FluentMessageIntent.Info,
        });

        // When a resource command starts a toast is immediately shown.
        // The toast is open for a certain amount of time and then automatically closed.
        // When the resource command is finished the status is displayed in a toast.
        // Either the open toast is updated and its time is exteneded, or the a new toast is shown with the finished status.
        // Because of this logic we need to manage opening and closing the toasts manually.
        var toastParameters = new ToastParameters<CommunicationToastContent>()
        {
            Id = Guid.NewGuid().ToString(),
            Intent = ToastIntent.Progress,
            Title = toastStartingTitle,
            Content = new CommunicationToastContent(),
            Timeout = 0 // App logic will handle closing the toast
        };

        // Track whether toast is closed by timeout or user action.
        var toastClosed = false;
        Action<string?> closeCallback = (id) =>
        {
            if (id == toastParameters.Id)
            {
                toastClosed = true;
            }
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
            toastService.ShowCommunicationToast(toastParameters);

            closeToastCts.Token.Register(() =>
            {
                toastService.CloseToast(toastParameters.Id);
            });
            closeToastCts.CancelAfter(DashboardUIHelpers.ToastTimeout);

            response = await dashboardClient.ExecuteResourceCommandAsync(resource.Name, resource.ResourceType, command, arguments, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            toastService.OnClose -= closeCallback;
        }

        // Update toast and notification with the result.
        if (response.Kind == ResourceCommandResponseKind.Succeeded)
        {
            var successTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandSuccess)], command.GetDisplayName());
            toastParameters.Title = $"{getResourceName(resource)} {successTitle}";
            toastParameters.Intent = ToastIntent.Success;
            toastParameters.Icon = GetIntentIcon(ToastIntent.Success);

            if (response.Result is not null)
            {
                toastParameters.PrimaryAction = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)];
                toastParameters.OnPrimaryAction = EventCallback.Factory.Create<ToastResult>(this, () => OpenViewResponseDialogAsync(command, response));
            }

            notificationService.ReplaceNotification(progressNotificationId, new NotificationEntry
            {
                Title = successTitle,
                Body = response.Message,
                Intent = FluentMessageIntent.Success,
                PrimaryAction = response.Result is not null ? CreateViewResponseNotificationAction(command, response) : null
            });

            if (response.Result?.DisplayImmediately == true)
            {
                await OpenViewResponseDialogAsync(command, response).ConfigureAwait(false);
            }
        }
        else if (response.Kind == ResourceCommandResponseKind.Cancelled)
        {
            // For cancelled commands, just close the existing toast and don't show any success or error message.
            if (!toastClosed)
            {
                toastService.CloseToast(toastParameters.Id);
            }

            notificationService.RemoveNotification(progressNotificationId);
            closeToastCts.Dispose();
            return;
        }
        else
        {
            var failedTitle = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandFailed)], command.GetDisplayName());
            toastParameters.Title = $"{getResourceName(resource)} {failedTitle}";
            toastParameters.Intent = ToastIntent.Error;
            toastParameters.Icon = GetIntentIcon(ToastIntent.Error);
            toastParameters.PrimaryAction = loc[nameof(Dashboard.Resources.Resources.ResourceCommandToastViewLogs)];
            toastParameters.OnPrimaryAction = EventCallback.Factory.Create<ToastResult>(this, () => navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: getResourceName(resource))));
            toastParameters.Content.Details = response.Message;

            if (response.Result is not null)
            {
                toastParameters.SecondaryAction = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)];
                toastParameters.OnSecondaryAction = EventCallback.Factory.Create<ToastResult>(this, () => OpenViewResponseDialogAsync(command, response));
            }

            notificationService.ReplaceNotification(progressNotificationId, new NotificationEntry
            {
                Title = failedTitle,
                Body = response.Message,
                Intent = FluentMessageIntent.Error,
                PrimaryAction = response.Result is not null ? CreateViewResponseNotificationAction(command, response) : null
            });

            if (response.Result?.DisplayImmediately == true)
            {
                await OpenViewResponseDialogAsync(command, response).ConfigureAwait(false);
            }
        }

        if (!toastClosed)
        {
            // Extend cancel time.
            closeToastCts.CancelAfter(DashboardUIHelpers.ToastTimeout);

            // Update the open toast to display result. This only works if the toast is still open.
            toastService.UpdateToast(toastParameters.Id, toastParameters);
        }
        else
        {
            toastParameters.Timeout = null; // Let the toast close automatically.

            // Show toast to display result.
            toastService.ShowCommunicationToast(toastParameters);

            closeToastCts.Dispose();
        }
    }

    // Copied from FluentUI.
    private static (Icon Icon, Color Color)? GetIntentIcon(ToastIntent intent)
    {
        return intent switch
        {
            ToastIntent.Success => (new Icons.Filled.Size24.CheckmarkCircle(), Color.Success),
            ToastIntent.Warning => (new Icons.Filled.Size24.Warning(), Color.Warning),
            ToastIntent.Error => (new Icons.Filled.Size24.DismissCircle(), Color.Error),
            ToastIntent.Info => (new Icons.Filled.Size24.Info(), Color.Info),
            ToastIntent.Progress => (new Icons.Regular.Size24.Flash(), Color.Neutral),
            ToastIntent.Upload => (new Icons.Regular.Size24.ArrowUpload(), Color.Neutral),
            ToastIntent.Download => (new Icons.Regular.Size24.ArrowDownload(), Color.Neutral),
            ToastIntent.Event => (new Icons.Regular.Size24.CalendarLtr(), Color.Neutral),
            ToastIntent.Mention => (new Icons.Regular.Size24.Person(), Color.Neutral),
            ToastIntent.Custom => null,
            _ => throw new InvalidOperationException()
        };
    }

    private NotificationAction CreateViewResponseNotificationAction(CommandViewModel command, ResourceCommandResponseViewModel response)
    {
        return new NotificationAction
        {
            Text = loc[nameof(Dashboard.Resources.Resources.ResourceCommandViewResponse)],
            OnClick = () => OpenViewResponseDialogAsync(command, response)
        };
    }

    private async Task<(bool Canceled, Value? Arguments)> GetCommandArgumentsAsync(CommandViewModel command)
    {
        if (command.ArgumentInputs.IsDefaultOrEmpty)
        {
            return (false, null);
        }

        var interaction = new WatchInteractionsResponseUpdate
        {
            Title = command.GetDisplayName(),
            Message = command.GetDisplayDescription() ?? string.Empty,
            PrimaryButtonText = command.GetDisplayName(),
            SecondaryButtonText = dialogsLoc[nameof(DialogResources.InteractionButtonCancel)],
            ShowDismiss = true,
            ShowSecondaryButton = true,
            InputsDialog = new InteractionInputsDialog()
        };
        interaction.InputsDialog.InputItems.AddRange(command.ArgumentInputs.Select(input => input.Clone()));

        var completion = new TaskCompletionSource<IReadOnlyList<InteractionInput>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDialogReference? dialogReference = null;
        var viewModel = new InteractionsInputsDialogViewModel
        {
            Interaction = interaction,
            Message = WebUtility.HtmlEncode(interaction.Message),
            OnSubmitCallback = async (submittedInteraction, update) =>
            {
                if (update)
                {
                    return;
                }

                completion.TrySetResult(submittedInteraction.InputsDialog.InputItems.ToList());
                if (dialogReference is not null)
                {
                    await dialogReference.CloseAsync().ConfigureAwait(false);
                }
            }
        };

        var width = dialogService.IsDesktop ? "75vw" : "100vw";
        var parameters = new DialogParameters
        {
            ShowDismiss = true,
            PrimaryAction = command.GetDisplayName(),
            SecondaryAction = dialogsLoc[nameof(DialogResources.InteractionButtonCancel)],
            PreventDismissOnOverlayClick = true,
            Title = command.GetDisplayName(),
            Width = $"min(650px, {width})",
            OnDialogResult = EventCallback.Factory.Create<DialogResult>(this, result =>
            {
                if (result.Cancelled)
                {
                    completion.TrySetResult(null);
                }

                return Task.CompletedTask;
            })
        };

        dialogReference = await dialogService.ShowDialogAsync<InteractionsInputDialog>(viewModel, parameters).ConfigureAwait(false);
        var inputs = await completion.Task.ConfigureAwait(false);

        return inputs is null
            ? (true, null)
            : (false, CreateCommandArguments(inputs));
    }

    internal static Value CreateCommandArguments(IReadOnlyList<InteractionInput> inputs)
    {
        var arguments = new Struct();
        foreach (var input in inputs)
        {
            var value = CreateCommandArgumentValue(input);
            if (value is not null)
            {
                arguments.Fields.Add(input.Name, value);
            }
        }

        return Value.ForStruct(arguments);
    }

    private static Value? CreateCommandArgumentValue(InteractionInput input)
    {
        var value = input.Value;
        return input.InputType switch
        {
            InputType.Boolean => Value.ForBool(bool.TryParse(value, out var result) && result),
            InputType.Number when string.IsNullOrWhiteSpace(value) => null,
            InputType.Number when double.TryParse(value, CultureInfo.InvariantCulture, out var result) => Value.ForNumber(result),
            InputType.Text or InputType.SecretText or InputType.Choice when !string.IsNullOrEmpty(value) => Value.ForString(value),
            _ => null
        };
    }

    private async Task OpenViewResponseDialogAsync(CommandViewModel command, ResourceCommandResponseViewModel response)
    {
        var fixedFormat = response.Result!.Format switch
        {
            CommandResultFormat.Json => DashboardUIHelpers.JsonFormat,
            CommandResultFormat.Markdown => DashboardUIHelpers.MarkdownFormat,
            _ => null
        };

        await TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
        {
            DialogService = dialogService,
            ValueDescription = command.GetDisplayName(),
            Value = response.Result.Value,
            FixedFormat = fixedFormat
        }).ConfigureAwait(false);
    }
}
