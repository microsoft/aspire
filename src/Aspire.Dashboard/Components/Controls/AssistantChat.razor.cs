// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public sealed partial class AssistantChat : ComponentBase, IAsyncDisposable
{
    [Parameter]
    public required AssistantChatViewModel ChatViewModel { get; set; }

    [Parameter]
    public required string Class { get; set; }

    [Parameter]
    public required EventCallback ModelInitialized { get; set; }

    [Inject]
    public required ILogger<AssistantChat> Logger { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IStringLocalizer<AIAssistant> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    private readonly CancellationTokenSource _cts = new();
    private ElementReference? _chatMessageTextBox;
    private ScrollInstruction _scrollInstruction;
    private IJSObjectReference? _module;
    private AssistantChatViewModel? _chatViewModel;
    private MenuButtonItem? _selectedModelItem;
    private readonly List<MenuButtonItem> _modelMenuItems = new()!;
    private string? _currentChatMessageId;
    private bool _currentChatMessageChanged;
    private bool _currentChatMessageComplete;
    private bool _initializedChatScript;
    private AssistantChatDisplayState _renderDisplayState;

    private enum ScrollInstruction
    {
        None,
        ScrollImmediately
    }

    private MenuButtonItem CreateItem(ModelViewModel model)
    {
        MenuButtonItem menuButtonItem = null!;

        menuButtonItem = new MenuButtonItem
        {
            Text = model.DisplayName,
            OnClick = async () =>
            {
                _selectedModelItem = menuButtonItem;
                ChatViewModel.SetSelectedModel(model);

                CreateModelMenuItems();
                StateHasChanged();

                await ChatViewModel.UpdateSettingsAsync();
                if (_chatMessageTextBox is { } textbox)
                {
                    await textbox.FocusAsync();
                }
            }
        };

        return menuButtonItem;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ChatViewModel != _chatViewModel)
        {
            if (_chatViewModel != null)
            {
                // The view model for the component has changed. Disconnect and dispose the old model.
                await _chatViewModel.DisconnectAsync();
                _chatViewModel.Dispose();
                _initializedChatScript = false;
            }

            _chatViewModel = ChatViewModel;

            ChatViewModel.OnToolInvokedCallback = SetStatusAsync;
            ChatViewModel.OnConversationChangedCallback = ConversationChangedAsync;
            ChatViewModel.OnContextChangedCallback = ContextChangedAsync;
            ChatViewModel.OnInitializeCallback = InitializedAsync;

            CreateModelMenuItems();

            await ChatViewModel.ComponentInitialize();
        }
    }

    private void CreateModelMenuItems()
    {
        _modelMenuItems.Clear();
        foreach (var model in ChatViewModel.Models)
        {
            _modelMenuItems.Add(CreateItem(model));
        }
        _selectedModelItem = _modelMenuItems.FirstOrDefault(i => i.Text == ChatViewModel.SelectedModel?.DisplayName) ?? _modelMenuItems.LastOrDefault();
        _selectedModelItem?.Icon = DeckIconName.Checkmark;
    }

    private async Task ScrollToEndOfMessagesAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        if (_module != null)
        {
            var scrollOptions = new
            {
                containerId = AssistantChatViewModel.ChatAssistantContainerId
            };
            await _module.InvokeVoidAsync("scrollToBottomChatAssistant", _cts.Token, scrollOptions);
            _scrollInstruction = ScrollInstruction.None;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Controls/AssistantChat.razor.js");
        }

        if (_module != null)
        {
            // Use the display state captured during render to make sure the value changing during render has no impact.
            if (_renderDisplayState == AssistantChatDisplayState.Chat && !_initializedChatScript)
            {
                _initializedChatScript = true;

                // Attach an event for listening to key press in the browser to submit a message on enter.
                // A JavaScript event is better than using @onkeypress directly in Blazor:
                // - Wait until enter before making a server call.
                // - Suppress adding new line to the textarea value.
                var initializeOptions = new
                {
                    containerId = AssistantChatViewModel.ChatAssistantContainerId,
                    scrollBottomButtonId = "chat-scroll-bottom-button",
                    textareaId = "chat-message",
                    formId = "chat-form"
                };
                await _module.InvokeVoidAsync("initializeAssistantChat", initializeOptions);
            }

            if (_currentChatMessageChanged)
            {
                _currentChatMessageChanged = false;

                var initializeOptions = new
                {
                    containerId = AssistantChatViewModel.ChatAssistantContainerId,
                    chatMessageId = _currentChatMessageId
                };
                await _module.InvokeVoidAsync("initializeCurrentMessage", initializeOptions);
            }

            if (_currentChatMessageComplete)
            {
                _currentChatMessageComplete = false;

                var initializeOptions = new
                {
                    containerId = AssistantChatViewModel.ChatAssistantContainerId,
                    chatMessageId = _currentChatMessageId
                };
                await _module.InvokeVoidAsync("completeCurrentMessage", initializeOptions);
            }
        }

        if (_scrollInstruction == ScrollInstruction.ScrollImmediately)
        {
            await ScrollToEndOfMessagesAsync();
        }
    }

    private DeckIconName GetSubmitIcon()
    {
        if (ChatViewModel.ResponseInProgress)
        {
            return DeckIconName.Stop;
        }
        else
        {
            return DeckIconName.Send;
        }
    }

    private bool IsSubmitEnabled()
    {
        if (ChatViewModel.ResponseInProgress)
        {
            // Enabled so the user can cancel the response.
            return true;
        }
        else
        {
            // Enabled if the user has entered a message.
            return !string.IsNullOrEmpty(ChatViewModel.UserMessage);
        }
    }

    private async Task HandleSubmit()
    {
        if (!ChatViewModel.ResponseInProgress)
        {
            if (!string.IsNullOrWhiteSpace(ChatViewModel.UserMessage))
            {
                var message = ChatViewModel.UserMessage.Trim();
                await AddPrompt(message, message, fromFollowUpPrompt: false);
            }
        }
        else
        {
            ChatViewModel.CancelResponseInProgress();
        }
    }

    private async Task InitializedAsync(CancellationToken token)
    {
        CreateModelMenuItems();

        await InvokeAsync(async () =>
        {
            if (ModelInitialized.HasDelegate)
            {
                await ModelInitialized.InvokeAsync(null);
            }
        });
    }

    private async Task ContextChangedAsync(CancellationToken token)
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task ConversationChangedAsync(ChatViewModel? chatViewModel, ResponseState responseState, CancellationToken token)
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (responseState == ResponseState.Starting)
                {
                    // Scroll to the end. This places the new message at the top of the chat window.
                    _scrollInstruction = ScrollInstruction.ScrollImmediately;
                }

                if (chatViewModel != null && _currentChatMessageId != chatViewModel.ElementId && !chatViewModel.IsComplete)
                {
                    _currentChatMessageId = chatViewModel.ElementId;
                    _currentChatMessageChanged = true;
                }

                if (responseState == ResponseState.AddedPlaceHolders)
                {
                    _currentChatMessageComplete = true;
                }

                StateHasChanged();
            });
        }
        catch (Exception ex) when (_cts.Token.IsCancellationRequested)
        {
            // Ignore errors after cancellation.
            Logger.LogTrace(ex, "Error updating UI after cancellation.");
        }
    }

    private async Task SetStatusAsync(string toolName, string statusText, CancellationToken cancellationToken)
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnInitialPromptAsync(InitialPrompt item)
    {
        // Ensure prompt is started successfully and isn't interfered with by FocusAsync by starting prompt first.
        var task = ChatViewModel.InitialPromptSelectedAsync(item);

        if (_chatMessageTextBox is { } textbox)
        {
            await textbox.FocusAsync();
        }

        await task;
    }

    private async Task OnNextStepItemAsync(FollowUpPromptViewModel item)
    {
        // Ensure prompt is started successfully and isn't interfered with by FocusAsync by starting prompt first.
        var followUpPromptTask = AddPrompt(item.Text, item.Text, fromFollowUpPrompt: true);

        if (_chatMessageTextBox is { } textbox)
        {
            await textbox.FocusAsync();
        }

        await followUpPromptTask;
    }

    private void RefreshFollowUpPrompts()
    {
        _chatViewModel?.RefreshFollowUpPrompts();
    }

    private async Task AddPrompt(string displayText, string promptText, bool fromFollowUpPrompt)
    {
        await ChatViewModel.AddFollowUpPromptAsync(displayText, promptText, fromFollowUpPrompt);

        await ChatViewModel.CallServiceAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ChatViewModel.DisconnectAsync();

        _cts.Cancel();

        await JSInteropHelpers.SafeDisposeAsync(_module);
    }

    private void LikeChatMessage(ChatViewModel chat)
    {
        chat.IsLiked = !chat.IsLiked;
        chat.IsDisliked = false;

        if (chat.IsLiked)
        {
            ChatViewModel.PostFeedback(FeedbackType.ThumbsUp);
        }
    }

    private void DislikeChatMessage(ChatViewModel chat)
    {
        chat.IsDisliked = !chat.IsDisliked;
        chat.IsLiked = false;

        if (chat.IsDisliked)
        {
            ChatViewModel.PostFeedback(FeedbackType.ThumbsDown);
        }
    }

    private async Task RetryChatMessageAsync(ChatViewModel chat)
    {
        await ChatViewModel.RetryChatMessageAsync(chat);
    }
}
