// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Aspire.Dashboard.Components.Deck;

namespace Aspire.Dashboard.Model;

public static class CommonMenuItems
{
    public static void AddToggleHiddenResourcesMenuItem(
        List<MenuButtonItem> menuItems,
        IStringLocalizer<ControlsStrings> loc,
        bool showHiddenResources,
        IEnumerable<ResourceViewModel> resources,
        ISessionStorage sessionStorage,
        EventCallback<bool> refreshFunction)
    {
        var areResourcesHidden = resources.Any(r => r.IsResourceHidden(false));
        if (!showHiddenResources)
        {
            menuItems.Add(new MenuButtonItem
            {
                IsDisabled = !areResourcesHidden,
                OnClick = OnToggleShowHiddenResources,
                Text = loc[nameof(ControlsStrings.ShowHiddenResources)],
                Icon = DeckIconName.Eye
            });
        }
        else
        {
            menuItems.Add(new MenuButtonItem
            {
                OnClick = OnToggleShowHiddenResources,
                Text = loc[nameof(ControlsStrings.HideHiddenResources)],
                Icon = DeckIconName.EyeOff
            });
        }
        async Task OnToggleShowHiddenResources()
        {
            showHiddenResources = !showHiddenResources;
            await sessionStorage.SetAsync(BrowserStorageKeys.ResourcesShowHiddenResources, showHiddenResources).ConfigureAwait(true);
            await refreshFunction.InvokeAsync(showHiddenResources).ConfigureAwait(true);
        }
    }
}
