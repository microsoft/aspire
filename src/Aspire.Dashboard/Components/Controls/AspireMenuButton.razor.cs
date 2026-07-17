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

    protected override void OnParametersSet()
    {
        _icon = Icon ?? s_defaultIcon;

        if (Items != null && !_items.SequenceEqual(Items))
        {
            _items = Items.ToArray();

            // Disabled if there are no actionable items
            _disabled = !_items.Any(i => !i.IsDivider);
        }
    }

    private void ToggleMenu()
    {
        _visible = !_visible;
    }

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args is not null && args.Key == "Escape")
        {
            _visible = false;
        }
    }
}
