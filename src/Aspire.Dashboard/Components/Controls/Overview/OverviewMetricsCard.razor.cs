// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Summarizes a resource's metrics: its instrument and meter counts, and live tiles for its pinned instruments.
/// </summary>
public sealed partial class OverviewMetricsCard : ComponentBase
{
    private const int MetricsPreviewCount = 8;
    // Matches the metrics page's default duration, so a tile's sparkline looks like the card it opens.
    private static readonly TimeSpan s_metricsPreviewDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the name the metrics page selects the resource by.
    /// </summary>
    [Parameter, EditorRequired]
    public required string ResourceName { get; set; }

    /// <summary>
    /// Gets or sets the telemetry resource whose pinned instruments are shown as live tiles.
    /// </summary>
    [Parameter]
    public ResourceKey? ResourceKey { get; set; }

    [Parameter, EditorRequired]
    public required OverviewCardLayout Layout { get; set; }

    /// <summary>
    /// Gets or sets whether the card shows the resource's metrics. Otherwise it's an empty card.
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
    public required MetricPinStore PinStore { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }
}
