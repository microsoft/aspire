// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public partial class AspirePopupFocusNavigation : ComponentBase, IAsyncDisposable
{
    private readonly string _popupId = Identifier.NewId();
    private DotNetObjectReference<AspirePopupFocusNavigation>? _popupReference;
    private string? _registeredAnchorId;

    [Parameter]
    public required string AnchorId { get; set; }

    [Parameter]
    public bool Open { get; set; }

    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_registeredAnchorId is not null && (!Open || _registeredAnchorId != AnchorId))
        {
            await DisposeKeyboardNavigationAsync();
        }

        if (Open && _registeredAnchorId is null && !string.IsNullOrEmpty(AnchorId))
        {
            var anchorId = AnchorId;
            _registeredAnchorId = anchorId;
            _popupReference ??= DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("initializeAspirePopupKeyboardNavigation", anchorId, _popupId, _popupReference, new { tabExitsAlways = false });
        }
    }

    [JSInvokable]
    public async Task CloseAsync()
    {
        await DisposeKeyboardNavigationAsync();
        Open = false;

        if (OpenChanged.HasDelegate)
        {
            await OpenChanged.InvokeAsync(false);
        }
    }

    private async ValueTask DisposeKeyboardNavigationAsync()
    {
        if (_registeredAnchorId is not null)
        {
            var registeredAnchorId = _registeredAnchorId;
            _registeredAnchorId = null;
            try
            {
                await JS.InvokeVoidAsync("disposeAspirePopupKeyboardNavigation", registeredAnchorId, _popupId);
            }
            catch (JSDisconnectedException)
            {
                // Disposal can run while the Blazor circuit is disconnecting; the browser will drop the listener with the page.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeKeyboardNavigationAsync();
        _popupReference?.Dispose();
    }
}
