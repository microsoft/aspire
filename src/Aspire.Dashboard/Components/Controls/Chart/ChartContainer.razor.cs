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
    private static readonly TimeSpan s_dataFetchInterval = TimeSpan.FromSeconds(10);

    private OtlpInstrumentData? _instrument;
    private PeriodicTimer? _tickTimer;
    private Task? _tickTask;
    private IDisposable? _themeChangedSubscription;
    private readonly InstrumentViewModel _instrumentViewModel = new InstrumentViewModel();
    private (ResourceKey ResourceKey, string MeterName, string InstrumentName)? _dataEndTimeKey;
    private DateTimeOffset? _dataEndTime;

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

    [Parameter, EditorRequired]
    public required string? PauseText { get; set; }

    [Inject]
    private DashboardDataSource DataSource { get; set; } = null!;

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    [Inject]
    public required ILogger<ChartContainer> Logger { get; init; }

    [Inject]
    public required ThemeManager ThemeManager { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    public ImmutableList<DimensionFilterViewModel> DimensionFilters { get; set; } = [];
    public string? PreviousMeterName { get; set; }
    public string? PreviousInstrumentName { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await ThemeManager.EnsureInitializedAsync();

        if (!TelemetryRepository.IsReadOnly)
        {
            // Update the graph every 200ms. This displays the latest data and moves time forward.
            _tickTimer = new PeriodicTimer(s_chartUpdateInterval);
            _tickTask = Task.Run(UpdateDataAsync);
        }
        _themeChangedSubscription = ThemeManager.OnThemeChanged(async () =>
        {
            _instrumentViewModel.Theme = ThemeManager.EffectiveTheme;
            await InvokeAsync(StateHasChanged);
        });
    }

    public async ValueTask DisposeAsync()
    {
        _themeChangedSubscription?.Dispose();
        _tickTimer?.Dispose();

        // Wait for UpdateData to complete.
        if (_tickTask is { } t)
        {
            await t;
        }
    }

    private async Task UpdateDataAsync()
    {
        var timer = _tickTimer;
        long? lastDataFetchTimestamp = null;
        while (await timer!.WaitForNextTickAsync())
        {
            if (lastDataFetchTimestamp is null || Stopwatch.GetElapsedTime(lastDataFetchTimestamp.Value) >= s_dataFetchInterval)
            {
                _instrument = GetInstrument(useIncrementalCache: true);
                lastDataFetchTimestamp = Stopwatch.GetTimestamp();

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

    public async Task DimensionValuesChangedAsync(DimensionFilterViewModel dimensionViewModel)
    {
        _instrument = GetInstrument(useIncrementalCache: false);
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
        _instrument = GetInstrument(useIncrementalCache: false);

        if (_instrument == null)
        {
            return;
        }

        var hasInstrumentChanged = PreviousMeterName != MeterName || PreviousInstrumentName != InstrumentName;
        PreviousMeterName = MeterName;
        PreviousInstrumentName = InstrumentName;

        UpdateDimensionFilters(hasInstrumentChanged);

        await UpdateInstrumentDataAsync(_instrument);
    }

    private OtlpInstrumentData? GetInstrument(bool useIncrementalCache)
    {
        DateTime endDate;
        if (TelemetryRepository.IsReadOnly)
        {
            EnsureDataEndTime();
            endDate = _dataEndTime?.UtcDateTime ?? DateTime.UtcNow;
        }
        else
        {
            // When paused, use the paused time to keep the data window stable.
            // This ensures filter changes while paused still show the same data.
            endDate = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : DateTime.UtcNow;
        }

        var dataPointInterval = MetricDataPointInterval.Get(Duration);

        // Histogram graphs need one preceding rollup to calculate bucket count changes at the beginning of the chart.
        var historyDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, dataPointInterval.Ticks));
        var startDate = endDate.Subtract(Duration + historyDuration);
        var cursors = useIncrementalCache && _instrument is not null
            ? MetricInstrumentDataCache.CreateCursors(_instrument, historyDuration, dataPointInterval)
            : [];

        var refreshedInstrument = TelemetryRepository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = ResourceKey,
            MeterName = MeterName,
            InstrumentName = InstrumentName,
            StartTime = startDate,
            EndTime = endDate,
            DataPointInterval = dataPointInterval,
            PopulateExemplarAttributes = false,
            DimensionCursors = cursors,
            DimensionFilters = DimensionFilters
                .Where(filter => filter.AreAllValuesSelected is not true)
                .ToDictionary(
                filter => filter.Name,
                filter => (IReadOnlyList<string?>)filter.SelectedValues.Select(value => value.Value).ToArray())
        });

        if (refreshedInstrument == null)
        {
            Logger.LogDebug(
                "Unable to find instrument. ResourceKey: {ResourceKey}, MeterName: {MeterName}, InstrumentName: {InstrumentName}",
                ResourceKey,
                MeterName,
                InstrumentName);
        }

        return refreshedInstrument is not null && _instrument is not null && cursors.Count > 0
            ? MetricInstrumentDataCache.Merge(_instrument, refreshedInstrument, cursors, startDate)
            : refreshedInstrument;
    }

    private void EnsureDataEndTime()
    {
        var key = (ResourceKey, MeterName, InstrumentName);
        if (_dataEndTimeKey == key)
        {
            return;
        }

        var latestEndTime = TelemetryRepository.GetInstrumentLatestEndTime(ResourceKey, MeterName, InstrumentName);
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
                item.SelectedValues.Clear();

                if (hasInstrumentChanged)
                {
                    // Select all by default.
                    foreach (var v in item.Values)
                    {
                        item.SelectedValues.Add(v);
                    }
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

                        foreach (var v in newSelectedValues)
                        {
                            item.SelectedValues.Add(v);
                        }
                    }
                    else
                    {
                        // New filter. Select all by default.
                        foreach (var v in item.Values)
                        {
                            item.SelectedValues.Add(v);
                        }
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
