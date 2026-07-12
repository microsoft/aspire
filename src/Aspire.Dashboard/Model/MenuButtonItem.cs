// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

public class MenuButtonItem
{
    public bool IsDivider { get; set; }
    public List<MenuButtonItem>? NestedMenuItems { get; set; }
    public string? Text { get; set; }
    public string? Tooltip { get; set; }
    public DeckIconName? Icon { get; set; }
    public string? FluentIconName { get; set; }
    public IconVariant? FluentIconVariant { get; set; }
    public Func<Task>? OnClick { get; set; }
    public bool IsDisabled { get; set; }
    public string Id { get; set; } = Identifier.NewId();
    public string? Class { get; set; }
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }
}
