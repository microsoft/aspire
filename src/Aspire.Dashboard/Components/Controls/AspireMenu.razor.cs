// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Utilities;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components;

public partial class AspireMenu : FluentComponentBase
{
    public AspireMenu(LibraryConfiguration configuration)
        : base(configuration)
    {
    }

    private FluentMenu? _menu;
    private IReadOnlyList<MenuButtonItem>? _renderedItems;
    private bool _refreshMenuAfterRender;
    private bool? _appliedOpen;
    private int _targetOffsetLeft;
    private int _targetOffsetTop;

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
    public EventCallback OnRenderComplete { get; set; }

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

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_renderedItems, Items))
        {
            _renderedItems = Items;
            _refreshMenuAfterRender = Open;
        }

        if (_appliedOpen != Open)
        {
            _refreshMenuAfterRender = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && OnRenderComplete.HasDelegate)
        {
            await OnRenderComplete.InvokeAsync();
        }

        if (_refreshMenuAfterRender)
        {
            _refreshMenuAfterRender = false;

            if (_menu is not null)
            {
                if (Open)
                {
                    if (Anchored)
                    {
                        // Trigger already identifies the anchor. The parameterless path leaves placement
                        // to Fluent's CSS anchor positioning, including block and inline viewport fallbacks.
                        await _menu.OpenMenuAsync();
                    }
                    else
                    {
                        await _menu.OpenMenuAsync(Anchor, _targetOffsetLeft, _targetOffsetTop);
                    }
                }
                else
                {
                    await _menu.CloseMenuAsync();
                }

                _appliedOpen = Open;
            }
        }
    }

    public async Task CloseAsync()
    {
        await SetOpenAsync(false);
    }

    public async Task OpenAsync(int screenWidth, int screenHeight, int clientX, int clientY)
    {
        if (_menu is not null)
        {
            // Calculate the position to display the context menu using the cursor position (clientX, clientY)
            // together with the screen width and height.
            // The menu may need to be displayed above or left of the cursor to fit in the screen.
            const int estimatedMenuWidth = 200;
            _targetOffsetLeft = clientX + estimatedMenuWidth > screenWidth
                ? Math.Max(0, clientX - estimatedMenuWidth)
                : clientX;
            _targetOffsetTop = clientY + CalculatedVerticalThreshold > screenHeight
                ? Math.Max(0, clientY - CalculatedVerticalThreshold)
                : clientY;

            Style = new StyleBuilder()
                .AddStyle("max-width", "368px")
                .AddStyle("min-width", "64px")
                .Build();

            // Escape and light-dismiss can close the browser popover without raising OpenedChanged.
            // Treat every cursor request as a new open/position request even when Open is still true.
            _refreshMenuAfterRender = true;
            await SetOpenAsync(true);

            StateHasChanged();
        }
    }

    private async Task HandleItemClicked(MenuButtonItem item)
    {
        await SetOpenAsync(false);

        if (RestoreFocusOnItemClick && !string.IsNullOrEmpty(Anchor))
        {
            await JS.InvokeVoidAsync("focusElement", Anchor);
        }

        // Item callbacks can move focus to a dialog or another control, so restore the
        // menu trigger first to avoid stealing focus back after the callback completes.
        if (item.OnClick is { } onClick)
        {
            await onClick();
        }
    }

    private async Task OnOpenChanged(bool open)
    {
        _appliedOpen = open;
        await SetOpenAsync(open);
    }

    private async Task SetOpenAsync(bool open)
    {
        Open = open;
        StateHasChanged();

        if (OpenChanged.HasDelegate)
        {
            await OpenChanged.InvokeAsync(open);
        }
    }
}
