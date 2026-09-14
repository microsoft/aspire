// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components;

public partial class AspireMenuButton : FluentComponentBase, IAsyncDisposable
{
    private static readonly Icon s_defaultIcon = new Icons.Regular.Size24.ChevronDown();
    private const int InitializationWaitMilliseconds = 100;

    private IJSObjectReference? _jsModule;
    private bool _renderMenu;
    private bool _menuRenderComplete;
    private bool _openWhenMenuRenderCompletes;
    private bool _visible;
    private Icon? _icon;
    private MenuButtonItem[] _items = [];
    private bool _disabled;
    private Func<IList<MenuButtonItem>>? _renderedItemsProvider;

    [Parameter]
    public string? Text { get; set; }

    [Parameter]
    public Icon? IconStart { get; set; }

    [Parameter]
    public Icon? Icon { get; set; }

    [Parameter]
    public Color? IconColor { get; set; }

    [Parameter]
    public string? IconCustomColor { get; set; }

    [Parameter]
    public string? ButtonClass { get; set; }

    /// <summary>
    /// Gets or sets the callback that provides menu items when the menu is opened.
    /// </summary>
    [Parameter]
    public required Func<IList<MenuButtonItem>> ItemsProvider { get; set; }

    [Parameter]
    public Appearance? ButtonAppearance { get; set; }

    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string MenuButtonId { get; set; } = Identifier.NewId();

    [Parameter]
    public bool HideIcon { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether focus should return to this menu button after a menu item is clicked.
    /// </summary>
    /// <remarks>
    /// Focus restoration is enabled by default because the underlying menu anchor is the button that opened the menu.
    /// </remarks>
    [Parameter]
    public bool RestoreFocusOnItemClick { get; set; } = true;

    [Inject]
    public required IJSRuntime JS { get; init; }

    protected override void OnParametersSet()
    {
        _icon = Icon ?? s_defaultIcon;

        if (!ReferenceEquals(_renderedItemsProvider, ItemsProvider))
        {
            _renderedItemsProvider = ItemsProvider;
            _disabled = false;
        }

        if (_visible || _openWhenMenuRenderCompletes)
        {
            RefreshItems();

            if (_disabled)
            {
                OnMenuOpenChanged(false);
            }
        }
    }

    private async Task ToggleMenu()
    {
        if (_visible)
        {
            OnMenuOpenChanged(false);
            return;
        }

        if (_renderMenu && !_menuRenderComplete)
        {
            _openWhenMenuRenderCompletes = true;
            return;
        }

        if (!_menuRenderComplete)
        {
            // Keep the menu out of the render tree until observation is ready so a parent render
            // during the lazy module import can't complete menu rendering before this setup.
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Controls/AspireMenuButton.razor.js");
            await _jsModule.InvokeVoidAsync("prepareForFluentMenuInitialization", MenuButtonId);
        }

        RefreshItems();

        _renderMenu = true;

        // Reopen a retained menu immediately, but defer the first open until FluentMenu has
        // rendered and initialized its JavaScript modules.
        if (_menuRenderComplete)
        {
            _visible = true;
        }
        else
        {
            _openWhenMenuRenderCompletes = true;
        }
    }

    private void RefreshItems()
    {
        _items = ItemsProvider().ToArray();
        _disabled = !_items.Any(i => !i.IsDivider);
    }

    private async Task OnMenuRenderComplete()
    {
        // FluentMenu writes aria-expanded after its JavaScript modules are initialized.
        // Wait for that signal before opening the menu.
        await _jsModule!.InvokeVoidAsync("waitForFluentMenuInitialization", MenuButtonId, InitializationWaitMilliseconds);
        _menuRenderComplete = true;

        if (_openWhenMenuRenderCompletes)
        {
            OnMenuOpenChanged(true);
        }
    }

    private void OnMenuOpenChanged(bool open)
    {
        _openWhenMenuRenderCompletes = false;
        _visible = open;
    }

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args is not null && args.Key == "Escape")
        {
            OnMenuOpenChanged(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_jsModule is { } jsModule)
        {
            if (_renderMenu && !_menuRenderComplete)
            {
                try
                {
                    await jsModule.InvokeVoidAsync("cancelFluentMenuInitialization", MenuButtonId);
                }
                catch (JSDisconnectedException)
                {
                    // The browser may already be gone when the component is disposed.
                }
                catch (OperationCanceledException)
                {
                    // The browser may already be gone when the component is disposed.
                }
            }

            await JSInteropHelpers.SafeDisposeAsync(jsModule);
        }
    }
}
