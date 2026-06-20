// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Assistant;

public sealed class AssistantDialogViewModel
{
    public AssistantChatViewModel Chat { get; set; } = null!;

    public bool OpenedForMobileView { get; set; }

    internal string? ReturnFocusElementId { get; set; }
}
