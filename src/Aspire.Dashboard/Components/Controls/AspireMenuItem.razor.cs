// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class AspireMenuItem
{
    [Parameter, EditorRequired]
    public required MenuButtonItem Item { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<MenuButtonItem> OnItemActivated { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<MenuButtonItem> OnSecondaryActionClicked { get; set; }

    [Parameter]
    public EventCallback<(MenuButtonItem Item, bool IsChecked)> OnItemToggled { get; set; }

    private string SecondaryActionId => $"{Item.Id}-secondary-action";

    private string ItemTooltip => !string.IsNullOrEmpty(Item.Tooltip) ? Item.Tooltip : Item.Text ?? string.Empty;

    private Dictionary<string, object> AdditionalMenuItemAttributes
    {
        get
        {
            var attributes = new Dictionary<string, object>(Item.AdditionalAttributes ?? ImmutableDictionary<string, object>.Empty);
            attributes["data-tooltip"] = ItemTooltip;

            return attributes;
        }
    }

    private Task HandleItemClicked()
    {
        return Item.Role is MenuItemRole.Checkbox or MenuItemRole.Radio
            ? Task.CompletedTask
            : OnItemActivated.InvokeAsync(Item);
    }

    private Task HandleItemCheckedChanged(bool? isChecked)
    {
        if (Item.OnCheckedChanged is not null && Item.Role is MenuItemRole.Checkbox)
        {
            // The web component also raises its change event when a re-render updates the checked
            // state, e.g. when toggling one value unchecks an "All" item. Only a change that differs
            // from the rendered state comes from the user.
            var newValue = isChecked is true;
            return newValue == Item.Checked
                ? Task.CompletedTask
                : OnItemToggled.InvokeAsync((Item, newValue));
        }

        return isChecked is true && Item.Role is MenuItemRole.Checkbox or MenuItemRole.Radio
            ? OnItemActivated.InvokeAsync(Item)
            : Task.CompletedTask;
    }

    private Task HandleSecondaryActionClicked()
    {
        return OnSecondaryActionClicked.InvokeAsync(Item);
    }

    private static string GetIconSlot(MenuItemRole? role) => role switch
    {
        MenuItemRole.Checkbox or MenuItemRole.Radio => "indicator",
        _ => "start"
    };
}
