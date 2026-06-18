// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.CustomIcons;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model.Assistant;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class AssistantSidebarDialog : IAsyncDisposable
{
    private static readonly Icon s_gitHubCopilotIcon = new AspireIcons.Size16.GitHubCopilot();

    [Parameter]
    public AssistantDialogViewModel Content { get; set; } = default!;

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required IDialogService DialogService { get; init; }

    [Inject]
    public required IServiceProvider ServiceProvider { get; init; }

    protected override void OnInitialized()
    {
        InitializeChatViewModel();
    }

    private void InitializeChatViewModel()
    {
        Content.Chat.DisplayContainer = AssistantChatDisplayContainer.Sidebar;
    }

    private void OnModelInitialized()
    {
        StateHasChanged();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!ViewportInformation.IsDesktop)
        {
            await ExpandDialogAsync(openedForMobileView: true);
        }
    }

    public async Task CloseDialogAsync()
    {
        await ServiceProvider.GetRequiredService<IAssistantDisplayContext>().HideAssistantSidebarAsync();
    }

    public async Task ExpandDialogAsync(bool openedForMobileView)
    {
        Content.Chat.DisplayContainer = AssistantChatDisplayContainer.Switching;
        var assistantDisplayContext = ServiceProvider.GetRequiredService<IAssistantDisplayContext>();
        await assistantDisplayContext.HideAssistantSidebarAsync(restoreFocus: false);

        // The viewport switch changes which launcher is visible. Keep focus restoration
        // pointed at the control users can actually reach after the switch completes.
        var returnFocusElementId = GetReturnFocusElementId(openedForMobileView, Content.ReturnFocusElementId);
        await assistantDisplayContext.LaunchAssistantModelDialogAsync(Content.Chat, openedForMobileView, returnFocusElementId);
    }

    internal static string? GetReturnFocusElementId(bool openedForMobileView, string? returnFocusElementId)
    {
        return openedForMobileView
            ? MainLayout.NavigationButtonId
            : returnFocusElementId;
    }

    public async Task StartNewChatAsync()
    {
        var viewModel = ServiceProvider.GetRequiredService<AssistantChatViewModel>();
        var initializeTask = viewModel.InitializeAsync();

        await AIContextProvider.SetAssistantSidebarAsync(viewModel);
        InitializeChatViewModel();

        await initializeTask;
    }

    public ValueTask DisposeAsync()
    {
        // If the assistant dialog was closed without switching to the dialog, dispose the chat view model.
        if (Content.Chat.DisplayContainer == AssistantChatDisplayContainer.Sidebar)
        {
            Content.Chat.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
