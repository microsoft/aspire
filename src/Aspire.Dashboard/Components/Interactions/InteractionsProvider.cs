// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using MessageIntentDto = Aspire.DashboardService.Proto.V1.MessageIntent;
using MessageIntentUI = Microsoft.FluentUI.AspNetCore.Components.MessageBarIntent;

namespace Aspire.Dashboard.Components.Interactions;

public class InteractionsProvider : ComponentBase, IAsyncDisposable
{
    internal record InteractionMessageBarReference(int InteractionId, DashboardMessageBarReference Message, ComponentTelemetryContext TelemetryContext) : IDisposable
    {
        public void Dispose()
        {
            TelemetryContext.Dispose();
        }
    }
    internal record InteractionDialogReference(int InteractionId, DashboardDialogReference Dialog, ComponentTelemetryContext TelemetryContext) : IDisposable
    {
        public bool CompletedByServer { get; set; }

        public void Dispose()
        {
            TelemetryContext.Dispose();
        }
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly KeyedInteractionCollection _pendingInteractions = new();
    private readonly KeyedMessageCollection _openMessageBars = new();
    private MarkdownProcessor _markdownProcessor = default!;
    private Task? _dialogDisplayTask;
    private Task? _watchInteractionsTask;
    private TaskCompletionSource _interactionAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _messagesProcessed;

    // Internal for testing.
    internal bool? _enabled;
    internal InteractionDialogReference? _interactionDialogReference;
    internal IEnumerable<InteractionMessageBarReference> OpenMessageBars => _openMessageBars;
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

    [Inject(Key = ServiceClient.DashboardClient.LiveAppHostServiceKey)]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required DashboardMessageBarService MessageService { get; init; }

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
        // Exit quickly if the dashboard client is not enabled. For example, the dashboard is running in the standalone container.
        if (!DashboardClient.IsEnabled)
        {
            Logger.LogDebug("InteractionProvider is disabled because the DashboardClient is not enabled.");
            _enabled = false;
            return;
        }
        else
        {
            _enabled = true;
        }

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

    private async Task InteractionsDisplayAsync()
    {
        var waitForInteractionAvailableTask = Task.CompletedTask;

        while (!_cts.IsCancellationRequested)
        {
            // If there are no pending interactions then wait on this task to get notified when one is added.
            await waitForInteractionAvailableTask.WaitAsync(_cts.Token).ConfigureAwait(false);

            DashboardDialogReference? currentDialogReference = null;

            await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (_pendingInteractions.Count == 0)
                {
                    // Task is set when a new interaction is added.
                    // Continue here will exit the async lock and wait for the task to complete.
                    waitForInteractionAvailableTask = _interactionAvailableTcs.Task;
                    continue;
                }

                waitForInteractionAvailableTask = Task.CompletedTask;
                var item = ((IList<WatchInteractionsResponseUpdate>)_pendingInteractions)[0];
                _pendingInteractions.RemoveAt(0);

                Func<DashboardDialogService, Task<DashboardDialogReference>> openDialog;
                string dialogComponentId;

                if (item.MessageBox is { } messageBox)
                {
                    var dialogParameters = CreateDialogParameters(item, messageBox.Intent);
                    dialogParameters.Title = WebUtility.HtmlEncode(item.Title);
                    dialogParameters.OnDialogResult = EventCallback.Factory.Create<DialogResult>(this, async dialogResult =>
                    {
                        var request = new WatchInteractionsRequestUpdate
                        {
                            InteractionId = item.InteractionId
                        };

                        if (dialogResult.Cancelled)
                        {
                            // There will be data in the dialog result on cancel if the secondary button is clicked.
                            if (dialogResult.Value != null)
                            {
                                messageBox.Result = false;
                                request.MessageBox = messageBox;
                            }
                            else
                            {
                                request.Complete = new InteractionComplete();
                            }
                        }
                        else
                        {
                            messageBox.Result = true;
                            request.MessageBox = messageBox;
                        }

                        await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
                    });

                    var content = new InteractionMessageBoxContent
                    {
                        MarkupMessage = GetMessageHtml(item),
                        Intent = messageBox.Intent
                    };

                    dialogComponentId = TelemetryComponentIds.InteractionMessageBox;
                    openDialog = dialogService => dialogService.ShowDialogAsync<InteractionMessageBoxDialog>(content, dialogParameters);
                }
                else if (item.InputsDialog is not null)
                {
                    var vm = new InteractionsInputsDialogViewModel
                    {
                        Interaction = item,
                        Message = GetMessageHtml(item),
                        DashboardClient = DashboardClient,
                        OnSubmitCallback = async (savedInteraction, update) =>
                        {
                            var request = new WatchInteractionsRequestUpdate
                            {
                                InteractionId = savedInteraction.InteractionId,
                                InputsDialog = savedInteraction.InputsDialog,
                                ResponseUpdate = update
                            };

                            await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
                        }
                    };

                    var dialogParameters = CreateDialogParameters(item, intent: null);
                    dialogParameters.Id = "interactions-input-dialog";
                    dialogParameters.Width = "min(650px, 75vw)";
                    // A non-cancelled result means the form submission already sent its request.
                    dialogParameters.OnDialogResult = CreateDialogResultCallback(dialogResult =>
                        dialogResult.Cancelled ? CreateCompletionRequest(item.InteractionId) : null);

                    dialogComponentId = TelemetryComponentIds.InteractionInputsDialog;
                    openDialog = dialogService => dialogService.ShowDialogAsync<InteractionsInputDialog>(vm, dialogParameters);
                }
                else if (item.PromptProgress is not null)
                {
                    var dialogParameters = CreateProgressDialogParameters(item);
                    var vm = new InteractionsProgressDialogViewModel
                    {
                        Message = GetMessageHtml(item)
                    };

                    dialogComponentId = TelemetryComponentIds.InteractionProgressDialog;
                    openDialog = dialogService => dialogService.ShowDialogAsync<InteractionsProgressDialog>(vm, dialogParameters);
                }
                else if (item.PromptTerminal is { } promptTerminal)
                {
                    var dialogParameters = CreateTerminalDialogParameters(item, promptTerminal);
                    var vm = new InteractionsTerminalDialogViewModel
                    {
                        TerminalId = promptTerminal.TerminalId,
                        Message = GetMessageHtml(item)
                    };

                    dialogComponentId = TelemetryComponentIds.InteractionTerminalDialog;
                    openDialog = dialogService => dialogService.ShowDialogAsync<InteractionsTerminalDialog>(vm, dialogParameters);
                }
                else
                {
                    Logger.LogWarning("Unexpected interaction kind: {Kind}", item.KindCase);
                    continue;
                }

                await InvokeAsync(async () =>
                {
                    currentDialogReference = await openDialog(DialogService);
                });

                Debug.Assert(currentDialogReference != null, "Dialog should have been created in UI thread.");
                _interactionDialogReference = new InteractionDialogReference(item.InteractionId, currentDialogReference, CreateTelemetryContext(dialogComponentId));
            }
            finally
            {
                _semaphore.Release();
            }

            try
            {
                if (currentDialogReference != null)
                {
                    await currentDialogReference.Result.WaitAsync(_cts.Token);

                    await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        if (_interactionDialogReference?.Dialog == currentDialogReference)
                        {
                            _interactionDialogReference.Dispose();
                            _interactionDialogReference = null;
                        }
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                }
            }
            catch
            {
                // Ignore any exceptions that occur while waiting for the dialog to close.
            }
        }
    }

    private ComponentTelemetryContext CreateTelemetryContext(string componentId)
    {
        var telemetryContext = new ComponentTelemetryContext(ComponentType.Control, componentId);
        TelemetryContextProvider.Initialize(telemetryContext);
        return telemetryContext;
    }

    private string GetMessageHtml(WatchInteractionsResponseUpdate item)
    {
        if (!item.EnableMessageMarkdown)
        {
            return WebUtility.HtmlEncode(item.Message);
        }

        return InteractionMarkdownHelper.ToHtml(_markdownProcessor, item.Message);
    }

    private async Task WatchInteractionsAsync()
    {
        await DashboardClient.WhenConnected.WaitAsync(_cts.Token).ConfigureAwait(false);

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
                    case WatchInteractionsResponseUpdate.KindOneofCase.PromptProgress:
                    case WatchInteractionsResponseUpdate.KindOneofCase.PromptTerminal:
                        if (_interactionDialogReference != null &&
                            _interactionDialogReference.InteractionId == item.InteractionId)
                        {
                            // Reconnection replays pending interactions. Update the open view instead of queuing
                            // another copy, even for dialogs without mutable content.
                            if (_interactionDialogReference.Dialog.Instance is { } dialogInstance &&
                                dialogInstance.Options.Parameters.TryGetValue("Content", out var content))
                            {
                                if (content is InteractionsInputsDialogViewModel inputsVM)
                                {
                                    await inputsVM.UpdateInteractionAsync(item);
                                }
                                else if (content is InteractionsTerminalDialogViewModel terminalVM)
                                {
                                    await terminalVM.UpdateMessageAsync(GetMessageHtml(item));
                                }
                            }
                        }
                        else
                        {
                            // New or updated interaction.
                            if (_pendingInteractions.Contains(item.InteractionId))
                            {
                                // Update existing interaction at the same place in collection.
                                var existingItem = _pendingInteractions[item.InteractionId];
                                var index = _pendingInteractions.IndexOf(existingItem);
                                _pendingInteractions.RemoveAt(index);
                                _pendingInteractions.Insert(index, item); // Reinsert at the same index to maintain order.
                            }
                            else
                            {
                                _pendingInteractions.Add(item);
                            }

                            NotifyInteractionAvailable();
                        }
                        break;
                    case WatchInteractionsResponseUpdate.KindOneofCase.Notification:
                        var notification = item.Notification;

                        // Check if the message bar is already open for this interaction.
                        // This can happen if the connection is lost and then restored, which will replay pending interactions.
                        if (_openMessageBars.Contains(item.InteractionId))
                        {
                            break;
                        }

                        DashboardMessageBarReference? message = null;
                        await InvokeAsync(async () =>
                        {
                            var primaryButtonText = item.PrimaryButtonText;
                            var secondaryButtonText = item.ShowSecondaryButton ? item.SecondaryButtonText : null;
                            if (notification.Intent == MessageIntentDto.Confirmation)
                            {
                                primaryButtonText = ResolvedPrimaryButtonText(item, notification.Intent);
                                secondaryButtonText = ResolvedSecondaryButtonText(item);
                            }

                            message = await MessageService.ShowAsync(
                                new DashboardMessageBarContent
                                {
                                    Title = item.Title,
                                    Message = GetMessageHtml(item),
                                    UseMarkupString = true,
                                    AllowDismiss = item.ShowDismiss,
                                    LinkText = notification.LinkText,
                                    LinkUrl = notification.LinkUrl,
                                    PrimaryAction = primaryButtonText,
                                    SecondaryAction = secondaryButtonText
                                },
                                MapMessageIntent(notification.Intent),
                                DashboardUIHelpers.MessageBarSection);
                        });

                        Debug.Assert(message != null, "Message should have been created in UI thread.");
                        _openMessageBars.Add(new InteractionMessageBarReference(item.InteractionId, message, CreateTelemetryContext(TelemetryComponentIds.InteractionMessageBar)));

                        // Don't await the result because the watcher must continue processing server updates while the message bar is open.
                        // The observer handles and logs its own exceptions.
                        _ = ObserveNotificationResultAsync(item, notification, message);
                        break;
                    case WatchInteractionsResponseUpdate.KindOneofCase.Complete:
                        // Complete interaction.
                        _pendingInteractions.Remove(item.InteractionId);

                        // Close the interaction's dialog if it is open.
                        if (_interactionDialogReference?.InteractionId == item.InteractionId)
                        {
                            _interactionDialogReference.CompletedByServer = true;
                            try
                            {
                                await InvokeAsync(_interactionDialogReference.Dialog.CloseAsync);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogDebug(ex, "Unexpected error when closing interaction {InteractionId} dialog reference.", item.InteractionId);
                            }
                            finally
                            {
                                _interactionDialogReference.Dispose();
                                _interactionDialogReference = null;
                            }
                        }

                        if (_openMessageBars.TryGetValue(item.InteractionId, out var openMessageBar))
                        {
                            // The presence of the item in the collection is used to decide whether to report completion to the server.
                            // This item is already completed (we're reacting to a completion notification) so remove before close.
                            _openMessageBars.Remove(item.InteractionId);

                            // InvokeAsync not necessary here. It's called internally.
                            await openMessageBar.Message.CloseAsync();
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

    private async Task ObserveNotificationResultAsync(
        WatchInteractionsResponseUpdate interaction,
        InteractionNotification notification,
        DashboardMessageBarReference message)
    {
        try
        {
            var result = await message.Result.ConfigureAwait(false);
            WatchInteractionsRequestUpdate? request = null;

            await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                // The watcher can close a message bar after the server completes the interaction.
                // Only a user-initiated close that still owns the registered reference is sent back.
                if (_openMessageBars.TryGetValue(interaction.InteractionId, out var openMessageBar) &&
                    ReferenceEquals(openMessageBar.Message, message))
                {
                    request = new WatchInteractionsRequestUpdate
                    {
                        InteractionId = interaction.InteractionId
                    };

                    if (result.Data is not bool actionResult)
                    {
                        request.Complete = new InteractionComplete();
                    }
                    else
                    {
                        notification.Result = actionResult;
                        request.Notification = notification;
                    }

                    _openMessageBars.Remove(interaction.InteractionId);
                    openMessageBar.Dispose();
                }
            }
            finally
            {
                _semaphore.Release();
            }

            if (request is not null)
            {
                await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The provider is being disposed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error while completing notification interaction {InteractionId}.", interaction.InteractionId);
        }
    }

    private static MessageIntentUI MapMessageIntent(MessageIntentDto intent)
    {
        return intent switch
        {
            MessageIntentDto.Success => MessageIntentUI.Success,
            MessageIntentDto.Warning => MessageIntentUI.Warning,
            MessageIntentDto.Error => MessageIntentUI.Error,
            MessageIntentDto.Information => MessageIntentUI.Info,
            _ => MessageIntentUI.Info,
        };
    }

    private DialogParameters CreateDialogParameters(WatchInteractionsResponseUpdate interaction, MessageIntentDto? intent)
    {
        var dialogParameters = new DialogParameters
        {
            UseCustomFooter = true,
            ShowDismiss = interaction.ShowDismiss,
            PrimaryAction = ResolvedPrimaryButtonText(interaction, intent),
            SecondaryAction = ResolvedSecondaryButtonText(interaction),
            PreventDismissOnOverlayClick = true,
            Title = interaction.Title
        };

        return dialogParameters;
    }

    private DialogParameters CreateProgressDialogParameters(WatchInteractionsResponseUpdate interaction)
    {
        return CreatePromptDialogParameters(
            interaction,
            width: "500px",
            createResult: () => new WatchInteractionsRequestUpdate
            {
                InteractionId = interaction.InteractionId,
                PromptProgress = new InteractionPromptProgress { Result = false }
            });
    }

    private DialogParameters CreateTerminalDialogParameters(WatchInteractionsResponseUpdate interaction, InteractionPromptTerminal terminal)
    {
        var dialogParameters = CreatePromptDialogParameters(
            interaction,
            width: "75vw",
            createResult: () => new WatchInteractionsRequestUpdate
            {
                InteractionId = interaction.InteractionId,
                PromptTerminal = new InteractionPromptTerminal
                {
                    TerminalId = terminal.TerminalId,
                    Result = false
                }
            });
        dialogParameters.Id = "interactions-terminal-dialog";

        return dialogParameters;
    }

    private DialogParameters CreatePromptDialogParameters(
        WatchInteractionsResponseUpdate interaction,
        string width,
        Func<WatchInteractionsRequestUpdate> createResult)
    {
        var dialogParameters = CreateDialogParameters(interaction, intent: null);
        dialogParameters.Width = width;
        dialogParameters.ShowDismiss = false;
        dialogParameters.SecondaryAction = null;

        // If a primary button text is provided, show it as a cancel button.
        // Otherwise, hide the primary action (the dialog can only be closed from the server side).
        if (string.IsNullOrEmpty(interaction.PrimaryButtonText))
        {
            dialogParameters.PrimaryAction = null;
        }

        dialogParameters.OnDialogResult = CreateDialogResultCallback(
            dialogResult => dialogResult.Cancelled ? CreateCompletionRequest(interaction.InteractionId) : createResult(),
            ignoreResult: () => _cts.IsCancellationRequested ||
                (_interactionDialogReference is { CompletedByServer: true } reference &&
                 reference.InteractionId == interaction.InteractionId));

        return dialogParameters;
    }

    private EventCallback<DialogResult> CreateDialogResultCallback(
        Func<DialogResult, WatchInteractionsRequestUpdate?> createRequest,
        Func<bool>? ignoreResult = null)
    {
        return EventCallback.Factory.Create<DialogResult>(this, async dialogResult =>
        {
            if (ignoreResult?.Invoke() == true)
            {
                return;
            }

            if (createRequest(dialogResult) is { } request)
            {
                await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
            }
        });
    }

    private static WatchInteractionsRequestUpdate CreateCompletionRequest(int interactionId)
    {
        return new WatchInteractionsRequestUpdate
        {
            InteractionId = interactionId,
            Complete = new InteractionComplete()
        };
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

    private void NotifyInteractionAvailable()
    {
        // Let current waiters know that an interaction is available.
        _interactionAvailableTcs.TrySetResult();

        // Reset the task completion source for future waiters.
        _interactionAvailableTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        await TaskHelpers.WaitIgnoreCancelAsync(_dialogDisplayTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_watchInteractionsTask);

        _interactionDialogReference?.Dispose();
        foreach (var messageBar in _openMessageBars)
        {
            messageBar.Dispose();
        }
    }

    private class KeyedInteractionCollection : KeyedCollection<int, WatchInteractionsResponseUpdate>
    {
        protected override int GetKeyForItem(WatchInteractionsResponseUpdate item)
        {
            return item.InteractionId;
        }
    }

    private class KeyedMessageCollection : KeyedCollection<int, InteractionMessageBarReference>
    {
        protected override int GetKeyForItem(InteractionMessageBarReference item)
        {
            return item.InteractionId;
        }
    }
}
