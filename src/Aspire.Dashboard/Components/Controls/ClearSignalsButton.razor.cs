// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class ClearSignalsButton : ComponentBase
{
    private const DeckIconName ClearSelectedResourceIcon = DeckIconName.CheckboxChecked;
    private const DeckIconName ClearAllResourcesIcon = DeckIconName.Stack;

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Parameter]
    public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }

    [Parameter]
    public required Func<ResourceKey?, Task> HandleClearSignal { get; set; }

    private readonly List<MenuButtonItem> _clearMenuItems = new();

    protected override void OnParametersSet()
    {
        _clearMenuItems.Clear();

        _clearMenuItems.Add(new()
        {
            Id = "clear-menu-all",
            Icon = ClearAllResourcesIcon,
            OnClick = () => HandleClearSignal(null),
            Text = ControlsStringsLoc[name: nameof(ControlsStrings.ClearAllResources)],
        });

        _clearMenuItems.Add(new()
        {
            Id = "clear-menu-resource",
            Icon = ClearSelectedResourceIcon,
            OnClick = () => HandleClearSignal(SelectedResource.Id?.GetResourceKey()),
            IsDisabled = SelectedResource.Id == null,
            Text = SelectedResource.Id == null
                ? ControlsStringsLoc[nameof(ControlsStrings.ClearPendingSelectedResource)]
                : string.Format(CultureInfo.InvariantCulture, ControlsStringsLoc[name: nameof(ControlsStrings.ClearSelectedResource)], SelectedResource.Name),
        });
    }
}
