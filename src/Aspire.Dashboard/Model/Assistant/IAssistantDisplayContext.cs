// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Assistant;

/// <summary>
/// Tracks assistant display state that is only needed by dashboard components.
/// </summary>
internal interface IAssistantDisplayContext
{
    string? AssistantReturnFocusElementId { get; }
    bool RestoreFocusOnAssistantSidebarHidden { get; }

    Task LaunchAssistantModelDialogAsync(AssistantChatViewModel viewModel, bool openedForMobileView = false, string? returnFocusElementId = null);
    Task LaunchAssistantSidebarAsync(AssistantChatViewModel viewModel, string? returnFocusElementId = null);
    Task HideAssistantSidebarAsync(bool restoreFocus = true);
}
