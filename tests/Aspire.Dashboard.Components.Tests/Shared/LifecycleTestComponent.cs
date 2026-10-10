// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class LifecycleTestComponent : ComponentBase, IDisposable
{
    [Parameter, EditorRequired]
    public required Action Initialized { get; init; }

    [Parameter, EditorRequired]
    public required Action Disposed { get; init; }

    protected override void OnInitialized()
    {
        Initialized();
    }

    public void Dispose()
    {
        Disposed();
    }
}