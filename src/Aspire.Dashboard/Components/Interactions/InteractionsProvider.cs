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
using Color = Microsoft.FluentUI.AspNetCore.Components.Color;
using MessageIntentDto = Aspire.DashboardService.Proto.V1.MessageIntent;
using MessageIntentUI = Microsoft.FluentUI.AspNetCore.Components.MessageIntent;

namespace Aspire.Dashboard.Components.Interactions;

public class InteractionsProvider : ComponentBase, IAsyncDisposable
{
    internal record InteractionMessageBarReference(int InteractionId, Message Message, ComponentTelemetryContext TelemetryContext) : IDisposable
    {
        public void Dispose()
        {
            TelemetryContext.Dispose();
        }
    }
    internal record InteractionDialogReference(int InteractionId, IDialogReference Dialog, ComponentTelemetryContext TelemetryContext) : IDisposable
    {
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

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required IMessageService MessageService { get; init; }

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

            IDialogReference? currentDialogReference = null;

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

                Func<DashboardDialogService, Task<IDialogReference>> openDialog;
                string dialogComponentId;

                if (item.MessageBox is { } messageBox)
                {
                    var dialogParameters = CreateDialogParameters(item, messageBox.Intent);
                    dialogParameters.OnDialogResult = EventCallback.Factory.Create<DialogResult>(this, async dialogResult =>
                    {
                        var request = new WatchInteractionsRequestUpdate
                        {
                            InteractionId = item.InteractionId
                        };

                        if (dialogResult.Cancelled)
                        {
                            // There will be data in the dialog result on cancel if the secondary button is clicked.
                            if (dialogResult.Data != null)
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

                    var content = new MessageBoxContent
                    {
                        Title = item.Title,
                        MarkupMessage = new MarkupString(GetMessageHtml(item)),
                    };
                    switch (messageBox.Intent)
                    {
                        case MessageIntentDto.None:
                            content.Icon = null;
                            break;
                        case MessageIntentDto.Success:
                            content.IconColor = Color.Success;
                            content.Icon = new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.CheckmarkCircle();
                            break;
                        case MessageIntentDto.Warning:
                            content.IconColor = Color.Warning;
                            content.Icon = new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.Warning();
                            break;
                        case MessageIntentDto.Error:
                            content.IconColor = Color.Error;
                            content.Icon = new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.DismissCircle();
                            break;
                        case MessageIntentDto.Information:
                            content.IconColor = Color.Info;
                            content.Icon = new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.Info();
                            break;
                        case MessageIntentDto.Confirmation:
                            content.IconColor = Color.Success;
                            content.Icon = new Microsoft.FluentUI.AspNetCore.Components.Icons.Filled.Size24.QuestionCircle();
                            break;
                    }

                    dialogComponentId = TelemetryComponentIds.InteractionMessageBox;
                    openDialog = dialogService => ShowMessageBoxAsync(dialogService, content, dialogParameters);
                }
                else if (item.InputsDialog is { } inputs)
                {
                    var vm = new InteractionsInputsDialogViewModel
                    {
                        Interaction = item,
                        Message = GetMessageHtml(item),
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

                    var width = ViewportInformation.IsDesktop ? "75vw" : "100vw";

                    var dialogParameters = CreateDialogParameters(item, intent: null);
                    dialogParameters.Id = "interactions-input-dialog";
                    dialogParameters.Width = $"min(650px, {width})";
                    dialogParameters.OnDialogResult = EventCallback.Factory.Create<DialogResult>(this, async dialogResult =>
                    {
                        // Only send notification of completion if the dialog was cancelled.
                        // A non-cancelled dialog result means the user submitted the form and we already sent the request.
                        if (dialogResult.Cancelled)
                        {
                            var request = new WatchInteractionsRequestUpdate
                            {
                                InteractionId = item.InteractionId,
                                Complete = new InteractionComplete()
                            };

                            await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
                        }
                    });

                    dialogComponentId = TelemetryComponentIds.InteractionInputsDialog;
                    openDialog = dialogService => dialogService.ShowDialogAsync<InteractionsInputDialog>(vm, dialogParameters);
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
                        if (_interactionDialogReference != null &&
                            _interactionDialogReference.InteractionId == item.InteractionId &&
                            _interactionDialogReference.Dialog.Instance.Content is InteractionsInputsDialogViewModel inputsVM)
                        {
                            // If the dialog is already open for this interaction, update it with the new data.
                            await inputsVM.UpdateInteractionAsync(item);
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

                        Message? message = null;
                        await InvokeAsync(async () =>
                        {
                            message = await MessageService.ShowMessageBarAsync(options =>
                            {
                                options.Title = WebUtility.HtmlEncode(item.Title);
                                options.Body = GetMessageHtml(item);
                                // Allow for HTML in title and body. This is needed to support Markdown output.
                                // It's safe to enable because content is always either HTML-encoded or generated from Markdown which is sanitized.
                                options.UseMarkupString = true;

                                options.Intent = MapMessageIntent(notification.Intent);
                                options.Section = DashboardUIHelpers.MessageBarSection;
                                options.AllowDismiss = item.ShowDismiss;
                                if (!string.IsNullOrEmpty(notification.LinkText))
                                {
                                    options.Link = new()
                                    {
                                        Text = notification.LinkText,
                                        Href = notification.LinkUrl
                                    };
                                }

                                var primaryButtonText = item.PrimaryButtonText;
                                var secondaryButtonText = item.ShowSecondaryButton ? item.SecondaryButtonText : null;
                                if (notification.Intent == MessageIntentDto.Confirmation)
                                {
                                    primaryButtonText = ResolvedPrimaryButtonText(item, notification.Intent);
                                    secondaryButtonText = ResolvedSecondaryButtonText(item);
                                }

                                bool? result = null;

                                if (!string.IsNullOrEmpty(primaryButtonText))
                                {
                                    options.PrimaryAction = new ActionButton<Message>
                                    {
                                        Text = primaryButtonText,
                                        OnClick = m =>
                                        {
                                            result = true;
                                            m.Close();
                                            return Task.CompletedTask;
                                        }
                                    };
                                }
                                if (item.ShowSecondaryButton && !string.IsNullOrEmpty(secondaryButtonText))
                                {
                                    options.SecondaryAction = new ActionButton<Message>
                                    {
                                        Text = secondaryButtonText,
                                        OnClick = m =>
                                        {
                                            result = false;
                                            m.Close();
                                            return Task.CompletedTask;
                                        }
                                    };
                                }

                                options.OnClose = async m =>
                                {
                                    // Only send complete notification if in the open message bars list.
                                    if (_openMessageBars.TryGetValue(item.InteractionId, out var openMessageBar))
                                    {
                                        var request = new WatchInteractionsRequestUpdate
                                        {
                                            InteractionId = item.InteractionId
                                        };

                                        if (result == null)
                                        {
                                            request.Complete = new InteractionComplete();
                                        }
                                        else
                                        {
                                            notification.Result = result.Value;
                                            request.Notification = notification;
                                        }

                                        _openMessageBars.Remove(item.InteractionId);
                                        openMessageBar.Dispose();

                                        await DashboardClient.SendInteractionRequestAsync(request, _cts.Token).ConfigureAwait(false);
                                    }
                                };
                            });
                        });

                        Debug.Assert(message != null, "Message should have been created in UI thread.");
                        _openMessageBars.Add(new InteractionMessageBarReference(item.InteractionId, message, CreateTelemetryContext(TelemetryComponentIds.InteractionMessageBar)));
                        break;
                    case WatchInteractionsResponseUpdate.KindOneofCase.Complete:
                        // Complete interaction.
                        _pendingInteractions.Remove(item.InteractionId);

                        // Close the interaction's dialog if it is open.
                        if (_interactionDialogReference?.InteractionId == item.InteractionId)
                        {
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
                            openMessageBar.Message.Close();
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
            ShowDismiss = interaction.ShowDismiss,
            PrimaryAction = ResolvedPrimaryButtonText(interaction, intent),
            SecondaryAction = ResolvedSecondaryButtonText(interaction),
            PreventDismissOnOverlayClick = true,
            Title = interaction.Title
        };

        return dialogParameters;
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

    public async Task<IDialogReference> ShowMessageBoxAsync(DashboardDialogService dialogService, MessageBoxContent content, DialogParameters parameters)
    {
        var dialogParameters = new DialogParameters<MessageBoxContent>
        {
            Content = content,
            DialogType = DialogType.MessageBox,
            Alignment = HorizontalAlignment.Center,
            Title = content.Title,
            ShowDismiss = parameters.ShowDismiss,
            PrimaryAction = parameters.PrimaryAction,
            SecondaryAction = parameters.SecondaryAction,
            Width = parameters.Width,
            Height = parameters.Height,
            AriaLabel = (content.Title ?? ""),
            OnDialogResult = parameters.OnDialogResult,
            PreventDismissOnOverlayClick = true
        };
        return await dialogService.ShowMessageBoxAsync(dialogParameters).ConfigureAwait(false);
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
