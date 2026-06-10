// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class AssistantModalDialog : IAsyncDisposable
{
    [Parameter]
    public AssistantDialogViewModel Content { get; set; } = default!;

    [CascadingParameter]
    private IDialogInstance Dialog { get; set; } = default!;

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Inject]
    public required IDialogService DialogService { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required IServiceProvider ServiceProvider { get; init; }

    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource? _disconnectTcs;
    private DotNetObjectReference<ChatModalInterop>? _chatModalInteropReference;

    protected override void OnInitialized()
    {
        InitializeChatViewModel();
        _chatModalInteropReference = DotNetObjectReference.Create(new ChatModalInterop(this));
    }

    private void InitializeChatViewModel()
    {
        _disconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Content.Chat.DisplayContainer = AssistantChatDisplayContainer.Dialog;
        Content.Chat.OnDisconnectCallback = () =>
        {
            _disconnectTcs?.TrySetResult();
            return Task.CompletedTask;
        };
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ViewportInformation.IsDesktop && Content.OpenedForMobileView)
        {
            await CloseAndDisplaySidebarAsync();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("attachChatClickEvent", AssistantChatViewModel.ChatAssistantContainerId, _chatModalInteropReference);
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _chatModalInteropReference?.Dispose();

        // If the assistant dialog was closed without switching to the sidebar, dispose the chat view model.
        if (Content.Chat.DisplayContainer == AssistantChatDisplayContainer.Dialog)
        {
            Content.Chat.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public static async Task OpenDialogAsync(IDialogService dialogService, string title, AssistantDialogViewModel viewModel)
    {
        var parameters = new DialogParameters
        {
            Id = "chat-modal-dialog",
            Title = title,
            Width = "min(800px, 100vw)",
            TrapFocus = true,
            Modal = true,
            PreventDismissOnOverlayClick = true
        };

        var options = parameters.ToDialogOptions();
        options.Data = viewModel;
        await dialogService.ShowDialogAsync<AssistantModalDialog>(options);
    }

    private async Task CloseAndDisplaySidebarAsync()
    {
        Content.Chat.DisplayContainer = AssistantChatDisplayContainer.Switching;
        await CloseAndWaitForCleanUpAsync();
        await DisplaySidebarViewAsync();
    }

    private async Task CloseAndWaitForCleanUpAsync()
    {
        await Dialog.CloseAsync();
        await (_disconnectTcs?.Task ?? Task.CompletedTask);
    }

    private async Task DisplaySidebarViewAsync()
    {
        await AIContextProvider.LaunchAssistantSidebarAsync(Content.Chat);
    }

    public async Task StartNewChatAsync()
    {
        var viewModel = ServiceProvider.GetRequiredService<AssistantChatViewModel>();
        var initializeTask = viewModel.InitializeAsync();

        Content.Chat = viewModel;
        InitializeChatViewModel();

        await initializeTask;
    }

    private void OnModelInitialized()
    {
        StateHasChanged();
    }

    /// <summary>
    /// Handle user clicking on clicks in the browser.
    /// </summary>
    private sealed class ChatModalInterop
    {
        private readonly AssistantModalDialog _dialog;

        public ChatModalInterop(AssistantModalDialog dialog)
        {
            _dialog = dialog;
        }

        [JSInvokable]
        public async Task NavigateUrl(string url)
        {
            // Navigate to the URL and wait for the dialog to be closed
            _dialog.NavigationManager.NavigateTo(url);
            await _dialog.CloseAndDisplaySidebarAsync();
        }
    }
}
