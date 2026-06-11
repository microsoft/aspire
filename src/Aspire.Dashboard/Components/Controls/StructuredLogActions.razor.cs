// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public partial class StructuredLogActions : ComponentBase
{
    private AspireMenuButton? _menuButton;

    [Inject]
    public required StructuredLogMenuBuilder StructuredLogMenuBuilder { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Parameter]
    public required EventCallback<string> OnViewDetails { get; set; }

    [Parameter]
    public required OtlpLogEntry LogEntry { get; set; }

    private readonly List<MenuButtonItem> _menuItems = new();

    protected override void OnParametersSet()
    {
        _menuItems.Clear();

        StructuredLogMenuBuilder.AddMenuItems(
            _menuItems,
            LogEntry,
            EventCallback.Factory.Create(this, () => OnViewDetails.InvokeAsync(_menuButton?.MenuButtonId)));
    }
}
