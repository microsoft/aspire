// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Utilities;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public partial class AspireMenu : FluentComponentBase, IAsyncDisposable
{
    private FluentMenu? _menu;
    private readonly string _menuId = Identifier.NewId();
    private DotNetObjectReference<AspireMenu>? _menuReference;
    private string? _registeredAnchorId;

    [Parameter]
    public string? Anchor { get; set; }

    [Parameter]
    public bool Open { get; set; }

    [Parameter]
    public bool Anchored { get; set; } = true;

    [Parameter]
    public int? VerticalThreshold { get; set; }

    /// <summary>
    /// Raised when the <see cref="Open"/> property changed.
    /// </summary>
    [Parameter]
    public EventCallback<bool> OpenChanged { get; set; }

    [Parameter]
    public required IReadOnlyList<MenuButtonItem> Items { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether focus should return to <see cref="Anchor"/> after a menu item is clicked.
    /// </summary>
    /// <remarks>
    /// Use this only for button-anchored menus where <see cref="Anchor"/> identifies the element that opened the menu.
    /// Do not enable it for cursor-positioned or context menus where <see cref="Anchor"/> is only used for positioning.
    /// </remarks>
    [Parameter]
    public bool RestoreFocusOnItemClick { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    // Each menu item is approximately 32px tall, plus 16px padding for the menu container.
    private const int EstimatedItemHeight = 32;
    private const int MenuVerticalPadding = 16;

    private int CalculatedVerticalThreshold => VerticalThreshold ?? (Items.Count * EstimatedItemHeight + MenuVerticalPadding);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_registeredAnchorId is not null && (!Open || _registeredAnchorId != Anchor))
        {
            await DisposeKeyboardNavigationAsync();
        }

        if (Open && Anchored && _registeredAnchorId is null && !string.IsNullOrEmpty(Anchor))
        {
            var anchor = Anchor;
            _registeredAnchorId = anchor;
            _menuReference ??= DotNetObjectReference.Create(this);
            // Fluent UI's menu keyboard helper currently listens from the popup element after the
            // fluent-menu web component can stop bubbling Tab events. Use Aspire's capture-phase
            // mitigation until Fluent UI exposes equivalent behavior we can adopt directly.
            // See: https://github.com/microsoft/fluentui-blazor/blob/49346cfc358677b46bc7737aa2db1a548202dd6f/src/Core/Components/AnchoredRegion/FluentAnchoredRegion.razor.js
            await JS.InvokeVoidAsync("initializeAspirePopupKeyboardNavigation", anchor, _menuId, _menuReference, new { tabExitsAlways = true });
        }
    }

    [JSInvokable]
    public async Task CloseAsync()
    {
        await SetOpenAsync(false);
        StateHasChanged();
    }

    public async Task OpenAsync(int screenWidth, int screenHeight, int clientX, int clientY)
    {
        if (_menu is { } menu)
        {
            // Calculate the position to display the context menu using the cursor position (clientX, clientY)
            // together with the screen width and height.
            // The menu may need to be displayed above or left of the cursor to fit in the screen.
            var left = 0;
            var right = 0;
            var top = 0;
            var bottom = 0;

            if (clientX + menu.HorizontalThreshold > screenWidth)
            {
                right = screenWidth - clientX;
            }
            else
            {
                left = clientX;
            }

            if (clientY + CalculatedVerticalThreshold > screenHeight)
            {
                bottom = screenHeight - clientY;
            }
            else
            {
                top = clientY;
            }

            // Overwrite the style. We don't want to add new position values each time the menu is opened.
            Style = new StyleBuilder()
                .AddStyle("left", $"{left}px", left != 0)
                .AddStyle("right", $"{right}px", right != 0)
                .AddStyle("top", $"{top}px", top != 0)
                .AddStyle("bottom", $"{bottom}px", bottom != 0)
                // Width values come from fluentui-blazor stylesheet; max-width uses an app CSS variable so nested submenus stay in sync.
                // Explicitly set to override min-width: fit-content applied by library to some menus.
                .AddStyle("max-width", "var(--aspire-menu-max-width)")
                .AddStyle("min-width", "64px")
                .Build();

            await SetOpenAsync(true);

            StateHasChanged();
        }
    }

    private async Task HandleItemClicked(MenuButtonItem item)
    {
        if (item.OnClick is {} onClick)
        {
            await onClick();
        }
        await SetOpenAsync(false);

        if (RestoreFocusOnItemClick && !string.IsNullOrEmpty(Anchor))
        {
            await JS.InvokeVoidAsync("focusElement", Anchor);
        }
    }

    private async Task OnOpenChanged(bool open)
    {
        await SetOpenAsync(open);
    }

    private async Task SetOpenAsync(bool open)
    {
        if (!open)
        {
            await DisposeKeyboardNavigationAsync();
        }

        Open = open;

        if (OpenChanged.HasDelegate)
        {
            await OpenChanged.InvokeAsync(open);
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
                await JS.InvokeVoidAsync("disposeAspirePopupKeyboardNavigation", registeredAnchorId, _menuId);
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
        _menuReference?.Dispose();
    }
}
