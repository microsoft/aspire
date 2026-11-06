// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
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

    /// <summary>
    /// Gets or sets how cards are ordered. Null keeps the order of <see cref="LiveSparklinesBase.Instruments"/>.
    /// Cards without data always go last.
    /// </summary>
    [Parameter]
    public MetricSortBy? SortBy { get; set; }

    /// <summary>
    /// Gets or sets the most cards to render, after ordering. Null renders every instrument.
    /// </summary>
    [Parameter]
    public int? MaxCount { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private ElementReference _container;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _cardObserver;
    private DotNetObjectReference<MeterOverview>? _selfReference;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // The browser reports which cards are near the viewport, so only those keep refreshing. Without the report,
        // for example when the module fails to load, every card keeps refreshing.
        try
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", $"/{Assets["Components/Controls/Chart/MeterOverview.razor.js"]}");
            _selfReference = DotNetObjectReference.Create(this);
            _cardObserver = await _jsModule.InvokeAsync<IJSObjectReference>("observeCards", _container, _selfReference);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    [JSInvokable]
    public Task OnVisibleCardsChanged(string[] instrumentIds) => SetVisibleInstrumentsAsync(instrumentIds);

    protected override async ValueTask DisposeCoreAsync()
    {
        try
        {
            if (_cardObserver is not null)
            {
                await _cardObserver.InvokeVoidAsync("dispose");
                await _cardObserver.DisposeAsync();
            }
            if (_jsModule is not null)
            {
                await _jsModule.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the browser's observer with it.
        }

        _selfReference?.Dispose();
    }

    private IEnumerable<OtlpInstrumentSummary> GetOrderedInstruments()
    {
        // OrderBy is stable, so each key keeps the order of the keys before it.
        var ordered = Instruments.OrderBy(i => GetSparkline(i).LatestValue is null);
        string ResourceLabel(OtlpInstrumentSummary i) => GetResourceLabel?.Invoke(i) ?? string.Empty;

        IEnumerable<OtlpInstrumentSummary> sorted = SortBy switch
        {
            MetricSortBy.Instrument => ordered.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(ResourceLabel, StringComparer.OrdinalIgnoreCase),
            MetricSortBy.Resource => ordered.ThenBy(ResourceLabel, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            // Values of different units aren't comparable, but within a group they usually are, e.g. request counts.
            MetricSortBy.ValueDescending => ordered.ThenByDescending(i => GetSparkline(i).LatestValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            MetricSortBy.ValueAscending => ordered.ThenBy(i => GetSparkline(i).LatestValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => ordered
        };

        return MaxCount is { } maxCount ? sorted.Take(maxCount) : sorted;
    }

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

public enum MetricSortBy
{
    Instrument = 0,
    Resource = 1,
    ValueDescending = 3,
    ValueAscending = 4
}

public enum MetricGroupBy
{
    None,
    Resource,
    Meter,
    Instrument
}
