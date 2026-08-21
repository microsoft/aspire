// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components;

public partial class AspireMenuButton : FluentComponentBase
{
    public AspireMenuButton(LibraryConfiguration configuration)
        : base(configuration)
    {
    }

    private static readonly Icon s_defaultIcon = new Icons.Regular.Size24.ChevronDown();

    private bool _renderMenu;
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
    public ButtonAppearance? ButtonAppearance { get; set; }

    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string MenuButtonId { get; set; } = $"menu-button-{Guid.NewGuid():N}";

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

    protected override void OnParametersSet()
    {
        _icon = Icon ?? s_defaultIcon;

        if (!ReferenceEquals(_renderedItemsProvider, ItemsProvider))
        {
            _renderedItemsProvider = ItemsProvider;
            _disabled = false;
        }

        if (_visible)
        {
            RefreshItems();

            if (_disabled)
            {
                OnMenuOpenChanged(false);
            }
        }
    }

    private void ToggleMenu()
    {
        if (_visible)
        {
            OnMenuOpenChanged(false);
            return;
        }

        RefreshItems();

        _renderMenu = true;
        _visible = true;
    }

    private void RefreshItems()
    {
        _items = ItemsProvider().ToArray();
        _disabled = !_items.Any(i => !i.IsDivider);
    }

    private void OnMenuOpenChanged(bool open)
    {
        _visible = open;
    }

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args is not null && args.Key == "Escape")
        {
            OnMenuOpenChanged(false);
        }
    }

}
