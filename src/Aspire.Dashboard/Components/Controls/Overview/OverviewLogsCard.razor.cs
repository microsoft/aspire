// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Summarizes a resource's structured logs: counts by level, and the most recent warnings and errors, or the most
/// recent logs when there are none.
/// </summary>
public sealed partial class OverviewLogsCard : ComponentBase
{
    /// <summary>
    /// Gets or sets the name the structured logs page selects the resource by.
    /// </summary>
    [Parameter, EditorRequired]
    public required string ResourceName { get; set; }

    [Parameter, EditorRequired]
    public required OverviewCardLayout Layout { get; set; }

    /// <summary>
    /// Gets or sets whether the card shows the resource's logs. Otherwise it's an empty card.
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

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }
}
