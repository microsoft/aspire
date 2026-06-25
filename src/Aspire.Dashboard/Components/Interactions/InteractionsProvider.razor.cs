// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MessageIntentDto = Aspire.DashboardService.Proto.V1.MessageIntent;

namespace Aspire.Dashboard.Components.Interactions;

/// <summary>
/// Watches AppHost interactions and renders them in the Deck design language: blocking inputs
/// dialogs and message boxes are shown one-at-a-time as a right-side <see cref="InteractionPane"/>,
/// while notifications stack as non-blocking <see cref="NotificationStack"/> toasts.
/// </summary>
/// <remarks>
/// This replaces the previous Fluent <c>DialogService</c>/<c>MessageService</c> rendering. The
/// watch/queue/validation-update/Complete and server request semantics are unchanged — only the
/// rendering surface differs — so dialogs still validate per field via the <c>update</c> action and
/// notification replies still echo <c>Notification{result}</c>.
/// </remarks>
public partial class InteractionsProvider : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly KeyedInteractionCollection _pendingInteractions = new();

    // Notifications are non-blocking; they render as toasts and stay until acted on or dismissed.
    private readonly List<DeckNotification> _notifications = new();
    private readonly Dictionary<int, WatchInteractionsResponseUpdate> _notificationInteractions = new();

    private MarkdownProcessor _markdownProcessor = default!;
    private Task? _dialogDisplayTask;
    private Task? _watchInteractionsTask;
    private TaskCompletionSource _interactionAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _messagesProcessed;

    // The single blocking dialog (inputs dialog or message box) currently shown, plus the mapped
    // input fields and the TCS the display loop awaits until the dialog closes.
    private WatchInteractionsResponseUpdate? _currentDialog;
    private string? _currentDialogMessage;
    private List<DeckInteractionInput> _currentDialogInputs = new();
    private TaskCompletionSource? _dialogClosedTcs;
    private ComponentTelemetryContext? _currentDialogTelemetry;

    // Internal for testing.
    internal bool? _enabled;
    internal WatchInteractionsResponseUpdate? CurrentDialog => _currentDialog;
    internal string? CurrentDialogMessage => _currentDialogMessage;
    internal IReadOnlyList<DeckNotification> OpenNotifications => _notifications;
    internal async Task<int> GetMessagesProcessedAsync()
    {
        await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            return _messagesProcessed;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required ILogger<InteractionsProvider> Logger { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    protected override void OnInitialized()
    {
        // Exit quickly if the dashboard client is not enabled (e.g. standalone container).
        if (!DashboardClient.IsEnabled)
        {
            Logger.LogDebug("InteractionProvider is disabled because the DashboardClient is not enabled.");
            _enabled = false;
            return;
        }

        _enabled = true;
        _markdownProcessor = InteractionMarkdownHelper.CreateProcessor(ControlsStringsLoc);

        _dialogDisplayTask = Task.Run(async () =>
        {
            try
            {
                await InteractionsDisplayAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Logger.LogError(ex, "Unexpected error while displaying interaction dialogs.");
            }
        });

        _watchInteractionsTask = Task.Run(async () =>
        {
            try
            {
                await WatchInteractionsAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Logger.LogError(ex, "Unexpected error while watching interactions.");
            }
        });
    }

    // Dequeues blocking interactions and shows them one at a time. Each iteration sets the current
    // dialog, re-renders, then waits on a TCS that completes when the user acts or the server
    // completes the interaction.
    private async Task InteractionsDisplayAsync()
    {
        var waitForInteractionAvailableTask = Task.CompletedTask;

        while (!_cts.IsCancellationRequested)
        {
            await waitForInteractionAvailableTask.WaitAsync(_cts.Token).ConfigureAwait(false);

            TaskCompletionSource? closedTcs = null;

            await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (_pendingInteractions.Count == 0)
                {
                    waitForInteractionAvailableTask = _interactionAvailableTcs.Task;
                    continue;
                }

                waitForInteractionAvailableTask = Task.CompletedTask;
                var item = ((IList<WatchInteractionsResponseUpdate>)_pendingInteractions)[0];
                _pendingInteractions.RemoveAt(0);

                if (item.KindCase is not (WatchInteractionsResponseUpdate.KindOneofCase.MessageBox or WatchInteractionsResponseUpdate.KindOneofCase.InputsDialog))
                {
                    Logger.LogWarning("Unexpected interaction kind in dialog queue: {Kind}", item.KindCase);
                    continue;
                }

                closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _dialogClosedTcs = closedTcs;

                var componentId = item.MessageBox is not null
                    ? TelemetryComponentIds.InteractionMessageBox
                    : TelemetryComponentIds.InteractionInputsDialog;

                await InvokeAsync(() =>
                {
                    SetCurrentDialog(item);
                    _currentDialogTelemetry = CreateTelemetryContext(componentId);
                    StateHasChanged();
                });
            }
            finally
            {
                _semaphore.Release();
            }

            // Wait until the dialog is closed (user acted, or the server completed the interaction).
            if (closedTcs is not null)
            {
                try
                {
                    await closedTcs.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Provider disposed while a dialog was open.
                }

                await InvokeAsync(() =>
                {
                    ClearCurrentDialog();
                    StateHasChanged();
                });
            }
        }
    }

    private void SetCurrentDialog(WatchInteractionsResponseUpdate item)
    {
        _currentDialog = item;
        _currentDialogMessage = GetMessageHtml(item);
        _currentDialogInputs = item.InputsDialog is { } inputs
            ? inputs.InputItems.Select(MapInput).ToList()
            : new List<DeckInteractionInput>();
    }

    private void ClearCurrentDialog()
    {
        _currentDialog = null;
        _currentDialogMessage = null;
        _currentDialogInputs = new();
        _dialogClosedTcs = null;
        _currentDialogTelemetry?.Dispose();
        _currentDialogTelemetry = null;
    }

    private async Task WatchInteractionsAsync()
    {
        var interactions = DashboardClient.SubscribeInteractionsAsync(_cts.Token);
        await foreach (var item in interactions)
        {
            await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                switch (item.KindCase)
                {
                    case WatchInteractionsResponseUpdate.KindOneofCase.MessageBox:
                    case WatchInteractionsResponseUpdate.KindOneofCase.InputsDialog:
                        if (_currentDialog is { } current && current.InteractionId == item.InteractionId)
                        {
                            // The dialog is already open for this interaction; update it in place
                            // (e.g. new validation errors). The InteractionPane keeps the user's typed
                            // values because the interaction id is unchanged.
                            await InvokeAsync(() =>
                            {
                                SetCurrentDialog(item);
                                StateHasChanged();
                            });
                        }
                        else if (_pendingInteractions.Contains(item.InteractionId))
                        {
                            // Update queued interaction in place to keep ordering.
                            var existingItem = _pendingInteractions[item.InteractionId];
                            var index = _pendingInteractions.IndexOf(existingItem);
                            _pendingInteractions.RemoveAt(index);
                            _pendingInteractions.Insert(index, item);
                        }
                        else
                        {
                            _pendingInteractions.Add(item);
                            NotifyInteractionAvailable();
                        }
                        break;

                    case WatchInteractionsResponseUpdate.KindOneofCase.Notification:
                        // Dedup replayed notifications (can happen when the connection is restored).
                        if (_notificationInteractions.ContainsKey(item.InteractionId))
                        {
                            break;
                        }

                        _notificationInteractions[item.InteractionId] = item;
                        var model = MapNotification(item);
                        await InvokeAsync(() =>
                        {
                            _notifications.Add(model);
                            StateHasChanged();
                        });
                        break;

                    case WatchInteractionsResponseUpdate.KindOneofCase.Complete:
                        _pendingInteractions.Remove(item.InteractionId);

                        // Close the dialog if it is open for this interaction.
                        if (_currentDialog?.InteractionId == item.InteractionId)
                        {
                            _dialogClosedTcs?.TrySetResult();
                        }

                        // Remove the notification if it is open for this interaction.
                        if (_notificationInteractions.Remove(item.InteractionId))
                        {
                            await InvokeAsync(() =>
                            {
                                _notifications.RemoveAll(n => n.InteractionId == item.InteractionId);
                                StateHasChanged();
                            });
                        }
                        break;

                    default:
                        Logger.LogWarning("Unexpected interaction kind: {Kind}", item.KindCase);
                        break;
                }

                _messagesProcessed++;
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    // --- Dialog responses ---------------------------------------------------

    // Inputs-dialog submit: write the user's values back into the proto and send with
    // ResponseUpdate=false. Don't close locally — the server completes the interaction on success
    // (which closes the dialog) or sends an update with validation errors (which keeps it open).
    // Internal for testing.
    internal Task OnDialogSubmitAsync(IReadOnlyDictionary<string, string> values)
        => SendInputsResponseAsync(values, responseUpdate: false);

    // Live validation: a field with UpdateStateOnChange changed. Send with ResponseUpdate=true so the
    // server re-validates without completing.
    private Task OnDialogUpdateAsync(IReadOnlyDictionary<string, string> values)
        => SendInputsResponseAsync(values, responseUpdate: true);

    private async Task SendInputsResponseAsync(IReadOnlyDictionary<string, string> values, bool responseUpdate)
    {
        if (_currentDialog is not { InputsDialog: { } inputsDialog } interaction)
        {
            return;
        }

        foreach (var input in inputsDialog.InputItems)
        {
            if (values.TryGetValue(input.Name, out var value))
            {
                input.Value = value;
            }
        }

        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId,
            InputsDialog = inputsDialog,
            ResponseUpdate = responseUpdate
        };

        await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
    }

    // Cancel/dismiss of a dialog (inputs dialog or message box dismiss): report completion and close.
    // Internal for testing.
    internal async Task OnDialogCancelAsync()
    {
        if (_currentDialog is not { } interaction)
        {
            return;
        }

        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId,
            Complete = new InteractionComplete()
        };

        await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
        _dialogClosedTcs?.TrySetResult();
    }

    // Internal for testing.
    internal Task OnMessageBoxPrimaryAsync() => SendMessageBoxResultAsync(result: true);

    private Task OnMessageBoxSecondaryAsync() => SendMessageBoxResultAsync(result: false);

    private async Task SendMessageBoxResultAsync(bool result)
    {
        if (_currentDialog is not { MessageBox: { } messageBox } interaction)
        {
            return;
        }

        messageBox.Result = result;
        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId,
            MessageBox = messageBox
        };

        await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
        _dialogClosedTcs?.TrySetResult();
    }

    // --- Notification responses ---------------------------------------------

    private Task OnNotificationPrimaryAsync(int interactionId) => SendNotificationResultAsync(interactionId, result: true);

    private Task OnNotificationSecondaryAsync(int interactionId) => SendNotificationResultAsync(interactionId, result: false);

    private async Task SendNotificationResultAsync(int interactionId, bool result)
    {
        if (!_notificationInteractions.TryGetValue(interactionId, out var item) || item.Notification is not { } notification)
        {
            return;
        }

        // Notification replies echo Notification{result} (not a message box). The server then drives
        // the next step, e.g. a "parameters required" primary action surfaces the inputs dialog.
        notification.Result = result;
        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interactionId,
            Notification = notification
        };

        await SendAndRemoveNotificationAsync(interactionId, request);
    }

    private async Task OnNotificationDismissAsync(int interactionId)
    {
        if (!_notificationInteractions.ContainsKey(interactionId))
        {
            return;
        }

        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interactionId,
            Complete = new InteractionComplete()
        };

        await SendAndRemoveNotificationAsync(interactionId, request);
    }

    private async Task SendAndRemoveNotificationAsync(int interactionId, WatchInteractionsRequestUpdate request)
    {
        _notificationInteractions.Remove(interactionId);
        _notifications.RemoveAll(n => n.InteractionId == interactionId);
        StateHasChanged();

        await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
    }

    // --- Mapping ------------------------------------------------------------

    private DeckNotification MapNotification(WatchInteractionsResponseUpdate item)
    {
        var notification = item.Notification;
        var tone = MapTone(notification.Intent);

        var primaryText = item.PrimaryButtonText;
        var secondaryText = item.ShowSecondaryButton ? item.SecondaryButtonText : null;
        if (notification.Intent == MessageIntentDto.Confirmation)
        {
            primaryText = ResolvedPrimaryButtonText(item, notification.Intent);
            secondaryText = ResolvedSecondaryButtonText(item);
        }

        return new DeckNotification(
            item.InteractionId,
            item.Title,
            new MarkupString(GetMessageHtml(item)),
            tone,
            primaryText,
            secondaryText,
            item.ShowSecondaryButton,
            item.ShowDismiss,
            notification.LinkText,
            notification.LinkUrl);
    }

    private static DeckInteractionInput MapInput(InteractionInput input)
    {
        var options = input.InputType == InputType.Choice && input.Options != null
            ? input.Options.Select(o => (o.Key, o.Value)).ToList()
            : new List<(string, string)>();

        return new DeckInteractionInput(
            input.Name,
            input.Label,
            input.Placeholder,
            MapInputType(input.InputType),
            input.Required,
            options,
            input.Value ?? string.Empty,
            input.ValidationErrors.ToList(),
            input.Description,
            input.MaxLength,
            input.AllowCustomChoice,
            input.Disabled || input.Loading,
            input.UpdateStateOnChange);
    }

    private static string MapInputType(InputType inputType) => inputType switch
    {
        InputType.SecretText => "secretText",
        InputType.Choice => "choice",
        InputType.Boolean => "boolean",
        InputType.Number => "number",
        _ => "text"
    };

    // Maps an Aspire message intent to a Deck tone class. "Confirmation" and "None" fall back to the
    // neutral information style, matching Deck.
    private static string MapTone(MessageIntentDto intent) => intent switch
    {
        MessageIntentDto.Success => "success",
        MessageIntentDto.Warning => "warning",
        MessageIntentDto.Error => "error",
        _ => "info"
    };

    private static MessageIntentDto? GetDialogIntent(WatchInteractionsResponseUpdate item)
        => item.MessageBox is { } messageBox ? messageBox.Intent : null;

    private string GetMessageHtml(WatchInteractionsResponseUpdate item)
    {
        if (!item.EnableMessageMarkdown)
        {
            return WebUtility.HtmlEncode(item.Message);
        }

        return InteractionMarkdownHelper.ToHtml(_markdownProcessor, item.Message);
    }

    private string ResolvedPrimaryButtonText(WatchInteractionsResponseUpdate interaction, MessageIntentDto? intent)
    {
        if (interaction.PrimaryButtonText is { Length: > 0 } primaryText)
        {
            return primaryText;
        }
        if (intent == MessageIntentDto.Error)
        {
            return Loc[nameof(Resources.Dialogs.InteractionButtonClose)];
        }

        return Loc[nameof(Resources.Dialogs.InteractionButtonOk)];
    }

    private string ResolvedSecondaryButtonText(WatchInteractionsResponseUpdate interaction)
    {
        if (!interaction.ShowSecondaryButton)
        {
            return string.Empty;
        }

        return interaction.SecondaryButtonText is { Length: > 0 } secondaryText
            ? secondaryText
            : Loc[nameof(Resources.Dialogs.InteractionButtonCancel)];
    }

    private ComponentTelemetryContext CreateTelemetryContext(string componentId)
    {
        var context = new ComponentTelemetryContext(ComponentType.Control, componentId);
        TelemetryContextProvider.Initialize(context);
        return context;
    }

    private void NotifyInteractionAvailable()
    {
        _interactionAvailableTcs.TrySetResult();
        _interactionAvailableTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _currentDialogTelemetry?.Dispose();

        await TaskHelpers.WaitIgnoreCancelAsync(_dialogDisplayTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_watchInteractionsTask);
    }

    private sealed class KeyedInteractionCollection : System.Collections.ObjectModel.KeyedCollection<int, WatchInteractionsResponseUpdate>
    {
        protected override int GetKeyForItem(WatchInteractionsResponseUpdate item)
        {
            return item.InteractionId;
        }
    }
}
