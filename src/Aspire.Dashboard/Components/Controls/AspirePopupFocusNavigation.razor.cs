// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

/// <summary>
/// Adds Aspire popup keyboard navigation to arbitrary popup content.
/// </summary>
/// <remarks>
/// This component intentionally does not render or position the popup. It only wires the local keyboard
/// mitigation around content that is already hosted by a Fluent UI popup. Remove it once Fluent UI's
/// anchored-region keyboard navigation handles Fluent web component anchors and popup/menu events from
/// the capture phase. See the upstream helper:
/// https://github.com/microsoft/fluentui-blazor/blob/49346cfc358677b46bc7737aa2db1a548202dd6f/src/Core/Components/AnchoredRegion/FluentAnchoredRegion.razor.js
/// </remarks>
public partial class AspirePopupFocusNavigation : ComponentBase, IAsyncDisposable
{
    private readonly string _popupId = Identifier.NewId();
    private DotNetObjectReference<AspirePopupFocusNavigation>? _popupReference;
    private string? _registeredAnchorId;

    /// <summary>
    /// Gets or sets the id of the element that opened the popup.
    /// </summary>
    [Parameter]
    public required string AnchorId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the popup is open.
    /// </summary>
    [Parameter]
    public bool Open { get; set; }

    /// <summary>
    /// Raised when the <see cref="Open"/> property changes.
    /// </summary>
    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }

    /// <summary>
    /// Gets or sets the popup content that receives keyboard navigation.
    /// </summary>
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
