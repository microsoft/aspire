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
    private static readonly Icon s_defaultIcon = new Icons.Regular.Size24.ChevronDown();

    private bool _visible;
    private Icon? _icon;
    private MenuButtonItem[] _items = [];
    private bool _disabled;

    [Parameter]
    public string? Text { get; set; }

    [Parameter]
    public Icon? IconStart { get; set; }

    [Parameter]
    public string? IconStartClass { get; set; }

    [Parameter]
    public Color? IconStartColor { get; set; }

    [Parameter]
    public string? IconStartCustomColor { get; set; }

    [Parameter]
    public Icon? Icon { get; set; }

    [Parameter]
    public Color? IconColor { get; set; }

    [Parameter]
    public string? IconCustomColor { get; set; }

    [Parameter]
    public string? ButtonClass { get; set; }

    [Parameter]
    public required IList<MenuButtonItem> Items { get; set; }

    [Parameter]
    public Appearance? ButtonAppearance { get; set; }

    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string MenuButtonId { get; set; } = Identifier.NewId();

    [Parameter]
    public bool HideIcon { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>
    /// Gets or sets the callback invoked immediately before the menu is opened.
    /// </summary>
    [Parameter]
    public EventCallback OnOpening { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether focus should return to this menu button after a menu item is clicked.
    /// </summary>
    /// <remarks>
    /// Use this for button-anchored menus because the underlying menu anchor is the element that opened the menu.
    /// Do not use this behavior for cursor-positioned or context menus where the anchor is only used for positioning.
    /// </remarks>
    [Parameter]
    public bool RestoreFocusOnItemClick { get; set; }

    protected override void OnParametersSet()
    {
        _icon = Icon ?? s_defaultIcon;
        UpdateItems();
    }

    private void UpdateItems()
    {
        if (Items != null && !_items.SequenceEqual(Items))
        {
            _items = Items.ToArray();
        }

        _disabled = Disabled || (!OnOpening.HasDelegate && !_items.Any(i => !i.IsDivider));
    }

    private async Task ToggleMenuAsync()
    {
        if (_visible)
        {
            _visible = false;
            return;
        }

        await OnOpening.InvokeAsync();
        UpdateItems();
        _visible = true;
    }

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args is not null && args.Key == "Escape")
        {
            _visible = false;
        }
    }
}
