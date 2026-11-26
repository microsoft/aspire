// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Long lists are truncated to keep overview cards short. The button at the bottom of a card shows how many rows are
/// hidden and expands the list, then collapses it again.
/// </summary>
public sealed partial class OverviewShowMoreButton : ComponentBase
{
    [Parameter]
    public int Total { get; set; }

    [Parameter, EditorRequired]
    public required int PreviewCount { get; set; }

    [Parameter]
    public bool Expanded { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }
}
