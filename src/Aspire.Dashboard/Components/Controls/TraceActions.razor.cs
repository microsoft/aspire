// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public partial class TraceActions : ComponentBase
{
    private AspireMenuButton? _menuButton;

    [Inject]
    public required TraceMenuBuilder TraceMenuBuilder { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Parameter]
    public required OtlpTrace Trace { get; set; }

    private readonly List<MenuButtonItem> _menuItems = new();

    protected override void OnParametersSet()
    {
        _menuItems.Clear();

        TraceMenuBuilder.AddMenuItems(_menuItems, Trace);
    }
}
