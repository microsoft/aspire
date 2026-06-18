// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Web;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Chart;

public abstract class ChartBase : ComponentBase, IAsyncDisposable
{
    private const int GraphPointCount = 30;

    private readonly CancellationTokenSource _cts = new();
    protected CancellationToken CancellationToken { get; private set; }

    private TimeSpan _tickDuration;
    private DateTimeOffset _lastUpdateTime;
    private DateTimeOffset _currentDataStartTime;
    private List<KeyValuePair<string, string>[]>? _renderedDimensionAttributes;
    private OtlpInstrumentKey? _renderedInstrument;
    private string? _renderedTheme;
    private bool _renderedShowCount;

    [Inject]
    public required IStringLocalizer<ControlsStrings> Loc { get; init; }

    [Inject]
    public required IInstrumentUnitResolver InstrumentUnitResolver { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Parameter, EditorRequired]
    public required InstrumentViewModel InstrumentViewModel { get; set; }

    [Parameter, EditorRequired]
    public required TimeSpan Duration { get; set; }

    [Parameter]
    public required List<OtlpResource> Resources { get; set; }

    // Stores a cache of the last set of spans returned as exemplars.
    // This dictionary is replaced each time the chart is updated.
    private Dictionary<SpanKey, OtlpSpan> _currentCache = new Dictionary<SpanKey, OtlpSpan>();
    private Dictionary<SpanKey, OtlpSpan> _newCache = new Dictionary<SpanKey, OtlpSpan>();

    protected override void OnInitialized()
    {
        // Copy the token so there is no chance it is accessed on CTS after it is disposed.
        CancellationToken = _cts.Token;
        _currentDataStartTime = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : GetCurrentDataTime();
        InstrumentViewModel.DataUpdateSubscriptions.Add(OnInstrumentDataUpdate);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (CancellationToken.IsCancellationRequested ||
            InstrumentViewModel.Instrument is null ||
            InstrumentViewModel.MatchedDimensions is null ||
            !ReadyForData())
        {
            return;
        }

        var inProgressDataTime = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : GetCurrentDataTime();

        // Only advance the time window when not paused. When paused, keep the chart's
        // time axis stable so filter changes don't cause the x-axis to jump.
        if (pausedAt is null)
        {
            while (_currentDataStartTime.Add(_tickDuration) < inProgressDataTime)
            {
                _currentDataStartTime = _currentDataStartTime.Add(_tickDuration);
            }
        }

        var dimensionAttributes = InstrumentViewModel.MatchedDimensions.Select(d => d.Attributes).ToList();
        if (_renderedInstrument is null || _renderedInstrument != InstrumentViewModel.Instrument.GetKey() ||
            _renderedDimensionAttributes is null || !_renderedDimensionAttributes.SequenceEqual(dimensionAttributes) ||
            _renderedTheme != InstrumentViewModel.Theme ||
            _renderedShowCount != InstrumentViewModel.ShowCount)
        {
            // Dimensions (or entire chart) has changed. Re-render the entire chart.
            _renderedInstrument = InstrumentViewModel.Instrument.GetKey();
            _renderedDimensionAttributes = dimensionAttributes;
            _renderedTheme = InstrumentViewModel.Theme;
            _renderedShowCount = InstrumentViewModel.ShowCount;
            await UpdateChartAsync(tickUpdate: false, inProgressDataTime).ConfigureAwait(false);
        }
        else if (_lastUpdateTime.Add(TimeSpan.FromSeconds(0.2)) < TimeProvider.GetUtcNow())
        {
            // Throttle how often the chart is updated.
            _lastUpdateTime = TimeProvider.GetUtcNow();
            await UpdateChartAsync(tickUpdate: true, inProgressDataTime).ConfigureAwait(false);
        }
    }

    protected override void OnParametersSet()
    {
        _tickDuration = Duration / GraphPointCount;
    }

    private Task OnInstrumentDataUpdate()
    {
        return InvokeAsync(StateHasChanged);
    }

    private string FormatTooltip(string name, double yValue, DateTimeOffset xValue)
    {
        return $"<b>{HttpUtility.HtmlEncode(InstrumentViewModel.Instrument?.Name)}</b><br />{HttpUtility.HtmlEncode(name)}: {FormatHelpers.FormatNumberWithOptionalDecimalPlaces(yValue, maxDecimalPlaces: 6, CultureInfo.CurrentCulture)}<br />Time: {FormatHelpers.FormatTime(TimeProvider, TimeProvider.ToLocal(xValue))}";
    }

    private async Task UpdateChartAsync(bool tickUpdate, DateTimeOffset inProgressDataTime)
    {
        // Unit comes from the instrument and they're not localized.
        // The hardcoded "Count" label isn't localized for consistency.
        const string CountUnit = "Count";

        Debug.Assert(InstrumentViewModel.MatchedDimensions != null);
        Debug.Assert(InstrumentViewModel.Instrument != null);

        var unit = !InstrumentViewModel.ShowCount
            ? GetDisplayedUnit(InstrumentViewModel.Instrument)
            : CountUnit;

        var calculator = new ChartDataCalculator(GraphPointCount, Duration);
        ChartData data;

        if (InstrumentViewModel.Instrument?.Type != OtlpInstrumentType.Histogram || InstrumentViewModel.ShowCount)
        {
            data = calculator.CalculateChartValues(InstrumentViewModel.MatchedDimensions, _currentDataStartTime, TimeProvider.ToLocalDateTimeOffset, unit);

            // TODO: Exemplars on non-histogram charts doesn't work well. Don't display for now.
            data.Exemplars.Clear();
        }
        else
        {
            data = calculator.CalculateHistogramValues(InstrumentViewModel.MatchedDimensions, _currentDataStartTime, TimeProvider.ToLocalDateTimeOffset, unit);
        }

        // Add tooltips to traces. The calculator produces values and diff values
        // but omits tooltips to keep it free of UI/localization concerns.
        // Tooltips are added before encoding trace names because FormatTooltip
        // encodes the name parameter internally.
        foreach (var trace in data.Traces)
        {
            for (var i = 0; i < data.XValues.Count; i++)
            {
                if (trace.Percentile is not null)
                {
                    // Histogram: show tooltip only when diff is positive.
                    if (trace.DiffValues[i] > 0)
                    {
                        trace.Tooltips.Add(FormatTooltip(trace.Name, trace.Values[i].GetValueOrDefault(), data.XValues[i]));
                    }
                    else
                    {
                        trace.Tooltips.Add(null);
                    }
                }
                else
                {
                    // Non-histogram: show tooltip when value exists.
                    if (trace.Values[i] is not null)
                    {
                        trace.Tooltips.Add(FormatTooltip(trace.Name, trace.Values[i]!.Value, data.XValues[i]));
                    }
                    else
                    {
                        trace.Tooltips.Add(null);
                    }
                }
            }

            // HTML-encode trace names because Plotly renders HTML in legend text.
            trace.Name = HttpUtility.HtmlEncode(trace.Name);
        }

        // Resolve exemplar spans using the telemetry repository and span cache.
        ResolveExemplarSpans(data.Exemplars);

        // Replace cache for next update.
        _currentCache = _newCache;
        _newCache = new Dictionary<SpanKey, OtlpSpan>();

        await OnChartUpdatedAsync(data.Traces, data.XValues, data.Exemplars, tickUpdate, inProgressDataTime, CancellationToken);
    }

    private void ResolveExemplarSpans(List<ChartExemplar> exemplars)
    {
        for (var i = 0; i < exemplars.Count; i++)
        {
            var exemplar = exemplars[i];
            var key = new SpanKey(exemplar.TraceId, exemplar.SpanId);

            // Try to find span in the local cache first.
            // This is done to avoid scanning a potentially large trace collection in repository.
            if (!_currentCache.TryGetValue(key, out var span))
            {
                span = TelemetryRepository.GetSpan(exemplar.TraceId, exemplar.SpanId);
            }
            if (span != null)
            {
                _newCache[key] = span;
                // ChartExemplar properties are init-only, so replace with a new instance.
                exemplars[i] = new ChartExemplar
                {
                    Start = exemplar.Start,
                    Value = exemplar.Value,
                    TraceId = exemplar.TraceId,
                    SpanId = exemplar.SpanId,
                    Span = span
                };
            }
        }
    }

    private DateTimeOffset GetCurrentDataTime()
    {
        return TimeProvider.GetUtcNow().Subtract(TimeSpan.FromSeconds(1)); // Compensate for delay in receiving metrics from services.
    }

    private string GetDisplayedUnit(OtlpInstrumentSummary instrument)
    {
        return InstrumentUnitResolver.ResolveDisplayedUnit(instrument, titleCase: true, pluralize: true);
    }

    protected abstract Task OnChartUpdatedAsync(List<ChartTrace> traces, List<DateTimeOffset> xValues, List<ChartExemplar> exemplars, bool tickUpdate, DateTimeOffset inProgressDataTime, CancellationToken cancellationToken);

    protected abstract bool ReadyForData();

    public ValueTask DisposeAsync() => DisposeAsync(disposing: true);

    protected virtual ValueTask DisposeAsync(bool disposing)
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
