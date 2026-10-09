// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class TestGlobalKeydownListener(params AspireKeyboardShortcut[] shortcuts) : IGlobalKeydownListener
{
    public IReadOnlySet<AspireKeyboardShortcut> SubscribedShortcuts { get; } = shortcuts.ToHashSet();

    public Task OnPageKeyDownAsync(AspireKeyboardShortcut shortcut)
    {
        return Task.CompletedTask;
    }
}
