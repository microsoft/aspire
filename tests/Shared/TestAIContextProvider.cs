// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Assistant.Ghcp;
using Aspire.Dashboard.Model.Assistant.Prompts;
using Aspire.Dashboard.Resources;

namespace Aspire.Dashboard.Tests;

public class TestAIContextProvider : IAIContextProvider, IAssistantDisplayContext
{
    private readonly object _lock = new();
    private readonly List<Func<Task>> _displayChangedCallbacks = [];

    public AssistantChatViewModel? AssistantChatViewModel { get; set; }
    public bool ShowAssistantSidebarDialog { get; set; }
    public string? AssistantReturnFocusElementId { get; private set; }
    public bool RestoreFocusOnAssistantSidebarHidden { get; private set; } = true;
    public bool Enabled { get; set; }
    public AssistantChatState? ChatState { get; set; }
    public string? LastAssistantModelDialogReturnFocusElementId { get; private set; }
    public IceBreakersBuilder IceBreakersBuilder { get; } = new IceBreakersBuilder(new TestStringLocalizer<AIPrompts>());

    public AIContext AddNew(string description, Action<AIContext> configure)
    {
        return new AIContext(this, raiseChange: () => { }) { Description = description };
    }

    public AIContext? GetContext()
    {
        return null;
    }

    public Task<GhcpInfoResponse> GetInfoAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task HideAssistantSidebarAsync() => HideAssistantSidebarAsync(restoreFocus: true);

    public async Task HideAssistantSidebarAsync(bool restoreFocus)
    {
        AssistantChatViewModel = null;
        ShowAssistantSidebarDialog = false;
        RestoreFocusOnAssistantSidebarHidden = restoreFocus;
        await ExecuteDisplayChangedCallbacksAsync();
    }

    public Task LaunchAssistantModelDialogAsync(AssistantChatViewModel viewModel, bool openedForMobileView = false)
    {
        return LaunchAssistantModelDialogAsync(viewModel, openedForMobileView, returnFocusElementId: null);
    }

    public async Task LaunchAssistantModelDialogAsync(AssistantChatViewModel viewModel, bool openedForMobileView, string? returnFocusElementId)
    {
        AssistantReturnFocusElementId = returnFocusElementId;
        LastAssistantModelDialogReturnFocusElementId = returnFocusElementId;
        await ExecuteDisplayChangedCallbacksAsync();
    }

    public Task LaunchAssistantSidebarAsync(AssistantChatViewModel viewModel)
    {
        return LaunchAssistantSidebarAsync(viewModel, returnFocusElementId: null);
    }

    public async Task LaunchAssistantSidebarAsync(AssistantChatViewModel viewModel, string? returnFocusElementId)
    {
        AssistantChatViewModel = viewModel;
        ShowAssistantSidebarDialog = true;
        AssistantReturnFocusElementId = returnFocusElementId;
        RestoreFocusOnAssistantSidebarHidden = true;
        await ExecuteDisplayChangedCallbacksAsync();
    }

    public Task LaunchAssistantSidebarAsync(Func<InitializePromptContext, Task> sendInitialPrompt)
    {
        ShowAssistantSidebarDialog = true;
        AssistantReturnFocusElementId = null;
        RestoreFocusOnAssistantSidebarHidden = false;
        return ExecuteDisplayChangedCallbacksAsync();
    }

    public IDisposable OnContextChanged(Func<Task> callback)
    {
        throw new NotImplementedException();
    }

    public IDisposable OnDisplayChanged(Func<Task> callback)
    {
        lock (_lock)
        {
            _displayChangedCallbacks.Add(callback);
        }

        return new DisplayChangedSubscription(this, callback);
    }

    public void Remove(AIContext context)
    {
    }

    public async Task SetAssistantSidebarAsync(AssistantChatViewModel viewModel)
    {
        AssistantChatViewModel = viewModel;
        await ExecuteDisplayChangedCallbacksAsync();
    }

    private async Task ExecuteDisplayChangedCallbacksAsync()
    {
        Func<Task>[] callbacks;
        lock (_lock)
        {
            callbacks = [.. _displayChangedCallbacks];
        }

        foreach (var callback in callbacks)
        {
            await callback();
        }
    }

    private sealed class DisplayChangedSubscription : IDisposable
    {
        private readonly TestAIContextProvider _provider;
        private readonly Func<Task> _callback;

        public DisplayChangedSubscription(TestAIContextProvider provider, Func<Task> callback)
        {
            _provider = provider;
            _callback = callback;
        }

        public void Dispose()
        {
            lock (_provider._lock)
            {
                _provider._displayChangedCallbacks.Remove(_callback);
            }
        }
    }
}
