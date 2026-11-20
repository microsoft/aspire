// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Summarizes a resource's traces: the total and failed counts, and the most recent traces.
/// </summary>
public sealed partial class OverviewTracesCard : ComponentBase
{
    /// <summary>
    /// Gets or sets the name the traces page selects the resource by.
    /// </summary>
    [Parameter, EditorRequired]
    public required string ResourceName { get; set; }

    [Parameter, EditorRequired]
    public required OverviewCardLayout Layout { get; set; }

    /// <summary>
    /// Gets or sets whether the card shows the resource's traces. Otherwise it's an empty card.
    /// </summary>
    [Parameter]
    public bool HasContent { get; set; }

    /// <summary>
    /// Gets or sets whether the telemetry has loaded. Until then the card shows a loading placeholder.
    /// </summary>
    [Parameter]
    public bool IsLoaded { get; set; }

    [Parameter]
    public OverviewTelemetry? Telemetry { get; set; }

    [Parameter]
    public bool HideEmptyPanels { get; set; }

    // Trace links keep the current selection, so the resource header stays the same while viewing a trace.
    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }
}
