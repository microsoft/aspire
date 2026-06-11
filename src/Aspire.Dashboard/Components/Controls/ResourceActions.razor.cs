// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components;

public partial class ResourceActions : ComponentBase
{
    private static readonly Icon s_consoleLogsIcon = new Icons.Regular.Size16.SlideText();

    private AspireMenuButton? _menuButton;

    [Inject]
    public required ResourceMenuBuilder ResourceMenuBuilder { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlLoc { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Parameter]
    public required EventCallback<CommandViewModel> CommandSelected { get; set; }

    [Parameter]
    public required Func<ResourceViewModel, CommandViewModel, bool> IsCommandExecuting { get; set; }

    [Parameter]
    public required EventCallback<string?> OnViewDetails { get; set; }

    [Parameter]
    public required ResourceViewModel Resource { get; set; }

    [Parameter]
    public required int MaxHighlightedCount { get; set; }

    [Parameter]
    public required ConcurrentDictionary<string, ResourceViewModel> ResourceByName { get; set; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    private readonly List<CommandViewModel> _highlightedCommands = new();
    private readonly List<MenuButtonItem> _menuItems = new();

    protected override void OnParametersSet()
    {
        _menuItems.Clear();
        _highlightedCommands.Clear();

        ResourceMenuBuilder.AddMenuItems(
            _menuItems,
            Resource,
            ResourceByName,
            EventCallback.Factory.Create(this, () => OnViewDetails.InvokeAsync(_menuButton?.MenuButtonId)),
            CommandSelected,
            IsCommandExecuting,
            showViewDetails: true,
            showConsoleLogsItem: true,
            showUrls: false);

        // If display is desktop then we display highlighted commands next to the ... button.
        if (ViewportInformation.IsDesktop)
        {
            _highlightedCommands.AddRange(Resource.Commands.Where(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden).Take(MaxHighlightedCount));
        }
    }
}
