// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartContainer : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan s_chartUpdateInterval = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan s_dataFetchInterval = TimeSpan.FromSeconds(1);

    private readonly object _instrumentUpdateLock = new();
    private OtlpInstrumentData? _instrument;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _tickTask;
    private IDisposable? _themeChangedSubscription;
    private readonly InstrumentViewModel _instrumentViewModel = new InstrumentViewModel();
    private (ResourceKey ResourceKey, string MeterName, string InstrumentName)? _dataEndTimeKey;
    private (ResourceKey ResourceKey, string MeterName, string InstrumentName, TimeSpan Duration)? _instrumentRequestKey;
    private DateTimeOffset? _dataEndTime;
    private long _lastDataFetchTimestamp = -1;
    private long _instrumentUpdateVersion;
    private int _disposed;

    [Parameter, EditorRequired]
    public required ResourceKey ResourceKey { get; set; }

    [Parameter, EditorRequired]
    public required string MeterName { get; set; }

    [Parameter, EditorRequired]
    public required string InstrumentName { get; set; }

    [Parameter, EditorRequired]
    public required TimeSpan Duration { get; set; }

    [Parameter, EditorRequired]
    public required Pages.Metrics.MetricViewKind ActiveView { get; set; }

    [Parameter, EditorRequired]
    public required Func<Pages.Metrics.MetricViewKind, Task> OnViewChangedAsync { get; set; }

    [Parameter, EditorRequired]
    public required List<OtlpResource> Resources { get; set; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    [Inject]
    public required ILogger<ChartContainer> Logger { get; init; }

    [Inject]
    public required ThemeManager ThemeManager { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required DashboardActivitySource DashboardActivitySource { get; init; }

    [Inject]
    public required TimeProvider TimeProvider { get; init; }

    public ImmutableList<DimensionFilterViewModel> DimensionFilters { get; set; } = [];
    public string? PreviousMeterName { get; set; }
    public string? PreviousInstrumentName { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await ThemeManager.EnsureInitializedAsync();

        if (!TelemetryRepository.IsReadOnly)
        {
            // Update the graph every 200ms. This displays the latest data and moves time forward.
            var cancellationToken = _disposeCts.Token;
            // Don't suppress the execution context because graph updates use the current culture for formatting.
            _tickTask = Task.Run(() => UpdateDataAsync(cancellationToken));
        }
        _themeChangedSubscription = ThemeManager.OnThemeChanged(async () =>
        {
            _instrumentViewModel.Theme = ThemeManager.EffectiveTheme;
            await InvokeAsync(StateHasChanged);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_instrumentUpdateLock)
        {
            _instrumentUpdateVersion++;
        }

        _themeChangedSubscription?.Dispose();
        _disposeCts.Cancel();

        // Wait for UpdateData to complete.
        if (_tickTask is { } t)
        {
            await t;
        }

        _disposeCts.Dispose();
    }

    private async Task UpdateDataAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_chartUpdateInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var lastDataFetchTimestamp = Volatile.Read(ref _lastDataFetchTimestamp);
                if (lastDataFetchTimestamp < 0 || TimeProvider.GetElapsedTime(lastDataFetchTimestamp) >= s_dataFetchInterval)
                {
                    using var activity = DashboardActivitySource.ActivitySource.StartActivity("Update metric chart data from tick");

                    var result = await GetInstrumentAsync(useIncrementalCache: true, cancellationToken).ConfigureAwait(false);
                    if (!TryPublishInstrument(result))
                    {
                        continue;
                    }

                    if (_instrument is not null && HaveDimensionFilterValuesChanged(_instrument))
                    {
                        await InvokeAsync(() =>
                        {
                            UpdateDimensionFilters(hasInstrumentChanged: false);
                            StateHasChanged();
                        });
                    }
                }

                if (_instrument == null || PauseManager.AreMetricsPaused(out _))
                {
                    continue;
                }

                await UpdateInstrumentDataAsync(_instrument);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error in UpdateDataAsync");
        }
    }

    public async Task DimensionValuesChangedAsync(DimensionFilterViewModel dimensionViewModel)
    {
        var result = await GetInstrumentAsync(useIncrementalCache: false, _disposeCts.Token).ConfigureAwait(false);
        if (!TryPublishInstrument(result))
        {
            return;
        }

        if (_instrument is null)
        {
            return;
        }

        await UpdateInstrumentDataAsync(_instrument);
    }

    private async Task UpdateInstrumentDataAsync(OtlpInstrumentData instrument)
    {
        // Only update data in plotly
        await _instrumentViewModel.UpdateDataAsync(instrument.Summary, instrument.Dimensions);
    }

    private async Task ShowCountChangedAsync(bool showCount)
    {
        if (_instrumentViewModel.ShowCount == showCount)
        {
            return;
        }

        _instrumentViewModel.ShowCount = showCount;
        if (_instrument is not null)
        {
            await UpdateInstrumentDataAsync(_instrument);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        var requestKey = (ResourceKey, MeterName, InstrumentName, Duration);
        if (_instrumentRequestKey != requestKey)
        {
            _instrumentRequestKey = requestKey;
            await RefreshChartAsync();
        }
    }

    private async Task RefreshChartAsync()
    {
        // Track the selection change before awaiting. hasInstrumentChanged describes the selected
        // parameters, not the fetched data, so it must not be lost when a concurrent tick wins the
        // version race below.
        var hasInstrumentChanged = PreviousMeterName != MeterName || PreviousInstrumentName != InstrumentName;
        PreviousMeterName = MeterName;
        PreviousInstrumentName = InstrumentName;

        var result = await GetInstrumentAsync(useIncrementalCache: false, _disposeCts.Token).ConfigureAwait(false);
        if (!TryPublishInstrument(result))
        {
            // A newer fetch owns _instrument now, but the dimension filters still belong to this
            // selection. The tick loop only ever calls UpdateDimensionFilters with
            // hasInstrumentChanged: false, which carries the previous instrument's selections forward by
            // attribute name, so returning here would leave the new instrument filtered by the old one.
            if (hasInstrumentChanged)
            {
                UpdateDimensionFilters(hasInstrumentChanged: true);
            }

            return;
        }

        if (_instrument == null)
        {
            return;
        }

        UpdateDimensionFilters(hasInstrumentChanged);

        await UpdateInstrumentDataAsync(_instrument);
    }

    private async Task<(long UpdateVersion, OtlpInstrumentData? Instrument)> GetInstrumentAsync(bool useIncrementalCache, CancellationToken cancellationToken)
    {
        long updateVersion;
        OtlpInstrumentData? baseInstrument;
        lock (_instrumentUpdateLock)
        {
            updateVersion = ++_instrumentUpdateVersion;

            // Capture the base instrument under the same lock that stamps the version. TryPublishInstrument
            // writes _instrument under this lock, so reading it outside would let an incremental refresh
            // merge the previously selected instrument's series into the newly selected one: the guard below
            // only proves no newer call started, not that the base snapshot still matches the selection.
            baseInstrument = _instrument;
        }

        var resourceKey = ResourceKey;
        var meterName = MeterName;
        var instrumentName = InstrumentName;
        var duration = Duration;
        var dimensionFilters = DimensionFilters
            .Where(filter => filter.AreAllValuesSelected is not true)
            .ToDictionary(
                filter => filter.Name,
                filter => (IReadOnlyList<string?>)filter.SelectedValues.Select(value => value.Value).ToArray());

        var instrumentSummary = TelemetryRepository.GetInstrumentSummary(resourceKey, meterName, instrumentName);
        if (instrumentSummary is null)
        {
            Logger.LogDebug(
                "Unable to find instrument. ResourceKey: {ResourceKey}, MeterName: {MeterName}, InstrumentName: {InstrumentName}",
                resourceKey,
                meterName,
                instrumentName);
            return (updateVersion, Instrument: null);
        }

        DateTime endDate;
        if (TelemetryRepository.IsReadOnly)
        {
            EnsureDataEndTime(resourceKey, meterName, instrumentName);
            endDate = _dataEndTime?.UtcDateTime ?? DateTime.UtcNow;
        }
        else
        {
            // When paused, use the paused time to keep the data window stable.
            // This ensures filter changes while paused still show the same data.
            endDate = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : DateTime.UtcNow;
        }

        var dataPointInterval = MetricDataPointInterval.Get(duration);
        var includeExemplars = instrumentSummary.Type == OtlpInstrumentType.Histogram;

        // Histogram graphs need one preceding rollup to calculate bucket count changes at the beginning of the chart.
        var historyDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, dataPointInterval.Ticks));
        var startDate = endDate.Subtract(duration + historyDuration);
        var cursors = useIncrementalCache && baseInstrument is not null
            ? MetricInstrumentDataCache.CreateCursors(baseInstrument, historyDuration, dataPointInterval)
            : [];

        var refreshedInstrument = await TelemetryRepository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = resourceKey,
            MeterName = meterName,
            InstrumentName = instrumentName,
            StartTime = startDate,
            EndTime = endDate,
            DataPointInterval = dataPointInterval,
            IncludeExemplars = includeExemplars,
            PopulateExemplarAttributes = false,
            DimensionCursors = cursors,
            DimensionFilters = dimensionFilters
        }, cancellationToken).ConfigureAwait(false);
        Debug.Assert(refreshedInstrument is not null);

        lock (_instrumentUpdateLock)
        {
            if (updateVersion != _instrumentUpdateVersion)
            {
                return (updateVersion, Instrument: null);
            }

            Volatile.Write(ref _lastDataFetchTimestamp, TimeProvider.GetTimestamp());
        }

        var instrument = baseInstrument is not null && cursors.Count > 0
            ? MetricInstrumentDataCache.Merge(baseInstrument, refreshedInstrument, cursors, startDate)
            : refreshedInstrument;
        return (updateVersion, instrument);
    }

    private bool TryPublishInstrument((long UpdateVersion, OtlpInstrumentData? Instrument) result)
    {
        lock (_instrumentUpdateLock)
        {
            if (result.UpdateVersion != _instrumentUpdateVersion)
            {
                return false;
            }

            _instrument = result.Instrument;
            return true;
        }
    }

    private void EnsureDataEndTime(ResourceKey resourceKey, string meterName, string instrumentName)
    {
        var key = (resourceKey, meterName, instrumentName);
        if (_dataEndTimeKey == key)
        {
            return;
        }

        var latestEndTime = TelemetryRepository.GetInstrumentLatestEndTime(resourceKey, meterName, instrumentName);
        _dataEndTime = latestEndTime is not null ? new DateTimeOffset(latestEndTime.Value) : null;
        _dataEndTimeKey = key;
    }

    private List<DimensionFilterViewModel> CreateUpdatedFilters(bool hasInstrumentChanged)
    {
        var filters = new List<DimensionFilterViewModel>();
        if (_instrument != null)
        {
            foreach (var item in _instrument.KnownAttributeValues.OrderBy(kvp => kvp.Key))
            {
                var dimensionModel = new DimensionFilterViewModel
                {
                    Name = item.Key
                };

                dimensionModel.Values.AddRange(item.Value.Select(v =>
                {
                    var text = v switch
                    {
                        null => Loc[nameof(ControlsStrings.LabelValueUnset)],
                        { Length: 0 } => Loc[nameof(ControlsStrings.LabelEmpty)],
                        _ => v
                    };
                    return new DimensionValueViewModel
                    {
                        Text = text,
                        Value = v,
                    };
                }));

                filters.Add(dimensionModel);
            }

            foreach (var item in filters)
            {
                if (hasInstrumentChanged)
                {
                    // Select all by default.
                    item.SetSelectedValues(item.Values);
                }
                else
                {
                    var existing = DimensionFilters.SingleOrDefault(m => m.Name == item.Name);
                    if (existing != null)
                    {
                        // Select previously selected.
                        // Automatically select new incoming values if existing values are all selected.
                        var newSelectedValues = (existing.AreAllValuesSelected ?? false)
                            ? item.Values
                            : item.Values.Where(newValue => existing.SelectedValues.Any(existingValue => existingValue.Value == newValue.Value));

                        item.SetSelectedValues(newSelectedValues);
                    }
                    else
                    {
                        // New filter. Select all by default.
                        item.SetSelectedValues(item.Values);
                    }
                }
            }
        }

        return filters;
    }

    private bool UpdateDimensionFilters(bool hasInstrumentChanged)
    {
        var updatedFilters = ImmutableList.Create(CollectionsMarshal.AsSpan(CreateUpdatedFilters(hasInstrumentChanged)));
        if (HaveSameDimensionFilterContent(DimensionFilters, updatedFilters))
        {
            return false;
        }

        // Filters can be accessed from a background task, so replace the immutable collection atomically.
        DimensionFilters = updatedFilters;
        return true;
    }

    private bool HaveDimensionFilterValuesChanged(OtlpInstrumentData instrument)
    {
        if (instrument.KnownAttributeValues.Count != DimensionFilters.Count)
        {
            return true;
        }

        var index = 0;
        foreach (var attribute in instrument.KnownAttributeValues.OrderBy(attribute => attribute.Key))
        {
            var filter = DimensionFilters[index++];
            if (filter.Name != attribute.Key ||
                !filter.Values.Select(value => value.Value).SequenceEqual(attribute.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HaveSameDimensionFilterContent(
        ImmutableList<DimensionFilterViewModel> currentFilters,
        ImmutableList<DimensionFilterViewModel> updatedFilters)
    {
        if (currentFilters.Count != updatedFilters.Count)
        {
            return false;
        }

        for (var filterIndex = 0; filterIndex < currentFilters.Count; filterIndex++)
        {
            var currentFilter = currentFilters[filterIndex];
            var updatedFilter = updatedFilters[filterIndex];
            if (currentFilter.Name != updatedFilter.Name || currentFilter.Values.Count != updatedFilter.Values.Count)
            {
                return false;
            }

            for (var valueIndex = 0; valueIndex < currentFilter.Values.Count; valueIndex++)
            {
                var currentValue = currentFilter.Values[valueIndex];
                var updatedValue = updatedFilter.Values[valueIndex];
                if (currentValue.Text != updatedValue.Text ||
                    currentValue.Value != updatedValue.Value ||
                    currentFilter.SelectedValues.Contains(currentValue) != updatedFilter.SelectedValues.Contains(updatedValue))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private Task OnTabChangeAsync(FluentTab newTab)
    {
        var id = newTab.Id?.Substring("tab-".Length);

        if (id is null
            || !Enum.TryParse(typeof(Pages.Metrics.MetricViewKind), id, out var o)
            || o is not Pages.Metrics.MetricViewKind viewKind)
        {
            return Task.CompletedTask;
        }

        return OnViewChangedAsync(viewKind);
    }
}
