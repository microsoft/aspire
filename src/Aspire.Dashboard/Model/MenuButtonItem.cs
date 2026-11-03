// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

public class MenuButtonItem
{
    internal static MenuButtonItem CreateExternalLink(string text, string url, Icon icon, string? tooltip = null)
    {
        // app.js handles these attributes from the click event so browser popup blocking
        // doesn't prevent the link from opening in a new window.
        return new MenuButtonItem
        {
            Text = text,
            Tooltip = tooltip,
            Icon = icon,
            AdditionalAttributes = new Dictionary<string, object>
            {
                ["data-openbutton"] = "true",
                ["data-url"] = url,
                ["data-target"] = "_blank"
            }
        };
    }

    public bool IsDivider { get; set; }
    /// <summary>
    /// Whether the item is a non-interactive header used to label the menu (e.g. the resource
    /// a context menu was opened for). Header items render <see cref="Text"/> and <see cref="Icon"/>
    /// but ignore <see cref="OnClick"/> and are skipped by keyboard navigation.
    /// </summary>
    public bool IsHeader { get; set; }
    /// <summary>
    /// Whether the item is a non-interactive label for a group of related items within the menu
    /// (e.g. "State" above a set of state filters). Unlike <see cref="IsHeader"/>, a group header
    /// doesn't label the menu as a whole and can appear multiple times.
    /// </summary>
    public bool IsGroupHeader { get; set; }
    /// <summary>
    /// Whether the item is non-interactive placeholder text shown under a group header when the group
    /// has no items (e.g. "No existing recordings").
    /// </summary>
    public bool IsEmptyGroupText { get; set; }
    public List<MenuButtonItem>? NestedMenuItems { get; set; }
    public string? Text { get; set; }
    /// <summary>
    /// Tooltip text. Null uses the item text; an empty string disables the tooltip.
    /// </summary>
    public string? Tooltip { get; set; }
    /// <summary>
    /// Optional secondary text displayed in a muted style at the end of the item, before any
    /// secondary action (e.g. the duration of a recording).
    /// </summary>
    public string? Description { get; set; }
    /// <summary>
    /// Optional number displayed in a pill at the end of the item (e.g. how many resources a filter value matches).
    /// </summary>
    public int? Count { get; set; }
    public Icon? Icon { get; set; }
    /// <summary>
    /// Optional icon displayed inline before the text, in addition to the
    /// <see cref="Icon"/> that indicates its checked state.
    /// </summary>
    public Icon? StartIcon { get; set; }
    public Color StartIconColor { get; set; } = Color.Primary;
    public Icon? SecondaryActionIcon { get; set; }
    public string? SecondaryActionAriaLabel { get; set; }
    public Func<Task>? OnSecondaryActionClick { get; set; }
    public bool IsSecondaryActionSelected { get; set; }
    /// <summary>
    /// Optional ARIA role for the item. Set to <see cref="MenuItemRole.Checkbox"/> or
    /// <see cref="MenuItemRole.Radio"/> to expose an accessible checked state (via
    /// <see cref="Checked"/>) that assistive technology can announce; leave <see langword="null"/>
    /// for an ordinary menu item.
    /// </summary>
    public MenuItemRole? Role { get; set; }
    /// <summary>
    /// Whether the item is currently checked. Only meaningful when <see cref="Role"/> is a
    /// checkable role, in which case it drives the reflected <c>aria-checked</c> state.
    /// </summary>
    public bool Checked { get; set; }
    /// <summary>
    /// Optional callback for a <see cref="MenuItemRole.Checkbox"/> item that toggles independently.
    /// When set, the item reports both checking and unchecking, and the menu stays open so several
    /// items can be toggled in a row. The menu items are regenerated after the callback completes.
    /// </summary>
    public Func<bool, Task>? OnCheckedChanged { get; set; }
    public Func<Task>? OnClick { get; set; }
    public bool IsDisabled { get; set; }
    /// <summary>
    /// Gets or sets an optional stable identity used to preserve the rendered menu item across refreshes and reordering.
    /// </summary>
    public object? RenderKey { get; set; }
    public string Id { get; set; } = $"menu-item-{Guid.NewGuid():N}";
    public string? Class { get; set; }
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }
}
