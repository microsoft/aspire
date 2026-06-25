// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class AspireMenuButton : FluentComponentBase
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
    public Appearance? ButtonAppearance { get; set; }

    [Parameter]
    public string? Title { get; set; }

    [Parameter]
    public string MenuButtonId { get; set; } = Identifier.NewId();

    [Parameter]
    public bool HideIcon { get; set; }

    private string? IconStyle => IconColorStyle is null ? null : $"color: {IconColorStyle};";

    // Map the Fluent Appearance parameter (kept for call-site compatibility) onto the native Deck
    // button classes. No Text => icon-only trigger (.icon-btn); with Text => .btn, where the accent
    // appearance is primary and the lightweight/stealth appearances are ghost (transparent) triggers.
    private string TriggerClass
    {
        get
        {
            var classes = new List<string>();

            if (string.IsNullOrWhiteSpace(Text))
            {
                classes.Add("icon-btn");
            }
            else
            {
                classes.Add("btn");
                switch (ButtonAppearance)
                {
                    case Appearance.Accent:
                        classes.Add("btn--primary");
                        break;
                    case Appearance.Lightweight:
                    case Appearance.Stealth:
                        classes.Add("btn--ghost");
                        break;
                }
            }

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
