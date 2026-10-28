// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using MetricsResources = Aspire.Dashboard.Resources.Metrics;

namespace Aspire.Dashboard.Components;

/// <summary>
/// Shows instruments as cards with their latest value and a live sparkline,
/// so the user can scan a group at a glance and jump to the instrument worth investigating.
/// </summary>
public partial class MeterOverview : LiveSparklinesBase
{
    private static readonly Icon s_gaugeIcon = new Icons.Regular.Size16.Gauge();
    private static readonly Icon s_counterIcon = new Icons.Regular.Size16.DataTrending();
    private static readonly Icon s_histogramIcon = new Icons.Regular.Size16.DataHistogram();

    /// <summary>
    /// Gets or sets whether cards show their meter name, for groups mixing instruments from several meters.
    /// </summary>
    [Parameter, EditorRequired]
    public required bool ShowMeterName { get; set; }

    [Parameter, EditorRequired]
    public required Func<OtlpInstrumentSummary, string> GetInstrumentUrl { get; set; }

    /// <summary>
    /// Gets or sets the resource label shown as a pill on each card. Null hides it, for lists of a single resource.
    /// </summary>
    [Parameter]
    public Func<OtlpInstrumentSummary, string>? GetResourceLabel { get; set; }

    private string? GetInstrumentTypeName(OtlpInstrumentType type) => type switch
    {
        OtlpInstrumentType.Gauge => Loc[nameof(MetricsResources.MetricsInstrumentTypeGauge)].Value,
        OtlpInstrumentType.Sum => Loc[nameof(MetricsResources.MetricsInstrumentTypeCounter)].Value,
        OtlpInstrumentType.Histogram => Loc[nameof(MetricsResources.MetricsInstrumentTypeHistogram)].Value,
        _ => null
    };

    private static Icon? GetInstrumentIcon(OtlpInstrumentType type)
    {
        return type switch
        {
            OtlpInstrumentType.Gauge => s_gaugeIcon,
            OtlpInstrumentType.Sum => s_counterIcon,
            OtlpInstrumentType.Histogram => s_histogramIcon,
            _ => null
        };
    }
}
