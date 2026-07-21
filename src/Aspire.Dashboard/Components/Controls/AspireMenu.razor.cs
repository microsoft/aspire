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
        // Tear down the keyboard navigation when the menu closes, when the anchor element
        // changes, OR when the consumer switches the menu out of anchored mode while it stays
        // open (context-menu mode has no stable anchor for the popup listeners to bind to).
        if (_registeredAnchorId is not null && (!Open || !Anchored || _registeredAnchorId != Anchor))
        {
            await DisposeKeyboardNavigationAsync();
        }

        if (Open && Anchored && _registeredAnchorId is null && !string.IsNullOrEmpty(Anchor))
        {
            var anchor = Anchor;
            _registeredAnchorId = anchor;
            _menuReference ??= DotNetObjectReference.Create(this);
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
        // Close the menu and restore trigger focus BEFORE invoking the item callback. Some
        // callbacks (for example "View JSON") open their own focus-trapped modal dialog and
        // return once it has opened; if we restored trigger focus afterward, that focusElement
        // call would run after the dialog already moved focus into itself and would yank focus
        // back out from behind it. Restoring focus first means any focus-owning UI the callback
        // opens is always the last thing to grab focus, so it keeps it.
        await SetOpenAsync(false);

        if (RestoreFocusOnItemClick && !string.IsNullOrEmpty(Anchor))
        {
            await JS.InvokeVoidAsync("focusElement", Anchor);
        }

        if (item.OnClick is {} onClick)
        {
            await onClick();
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
        // Use try/finally so the DotNetObjectReference is always released, even if the
        // browser-side dispose call throws something other than JSDisconnectedException
        // (a transient JS error during teardown otherwise keeps this component rooted by
        // the DotNetObjectReference table for the lifetime of the circuit).
        try
        {
            await DisposeKeyboardNavigationAsync();
        }
        finally
        {
            _menuReference?.Dispose();
        }
    }
}
