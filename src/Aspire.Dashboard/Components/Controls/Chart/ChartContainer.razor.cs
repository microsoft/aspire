// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartContainer : ComponentBase, IAsyncDisposable
{
    private OtlpInstrumentData? _instrument;
    private PeriodicTimer? _tickTimer;
    private Task? _tickTask;
    private IDisposable? _themeChangedSubscription;
    private int _renderedDimensionsCount;
    private readonly InstrumentViewModel _instrumentViewModel = new InstrumentViewModel();

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
    public required TelemetryRepository TelemetryRepository { get; init; }

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

        // Update the graph every 200ms. This displays the latest data and moves time forward.
        _tickTimer = new PeriodicTimer(TimeSpan.FromSeconds(0.2));
        _tickTask = Task.Run(UpdateDataAsync);
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
        while (await timer!.WaitForNextTickAsync())
        {
            _instrument = GetInstrument();
            if (_instrument == null || PauseManager.AreMetricsPaused(out _))
            {
                continue;
            }

            if (_instrument.Dimensions.Count > _renderedDimensionsCount)
            {
                // Re-render the entire control if the number of dimensions has changed.
                _renderedDimensionsCount = _instrument.Dimensions.Count;
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                await UpdateInstrumentDataAsync(_instrument);
            }
        }
    }

    public async Task DimensionValuesChangedAsync(DimensionFilterViewModel dimensionViewModel)
    {
        if (_instrument == null)
        {
            return;
        }

        await UpdateInstrumentDataAsync(_instrument);
    }

    private async Task UpdateInstrumentDataAsync(OtlpInstrumentData instrument)
    {
        var matchedDimensions = instrument.Dimensions.Where(MatchDimension).ToList();

        // Only update data in plotly
        await _instrumentViewModel.UpdateDataAsync(instrument.Summary, matchedDimensions);
    }

    private bool MatchDimension(DimensionScope dimension)
    {
        foreach (var dimensionFilter in DimensionFilters)
        {
            if (!MatchFilter(dimension.Attributes, dimensionFilter))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchFilter(KeyValuePair<string, string>[] attributes, DimensionFilterViewModel filter)
    {
        // No filter selected.
        if (!filter.SelectedValues.Any())
        {
            return false;
        }

        var value = OtlpHelpers.GetValue(attributes, filter.Name);
        foreach (var item in filter.SelectedValues)
        {
            if (item.Value == value)
            {
                return true;
            }
        }

        return false;
    }

    protected override async Task OnParametersSetAsync()
    {
        _instrument = GetInstrument();

        if (_instrument == null)
        {
            return;
        }

        var hasInstrumentChanged = PreviousMeterName != MeterName || PreviousInstrumentName != InstrumentName;
        PreviousMeterName = MeterName;
        PreviousInstrumentName = InstrumentName;

        // Replace filters collection on change. Filters can be accessed from a background task so it is immutable for thread safety.
        DimensionFilters = ImmutableList.Create(CollectionsMarshal.AsSpan(CreateUpdatedFilters(hasInstrumentChanged)));

        await UpdateInstrumentDataAsync(_instrument);
    }

    private OtlpInstrumentData? GetInstrument()
    {
        // When paused, use the paused time to keep the data window stable.
        // This ensures filter changes while paused still show the same data.
        var endDate = PauseManager.AreMetricsPaused(out var pausedAt) ? pausedAt.Value : DateTime.UtcNow;
        // Get more data than is being displayed. Histogram graph uses some historical data to calculate bucket counts.
        // It's ok to get more data than is needed here. An additional date filter is applied when building chart values.
        var startDate = endDate.Subtract(Duration + TimeSpan.FromSeconds(30));

        var instrument = TelemetryRepository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = ResourceKey,
            MeterName = MeterName,
            InstrumentName = InstrumentName,
            StartTime = startDate,
            EndTime = endDate,
        });

        if (instrument == null)
        {
            Logger.LogDebug(
                "Unable to find instrument. ResourceKey: {ResourceKey}, MeterName: {MeterName}, InstrumentName: {InstrumentName}",
                ResourceKey,
                MeterName,
                InstrumentName);
        }

        return instrument;
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
                        Value = v
                    };
                }).OrderBy(v => v.Text));

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
