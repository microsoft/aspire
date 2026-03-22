// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

public sealed class DashboardCommandExecutor(
    IDashboardClient dashboardClient,
    IDialogService dialogService,
    IToastService toastService,
    IStringLocalizer<Dashboard.Resources.Resources> loc,
    NavigationManager navigationManager,
    DashboardTelemetryService telemetryService)
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

        var messageResourceName = getResourceName(resource);

        // When a resource command starts a toast is immediately shown.
        // The toast is open for a certain amount of time and then automatically closed.
        // When the resource command is finished the status is displayed in a toast.
        // Either the open toast is updated and its time is exteneded, or the a new toast is shown with the finished status.
        // Because of this logic we need to manage opening and closing the toasts manually.
        var toastParameters = new ToastParameters<CommunicationToastContent>()
        {
            Id = Guid.NewGuid().ToString(),
            Intent = ToastIntent.Progress,
            Title = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandStarting)], messageResourceName, command.GetDisplayName()),
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
        CancellationTokenSource closeToastCts;
        try
        {
            toastService.OnClose += closeCallback;
            // Show a toast immediately to indicate the command is starting.
            toastService.ShowCommunicationToast(toastParameters);

            closeToastCts = new CancellationTokenSource();
            closeToastCts.Token.Register(() =>
            {
                toastService.CloseToast(toastParameters.Id);
            });
            closeToastCts.CancelAfter(DashboardUIHelpers.ToastTimeout);

            response = await dashboardClient.ExecuteResourceCommandAsync(resource.Name, resource.ResourceType, command, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            toastService.OnClose -= closeCallback;
        }

        // Update toast with the result;
        if (response.Kind == ResourceCommandResponseKind.Succeeded)
        {
            toastParameters.Title = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandSuccess)], messageResourceName, command.GetDisplayName());
            toastParameters.Intent = ToastIntent.Success;
            toastParameters.Icon = GetIntentIcon(ToastIntent.Success);
        }
        else if (response.Kind == ResourceCommandResponseKind.Cancelled)
        {
            // For cancelled commands, just close the existing toast and don't show any success or error message
            if (!toastClosed)
            {
                toastService.CloseToast(toastParameters.Id);
            }
            return;
        }
        else
        {
            toastParameters.Title = string.Format(CultureInfo.InvariantCulture, loc[nameof(Dashboard.Resources.Resources.ResourceCommandFailed)], messageResourceName, command.GetDisplayName());
            toastParameters.Intent = ToastIntent.Error;
            toastParameters.Icon = GetIntentIcon(ToastIntent.Error);
            toastParameters.Content.Details = response.ErrorMessage;
            toastParameters.PrimaryAction = loc[nameof(Dashboard.Resources.Resources.ResourceCommandToastViewLogs)];
            toastParameters.OnPrimaryAction = EventCallback.Factory.Create<ToastResult>(this, () => navigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: getResourceName(resource))));
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
}
