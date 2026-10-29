// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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

    private string ItemTooltip => Item.Tooltip ?? Item.Text ?? string.Empty;

    private Dictionary<string, object> AdditionalMenuItemAttributes
    {
        get
        {
            var attributes = new Dictionary<string, object>(Item.AdditionalAttributes ?? ImmutableDictionary<string, object>.Empty);
            attributes["data-tooltip"] = ItemTooltip;
            if (Item.Role is MenuItemRole.Radio)
            {
                // A radio option is chosen by its native click, or Enter or Space, rather than by the web component's
                // change event. The web component raises that event only when its own checked state changes, and that
                // state isn't reliable: it keeps an option checked after a re-render removes its checked attribute, so
                // clicking that option again raises no event and the menu would ignore the choice. It also doesn't
                // raise the event for the option that is already checked.
                attributes["onclick"] = EventCallback.Factory.Create<MouseEventArgs>(this, HandleRadioClicked);
                attributes["onkeydown"] = EventCallback.Factory.Create<KeyboardEventArgs>(this, HandleRadioKeyDown);
            }

            return attributes;
        }
    }

    private Task HandleItemClicked()
    {
        return Item.Role is MenuItemRole.Checkbox or MenuItemRole.Radio
            ? Task.CompletedTask
            : OnItemActivated.InvokeAsync(Item);
    }

    private Task HandleRadioClicked(MouseEventArgs args) => OnItemActivated.InvokeAsync(Item);

    // Fluent's menu item handles Enter and Space itself, without raising a click.
    private Task HandleRadioKeyDown(KeyboardEventArgs args) =>
        args.Key is "Enter" or " " ? OnItemActivated.InvokeAsync(Item) : Task.CompletedTask;

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

        // Radio options are chosen by their click or key press (see AdditionalMenuItemAttributes). Their change event
        // also fires when a re-render checks them, e.g. when the menu reopens with the option chosen last time, which
        // isn't a choice.
        return isChecked is true && Item.Role is MenuItemRole.Checkbox
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
