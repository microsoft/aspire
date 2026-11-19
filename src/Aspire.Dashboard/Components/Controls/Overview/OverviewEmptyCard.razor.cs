// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// An overview card without content. It's hidden while the overview hides empty panels.
/// </summary>
public sealed partial class OverviewEmptyCard : ComponentBase
{
    [Parameter, EditorRequired]
    public required OverviewCard Card { get; set; }

    [Parameter, EditorRequired]
    public required OverviewCardLayout Layout { get; set; }

    [Parameter, EditorRequired]
    public required string TitleId { get; set; }

    [Parameter, EditorRequired]
    public required Icon Icon { get; set; }

    [Parameter, EditorRequired]
    public required string Title { get; set; }

    [Parameter, EditorRequired]
    public required string Message { get; set; }

    [Parameter]
    public bool Hidden { get; set; }
}
