// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model;

public class TracesViewModel
{
    private readonly TelemetryRepository _telemetryRepository;
    private readonly List<FieldTelemetryFilter> _filters = new();

    private PagedResult<OtlpTrace>? _traces;
    private ResourceKey? _resourceKey;
    private string _filterText = string.Empty;
    private int _startIndex;
    private int _count;
    private bool _currentDataHasErrors;
    private SpanType? _spanType;

    public TracesViewModel(TelemetryRepository telemetryRepository)
    {
        _telemetryRepository = telemetryRepository;
    }

    public ResourceKey? ResourceKey { get => _resourceKey; set => SetValue(ref _resourceKey, value); }
    public SpanType? SpanType { get => _spanType; set => SetValue(ref _spanType, value); }
    public string FilterText { get => _filterText; set => SetValue(ref _filterText, value); }
    public int StartIndex { get => _startIndex; set => SetValue(ref _startIndex, value); }
    public int Count { get => _count; set => SetValue(ref _count, value); }
    public TimeSpan MaxDuration { get; private set; }
    public IReadOnlyList<FieldTelemetryFilter> Filters => _filters;

    public void ClearFilters()
    {
        _filters.Clear();
        _traces = null;
    }

    public void AddFilter(FieldTelemetryFilter filter)
    {
        // Don't add duplicate filters.
        foreach (var existingFilter in _filters)
        {
            if (existingFilter.Equals(filter))
            {
                return;
            }
        }

        _filters.Add(filter);
        _traces = null;
    }

    public bool RemoveFilter(FieldTelemetryFilter filter)
    {
        if (_filters.Remove(filter))
        {
            _traces = null;
            return true;
        }
        return false;
    }

    private void SetValue<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        _traces = null;
    }

    public PagedResult<OtlpTrace> GetTraces()
    {
        var traces = _traces;
        if (traces == null)
        {
            var filters = GetFilters();

            var result = _telemetryRepository.GetTraces(new GetTracesRequest
            {
                ResourceKey = ResourceKey,
                StartIndex = StartIndex,
                Count = Count,
                Filters = filters,
                TraceNameFilterText = FilterText
            });

            traces = result.PagedResult;
            MaxDuration = result.MaxDuration;

            _currentDataHasErrors = result.PagedResult.Items.Any(t => t.Spans.Any(s => s.Status == OtlpSpanStatusCode.Error));
        }

        return traces;
    }

    // First check if there were any errors in already available data. Avoid fetching data again.
    public bool HasErrors() => _currentDataHasErrors || GetErrorTraces(count: 0).TotalItemCount > 0;

    public PagedResult<OtlpTrace> GetErrorTraces(int count)
    {
        var filters = Filters.Cast<TelemetryFilter>().ToList();

        if (SpanType?.Filter is { } typeFilter)
        {
            filters.Add(typeFilter);
        }

        filters.Add(new FieldTelemetryFilter { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = OtlpSpanStatusCode.Error.ToString() });

        var errorTraces = _telemetryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = ResourceKey,
            StartIndex = 0,
            Count = count,
            Filters = filters,
            TraceNameFilterText = FilterText
        });

        return errorTraces.PagedResult;
    }

    private List<TelemetryFilter> GetFilters()
    {
        var filters = Filters.Cast<TelemetryFilter>().ToList();
        if (SpanType?.Filter is { } typeFilter)
        {
            filters.Add(typeFilter);
        }

        return filters;
    }

    public void ClearData()
    {
        _traces = null;
    }
}

