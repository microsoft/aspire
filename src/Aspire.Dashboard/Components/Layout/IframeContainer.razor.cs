// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model.Interaction;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

public partial class IframeContainer : ComponentBase, IDisposable
{
    private ImmutableArray<IframeState> _iframes = [];

    [Inject]
    public required CustomInteractionState CustomInteractionState { get; init; }

    protected override void OnInitialized()
    {
        _iframes = CustomInteractionState.Iframes;
        CustomInteractionState.OnIframesChanged += OnIframesChanged;
    }

    private void OnIframesChanged()
    {
        _iframes = CustomInteractionState.Iframes;
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        CustomInteractionState.OnIframesChanged -= OnIframesChanged;
    }
}
