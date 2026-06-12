// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Interaction;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

public partial class MessageBarContainer : ComponentBase, IDisposable
{
    [Inject]
    public required CustomInteractionState CustomInteractionState { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    private bool HasActiveIframe => CustomInteractionState.Iframes.Any(f => f.IsActive);

    protected override void OnInitialized()
    {
        CustomInteractionState.OnIframesChanged += OnIframesChanged;
    }

    private void OnIframesChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        CustomInteractionState.OnIframesChanged -= OnIframesChanged;
    }
}
