// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Model.GenAI;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model;

public class StructuredLogsViewModel
{
    private readonly TelemetryRepository _telemetryRepository;
    private readonly List<FieldTelemetryFilter> _filters = new();
    // Cache span lookups for GenAI attributes to avoid repeated lookups.
    private readonly ConcurrentDictionary<SpanKey, bool> _spanGenAICache = new();

    private PagedResult<OtlpLogEntry>? _logs;
    private ResourceKey? _resourceKey;
    private string _filterText = string.Empty;
    private int _logsStartIndex;
    private int _logsCount;
    private LogLevel? _logLevel;
    private bool _currentDataHasErrors;

    public StructuredLogsViewModel(TelemetryRepository telemetryRepository)
    {
        _telemetryRepository = telemetryRepository;
    }

    public ResourceKey? ResourceKey { get => _resourceKey; set => SetValue(ref _resourceKey, value); }
    public string FilterText { get => _filterText; set => SetValue(ref _filterText, value); }
    public IReadOnlyList<FieldTelemetryFilter> Filters => _filters;

    public bool HasGenAISpan(string traceId, string spanId)
    {
        // Get a flag indicating whether the span has GenAI telemetry on it.
        // This is cached to avoid repeated lookups. The cache is cleared when logs change.
        // It's ok that this isn't completely thread safe, i.e. get and a clear happen at the same time.

        var spanKey = new SpanKey(traceId, spanId);

        if (_spanGenAICache.TryGetValue(spanKey, out var value))
        {
            return value;
        }

        var span = _telemetryRepository.GetSpan(spanKey.TraceId, spanKey.SpanId);
        var hasGenAISpan = false;

        if (span != null)
        {
            // Only cache a value if a span is present.
            // We don't want to cache false if there is no span because the span may be added later.
            hasGenAISpan = GenAIHelpers.HasGenAIAttribute(span.Attributes);
            _spanGenAICache.TryAdd(spanKey, hasGenAISpan);
        }

        return hasGenAISpan;
    }

    public void ClearFilters()
    {
        _filters.Clear();
        ClearData();
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
        ClearData();
    }

    public bool RemoveFilter(FieldTelemetryFilter filter)
    {
        if (_filters.Remove(filter))
        {
            ClearData();
            return true;
        }
        return false;
    }

    public int StartIndex { get => _logsStartIndex; set => SetValue(ref _logsStartIndex, value); }
    public int Count { get => _logsCount; set => SetValue(ref _logsCount, value); }
    public LogLevel? LogLevel { get => _logLevel; set => SetValue(ref _logLevel, value); }

    private void SetValue<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        ClearData();
    }

    public PagedResult<OtlpLogEntry> GetLogs()
    {
        var logs = _logs;
        if (logs == null)
        {
            var filters = GetFilters();

            logs = _telemetryRepository.GetLogs(new GetLogsContext
            {
                ResourceKey = ResourceKey,
                StartIndex = StartIndex,
                Count = Count,
                Filters = filters
            });

            _currentDataHasErrors = logs.Items.Any(i => i.Severity >= Microsoft.Extensions.Logging.LogLevel.Error);
        }

        return logs;
    }

    public List<TelemetryFilter> GetFilters()
    {
        var filters = Filters.Cast<TelemetryFilter>().ToList();
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            filters.Add(new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Message), Condition = FilterCondition.Contains, Value = FilterText });
        }
        // If the log level is set and it is not the bottom level, which has no effect, then add a filter.
        if (_logLevel != null && _logLevel != Microsoft.Extensions.Logging.LogLevel.Trace)
        {
            filters.Add(new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = _logLevel.Value.ToString() });
        }

        return filters;
    }

    // First check if there were any errors in already available data. Avoid fetching data again.
    public bool HasErrors() => _currentDataHasErrors || GetErrorLogs(count: 0).TotalItemCount > 0;

    public PagedResult<OtlpLogEntry> GetErrorLogs(int count)
    {
        var filters = GetFilters();
        filters.RemoveAll(f => f is FieldTelemetryFilter fieldFilter && fieldFilter.Field == nameof(OtlpLogEntry.Severity));
        filters.Add(new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = Microsoft.Extensions.Logging.LogLevel.Error.ToString() });

        var errorLogs = _telemetryRepository.GetLogs(new GetLogsContext
        {
            ResourceKey = ResourceKey,
            StartIndex = 0,
            Count = count,
            Filters = filters
        });

        return errorLogs;
    }

    public void ClearData()
    {
        _logs = null;

        // Clear cache whenever log data changes to prevent it growing forever.
        _spanGenAICache.Clear();
    }
}
