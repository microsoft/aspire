// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

/// <summary>
/// Loads a live sparkline and the latest value for each instrument of a list, and keeps them up to date.
/// Derived components only decide how to present them, e.g. the metrics page's cards or the resource overview's tiles.
/// </summary>
public abstract class LiveSparklinesBase : ComponentBase, IAsyncDisposable
{
    // Sparklines show the trend shape, not detail, so fewer points than the full chart is enough.
    private const int SparklinePointCount = 30;
    internal const double SparklineWidth = 100;
    internal const double SparklineHeight = 32;

    // Sparklines refresh less often than the main chart: a meter can have dozens of instruments
    // and each refresh queries the telemetry store once per instrument.
    private static readonly TimeSpan s_refreshInterval = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _disposeCts = new();
    // Copied from the CTS so it stays readable after the CTS is disposed. A parameter update can
    // race with disposal when the user switches resource, and CancellationTokenSource.Token throws
    // ObjectDisposedException once disposed.
    private readonly CancellationToken _disposeToken;
    private Dictionary<(ResourceKey, OtlpInstrumentKey), SparklineData> _sparklines = new();
    private Task? _refreshTask;
    private (ResourceKey?, TimeSpan, string)? _loadedParameters;

    // Refreshes run one at a time: the periodic refresh and a refresh of cards scrolled into view each replace the
    // whole dictionary, and running them together would drop one of their updates.
    // It isn't disposed: a refresh started by a parameter change can still hold it when the component is disposed, and
    // a SemaphoreSlim that never created a wait handle has nothing to release.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // The instruments shown near the viewport, or null when the derived component doesn't report them, in which case
    // every instrument is refreshed. A long list refreshes only these: a query per instrument every few seconds for
    // hundreds of cards keeps the server busy for cards nobody can see.
    private HashSet<(ResourceKey, OtlpInstrumentKey)>? _visibleInstruments;

    // Instruments a periodic refresh skipped because they were off screen. They're refreshed as soon as they're shown.
    private readonly HashSet<(ResourceKey, OtlpInstrumentKey)> _staleInstruments = new();

    /// <summary>
    /// Gets or sets the resource the instruments belong to. When null, each instrument is read from the resource it
    /// came from, so one list can show the instruments of several resources.
    /// </summary>
    [Parameter]
    public ResourceKey? ResourceKey { get; set; }

    [Parameter, EditorRequired]
    public required IReadOnlyList<OtlpInstrumentSummary> Instruments { get; set; }

    [Parameter, EditorRequired]
    public required TimeSpan Duration { get; set; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required IInstrumentUnitResolver InstrumentUnitResolver { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required ILogger<LiveSparklinesBase> Logger { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    private ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    protected LiveSparklinesBase()
    {
        _disposeToken = _disposeCts.Token;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_disposeToken.IsCancellationRequested)
        {
            return;
        }

        // Compare the instruments themselves, not just their count: unpinning one instrument and pinning another
        // keeps the pinned group's count but changes which sparklines need data.
        var parameters = (ResourceKey, Duration, string.Join('\n', Instruments.Select(i => $"{GetResourceKey(i)}\t{i.Parent.Name}\t{i.Name}")));
        if (_loadedParameters != parameters)
        {
            _loadedParameters = parameters;
            try
            {
                // Every instrument is loaded once, even off screen: the list is ordered by the instruments' values.
                await RefreshAsync(onlyVisible: false, _disposeToken);
            }
            catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
            {
                return;
            }
        }

        _refreshTask ??= RefreshPeriodicallyAsync(_disposeToken);
    }

    protected ResourceKey GetResourceKey(OtlpInstrumentSummary instrument) => ResourceKey ?? instrument.ResourceView.ResourceKey;

    /// <summary>
    /// Gets a key for the instrument that's unique in the list, for a derived component to identify its cards.
    /// </summary>
    protected string GetInstrumentId(OtlpInstrumentSummary instrument)
    {
        var resourceKey = GetResourceKey(instrument);
        return $"{resourceKey.Name}\n{resourceKey.InstanceId}\n{instrument.Parent.Name}\n{instrument.Name}";
    }

    /// <summary>
    /// Sets the instruments shown near the viewport, identified by <see cref="GetInstrumentId"/>. Later periodic
    /// refreshes skip the others, and shown instruments that missed a refresh are refreshed now.
    /// </summary>
    protected async Task SetVisibleInstrumentsAsync(IEnumerable<string> instrumentIds)
    {
        var ids = instrumentIds.ToHashSet(StringComparer.Ordinal);
        var visible = Instruments
            .Where(i => ids.Contains(GetInstrumentId(i)))
            .Select(i => (GetResourceKey(i), i.GetKey()))
            .ToHashSet();
        _visibleInstruments = visible;

        var toRefresh = Instruments.Where(i => _staleInstruments.Contains((GetResourceKey(i), i.GetKey()))).Where(i => visible.Contains((GetResourceKey(i), i.GetKey()))).ToList();
        if (toRefresh.Count == 0 || _disposeToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await RefreshInstrumentsAsync(toRefresh, replaceAll: false, _disposeToken);
        }
        catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested)
        {
            return;
        }

        await InvokeAsync(StateHasChanged);
    }

    protected SparklineData GetSparkline(OtlpInstrumentSummary instrument)
    {
        return _sparklines.TryGetValue((GetResourceKey(instrument), instrument.GetKey()), out var data) ? data : SparklineData.Empty;
    }

    protected string FormatUnit(OtlpInstrumentSummary instrument, double value)
    {
        return InstrumentUnitResolver.ResolveDisplayedUnit(instrument, titleCase: false, pluralize: value != 1);
    }

    private async Task RefreshPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_refreshInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Read-only telemetry doesn't change, and paused metrics should stay frozen.
                if (TelemetryRepository.IsReadOnly || PauseManager.AreMetricsPaused(out _))
                {
                    continue;
                }

                await RefreshAsync(onlyVisible: true, cancellationToken);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Keep a background failure from surfacing as an unobserved error during disposal.
            Logger.LogError(ex, "Unexpected error refreshing metric sparklines.");
        }
    }

    private Task RefreshAsync(bool onlyVisible, CancellationToken cancellationToken)
    {
        if (!onlyVisible || _visibleInstruments is not { } visible)
        {
            return RefreshInstrumentsAsync(Instruments, replaceAll: true, cancellationToken);
        }

        var shown = new List<OtlpInstrumentSummary>();
        foreach (var instrument in Instruments)
        {
            var key = (GetResourceKey(instrument), instrument.GetKey());
            if (visible.Contains(key))
            {
                shown.Add(instrument);
            }
            else
            {
                _staleInstruments.Add(key);
            }
        }

        return RefreshInstrumentsAsync(shown, replaceAll: false, cancellationToken);
    }

    /// <summary>
    /// Loads the sparklines of the given instruments. <paramref name="replaceAll"/> drops the sparklines of every other
    /// instrument, for when the given instruments are the whole list.
    /// </summary>
    private async Task RefreshInstrumentsAsync(IReadOnlyList<OtlpInstrumentSummary> instruments, bool replaceAll, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var refreshed = new List<((ResourceKey, OtlpInstrumentKey) Key, SparklineData Data)>(instruments.Count);
            foreach (var instrument in instruments)
            {
                refreshed.Add(((GetResourceKey(instrument), instrument.GetKey()), await GetSparklineDataAsync(instrument, cancellationToken)));
            }

            // Swap the whole dictionary so a render never sees a partially refreshed set of sparklines.
            var sparklines = replaceAll ? new Dictionary<(ResourceKey, OtlpInstrumentKey), SparklineData>() : new Dictionary<(ResourceKey, OtlpInstrumentKey), SparklineData>(_sparklines);
            if (replaceAll)
            {
                _staleInstruments.Clear();
            }
            foreach (var (key, data) in refreshed)
            {
                sparklines[key] = data;
                _staleInstruments.Remove(key);
            }
            _sparklines = sparklines;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<SparklineData> GetSparklineDataAsync(OtlpInstrumentSummary instrument, CancellationToken cancellationToken)
    {
        DateTime endTime;
        if (TelemetryRepository.IsReadOnly)
        {
            // Imported telemetry is historical, so anchor each sparkline to that instrument's last data.
            var latestEndTime = TelemetryRepository.GetInstrumentLatestEndTime(GetResourceKey(instrument), instrument.Parent.Name, instrument.Name);
            if (latestEndTime is null)
            {
                return SparklineData.Empty;
            }
            endTime = latestEndTime.Value;
        }
        else
        {
            endTime = PauseManager.AreMetricsPaused(out var pausedAt)
                ? pausedAt.Value
                // Compensate for delay in receiving metrics from services, matching the main chart.
                : TimeProvider.GetUtcNow().UtcDateTime.Subtract(TimeSpan.FromSeconds(1));
        }

        var dataPointInterval = MetricDataPointInterval.Get(Duration);

        // Histograms need one preceding rollup to calculate bucket count changes at the start of the range.
        var historyDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, dataPointInterval.Ticks));
        var data = await TelemetryRepository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = GetResourceKey(instrument),
            MeterName = instrument.Parent.Name,
            InstrumentName = instrument.Name,
            StartTime = endTime.Subtract(Duration + historyDuration),
            EndTime = endTime,
            DataPointInterval = dataPointInterval,
            IncludeExemplars = false,
            PopulateExemplarAttributes = false
        }, cancellationToken).ConfigureAwait(false);

        if (data is null)
        {
            return SparklineData.Empty;
        }

        var calculator = new ChartDataCalculator(SparklinePointCount, Duration);
        var endTimeOffset = new DateTimeOffset(endTime, TimeSpan.Zero);
        var unit = InstrumentUnitResolver.ResolveDisplayedUnit(instrument, titleCase: true, pluralize: true);

        // Histograms show their median. It's the most representative single value for a
        // distribution, and the full chart is one click away for P90/P99.
        var chartData = instrument.Type == OtlpInstrumentType.Histogram
            ? calculator.CalculateHistogramValues(data.Dimensions, endTimeOffset, static t => t, unit)
            : calculator.CalculateChartValues(data.Dimensions, endTimeOffset, static t => t, unit);
        var trace = chartData.Traces.Single(t => t.Percentile is null or 50);

        // The calculator pads points past the end time so the full chart's line reaches the right
        // edge. Those buckets are still filling up (or empty), which shows as a misleading drop at
        // the end of a sparkline, so only keep points that end by the end time.
        var values = new List<double?>(trace.Values.Count);
        for (var i = 0; i < trace.Values.Count; i++)
        {
            if (chartData.XValues[i] <= endTimeOffset)
            {
                values.Add(trace.Values[i]);
            }
        }
        var latestValue = values.LastOrDefault(v => v is not null);
        if (latestValue is null)
        {
            return SparklineData.Empty;
        }

        // The form follows what the reader wants from each instrument type:
        // - Counters only ever grow, so a line of the running total says little. Bars of the
        //   increase per interval show the actual activity, e.g. requests per interval.
        // - Histograms are often sparse (no requests means no data), and a line drawn between rare
        //   points implies values that were never measured. A bar per interval with data doesn't.
        // - Gauges and up-down counters are levels, which is what a line shows best.
        if (ChartDataCalculator.IsMonotonicCounter(instrument))
        {
            // The increases share the chart's intervals, so the same trailing points are dropped.
            var increases = calculator.CalculateCounterIncreaseValues(data.Dimensions, endTimeOffset, instrument.AggregationTemporality)
                .Where((_, i) => chartData.XValues[i] <= endTimeOffset)
                .ToList();
            return new SparklineData(latestValue, SparklineKind.Bars, SparklinePath.BuildBars(increases, SparklineWidth, SparklineHeight), string.Empty);
        }

        if (instrument.Type == OtlpInstrumentType.Histogram)
        {
            return new SparklineData(latestValue, SparklineKind.Bars, SparklinePath.BuildBars(values, SparklineWidth, SparklineHeight), string.Empty);
        }

        var (line, area) = SparklinePath.Build(values, SparklineWidth, SparklineHeight);
        return new SparklineData(latestValue, SparklineKind.Line, line, area);
    }

    internal static string FormatCompactValue(double value, CultureInfo culture)
    {
        // Cards have limited room, so large values use a short suffix, e.g. 125300000 -> "125.3M".
        // Small values keep their decimals because durations such as 0.004 seconds are meaningful.
        var abs = Math.Abs(value);
        var (scaled, suffix) = abs switch
        {
            >= 1e12 => (value / 1e12, "T"),
            >= 1e9 => (value / 1e9, "B"),
            >= 1e6 => (value / 1e6, "M"),
            >= 1e4 => (value / 1e3, "K"),
            _ => (value, string.Empty)
        };

        return suffix.Length > 0
            ? FormatHelpers.FormatNumberWithOptionalDecimalPlaces(scaled, maxDecimalPlaces: 1, culture) + suffix
            : FormatHelpers.FormatNumberWithOptionalDecimalPlaces(scaled, maxDecimalPlaces: 3, culture);
    }

    /// <summary>
    /// Releases the derived component's resources, after the refreshes stopped.
    /// </summary>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        if (_refreshTask is { } task)
        {
            await task;
        }
        _disposeCts.Dispose();
        await DisposeCoreAsync();
    }
}

public enum SparklineKind
{
    Line,
    Bars
}

/// <summary>
/// A sparkline's SVG paths and the instrument's latest value. For lines, <see cref="Path"/> is the stroke and
/// <see cref="AreaPath"/> the fill below it. For bars, <see cref="Path"/> holds all bars.
/// </summary>
public sealed record SparklineData(double? LatestValue, SparklineKind Kind, string Path, string AreaPath)
{
    public static readonly SparklineData Empty = new(null, SparklineKind.Line, string.Empty, string.Empty);
}
