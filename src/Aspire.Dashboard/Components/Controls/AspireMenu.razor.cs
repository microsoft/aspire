// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public partial class AspireMenu : ComponentBase, IAsyncDisposable
{
    private readonly string _menuId = $"aspire-menu-{Guid.NewGuid():N}";

    private ElementReference _menuElement;
    private IJSObjectReference? _module;
    private DotNetObjectReference<AspireMenu>? _selfRef;
    private bool _initialized;

    // Cursor coordinates for context (non-anchored) menus, set by OpenAsync.
    private bool _useCursor;
    private int _cursorX;
    private int _cursorY;

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

    public async Task CloseAsync()
    {
        await SetOpenAsync(false);
    }

    /// <summary>
    /// Opens the menu as a context menu positioned at the given cursor coordinates.
    /// </summary>
    public async Task OpenAsync(int screenWidth, int screenHeight, int clientX, int clientY)
    {
        _useCursor = true;
        _cursorX = clientX;
        _cursorY = clientY;

        await SetOpenAsync(true);
        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && !_initialized)
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Controls/AspireMenu.razor.js");
            _selfRef ??= DotNetObjectReference.Create(this);

            // Anchored button menus position relative to the anchor element; context menus position at
            // the cursor coordinates supplied to OpenAsync.
            var mode = _useCursor || !Anchored ? "cursor" : "anchor";
            await _module.InvokeVoidAsync("initialize", _menuElement, _menuId, mode, Anchor, _cursorX, _cursorY, _selfRef);
            _initialized = true;
        }
        else if (!Open && _initialized)
        {
            await DisposeInteropAsync();
            _initialized = false;
            _useCursor = false;
        }
    }

    [JSInvokable]
    public async Task CloseFromJs()
    {
        if (Open)
        {
            await SetOpenAsync(false);
            StateHasChanged();
        }
    }

    private async Task HandleItemClicked(MenuButtonItem item)
    {
        if (item.OnClick is { } onClick)
        {
            await onClick();
        }

        await SetOpenAsync(false);

        if (RestoreFocusOnItemClick && !string.IsNullOrEmpty(Anchor))
        {
            await JS.InvokeVoidAsync("focusElement", Anchor);
        }
    }

    private async Task SetOpenAsync(bool open)
    {
        Open = open;

        if (OpenChanged.HasDelegate)
        {
            await OpenChanged.InvokeAsync(open);
        }
    }

    // The menu is fixed-position and placed by JS. Constrain width so nested submenus stay in sync.
    private const string MenuStyle = "max-width: var(--aspire-menu-max-width); min-width: 64px;";

    private async Task DisposeInteropAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _menuId);
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone; nothing to clean up.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeInteropAsync();

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _selfRef?.Dispose();
    }
}
