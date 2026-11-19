// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// The loading placeholder of an overview card that shows stats above a list, such as structured logs and traces.
/// </summary>
public sealed partial class OverviewListSkeleton : ComponentBase
{
    [Parameter, EditorRequired]
    public required int StatCount { get; set; }

    [Parameter, EditorRequired]
    public required int RowCount { get; set; }
}
