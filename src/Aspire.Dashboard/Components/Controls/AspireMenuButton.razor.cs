// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Aspire.Dashboard.Components;

public partial class AspireMenuButton : ComponentBase
{
    private const DeckIconName DefaultIcon = DeckIconName.ChevronDown;

    private bool _visible;
    private DeckIconName _icon = DefaultIcon;
    private MenuButtonItem[] _items = [];
    private bool _disabled;

    [Parameter]
    public string? Text { get; set; }

    [Parameter]
    public DeckIconName? IconStart { get; set; }

    [Parameter]
    public DeckIconName? Icon { get; set; }

    /// <summary>Optional CSS color applied to the trigger icon (e.g. <c>var(--foreground-settings-text)</c>).</summary>
    [Parameter]
    public string? IconColorStyle { get; set; }

    [Parameter]
    public string? ButtonClass { get; set; }

    [Parameter]
    public required IList<MenuButtonItem> Items { get; set; }

    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string MenuButtonId { get; set; } = Guid.NewGuid().ToString("N");

    [Parameter]
    public bool HideIcon { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether focus should return to this menu button after a menu item is clicked.
    /// </summary>
    /// <remarks>
    /// Use this for button-anchored menus because the underlying menu anchor is the element that opened the menu.
    /// Do not use this behavior for cursor-positioned or context menus where the anchor is only used for positioning.
    /// </remarks>
    [Parameter]
    public bool RestoreFocusOnItemClick { get; set; }

    private string? IconStyle => IconColorStyle is null ? null : $"color: {IconColorStyle};";

    private string TriggerClass
    {
        get
        {
            var classes = new List<string>
            {
                string.IsNullOrWhiteSpace(Text) ? "icon-btn" : "btn"
            };

            if (!string.IsNullOrWhiteSpace(ButtonClass))
            {
                classes.Add(ButtonClass);
            }

            return string.Join(' ', classes);
        }
    }

    protected override void OnParametersSet()
    {
        _icon = Icon ?? DefaultIcon;

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
