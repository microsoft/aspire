// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// The number of items in an overview card, such as "10 of 24" when the card shows the first items of a long list.
/// </summary>
public sealed partial class OverviewCardCount : ComponentBase
{
    [Parameter]
    public int Shown { get; set; }

    [Parameter]
    public int Total { get; set; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }
}
