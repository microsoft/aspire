// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model;

public class StructuredLogsViewModel
{
    private readonly DashboardDataSource _dataSource;
    private readonly List<FieldTelemetryFilter> _filters = new();

    private PagedResult<LogSummary>? _logs;
    private ResourceKey? _resourceKey;
    private string _filterText = string.Empty;
    private int _logsStartIndex;
    private int _logsCount;
    private LogLevel? _logLevel;
    private bool _currentDataHasErrors;

    public StructuredLogsViewModel(DashboardDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public ResourceKey? ResourceKey { get => _resourceKey; set => SetValue(ref _resourceKey, value); }
    public string FilterText { get => _filterText; set => SetValue(ref _filterText, value); }
    public IReadOnlyList<FieldTelemetryFilter> Filters => _filters;

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

    public PagedResult<LogSummary> GetLogs()
    {
        var logs = _logs;
        if (logs == null)
        {
            var filters = GetFilters();

            logs = _dataSource.TelemetryRepository.GetLogSummaries(new GetLogsContext
            {
                ResourceKeys = ResourceKey is { } key ? [key] : [],
                StartIndex = StartIndex,
                Count = Count,
                Filters = filters
            });

            _currentDataHasErrors = logs.Items.Any(i => i.Severity >= Microsoft.Extensions.Logging.LogLevel.Error);
        }

        return logs;
    }

    public List<TelemetryFilter> GetFilters() => BuildFilters(Filters, FilterText, _logLevel);

    /// <summary>
    /// Builds the complete filter list from field filters, text filter, and log level.
    /// This is the single source of truth for structured log filtering logic.
    /// </summary>
    internal static List<TelemetryFilter> BuildFilters(IReadOnlyList<FieldTelemetryFilter> fieldFilters, string? filterText, LogLevel? logLevel)
    {
        var filters = fieldFilters.Cast<TelemetryFilter>().ToList();
        if (!string.IsNullOrWhiteSpace(filterText))
        {
            filters.Add(new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Message), Condition = FilterCondition.Contains, Value = filterText });
        }
        // If the log level is set and it is not the bottom level, which has no effect, then add a filter.
        if (logLevel != null && logLevel != Microsoft.Extensions.Logging.LogLevel.Trace)
        {
            filters.Add(new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = logLevel.Value.ToString() });
        }

        return filters;
    }

    public void ClearData()
    {
        _logs = null;
    }
}
