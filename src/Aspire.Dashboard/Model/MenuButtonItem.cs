// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

public class MenuButtonItem
{
    public bool IsDivider { get; set; }
    /// <summary>
    /// Whether the item is a non-interactive header used to label the menu (e.g. the resource
    /// a context menu was opened for). Header items render <see cref="Text"/> and <see cref="Icon"/>
    /// but ignore <see cref="OnClick"/> and are skipped by keyboard navigation.
    /// </summary>
    public bool IsHeader { get; set; }
    public List<MenuButtonItem>? NestedMenuItems { get; set; }
    public string? Text { get; set; }
    public string? Tooltip { get; set; }
    public Icon? Icon { get; set; }
    public Icon? SecondaryActionIcon { get; set; }
    public string? SecondaryActionAriaLabel { get; set; }
    public Func<Task>? OnSecondaryActionClick { get; set; }
    public bool IsSecondaryActionSelected { get; set; }
    /// <summary>
    /// Optional ARIA role for the item. Set to <see cref="MenuItemRole.MenuItemCheckbox"/> or
    /// <see cref="MenuItemRole.MenuItemRadio"/> to expose an accessible checked state (via
    /// <see cref="Checked"/>) that assistive technology can announce; leave <see langword="null"/>
    /// for an ordinary menu item.
    /// </summary>
    public MenuItemRole? Role { get; set; }
    /// <summary>
    /// Whether the item is currently checked. Only meaningful when <see cref="Role"/> is a
    /// checkable role, in which case it drives the reflected <c>aria-checked</c> state.
    /// </summary>
    public bool Checked { get; set; }
    public Func<Task>? OnClick { get; set; }
    public bool IsDisabled { get; set; }
    public string Id { get; set; } = Identifier.NewId();
    public string? Class { get; set; }
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }
}
