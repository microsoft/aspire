// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// The telemetry summarized by the structured logs, traces and metrics cards of an overview.
/// </summary>
public sealed class OverviewTelemetry
{
    private const int RecentItemCount = 5;

    public required int LogCount { get; init; }
    public required int ErrorLogCount { get; init; }
    public required int WarningLogCount { get; init; }
    public required List<LogSummary> RecentAttentionLogs { get; init; }
    public required List<LogSummary> RecentLogs { get; init; }
    public required int TraceCount { get; init; }
    public required int FailedTraceCount { get; init; }
    public required List<TraceSummary> RecentTraces { get; init; }
    public required List<OtlpInstrumentSummary> Instruments { get; init; }

    internal static async Task<OverviewTelemetry> LoadAsync(ITelemetryRepository repository, ResourceKey key, CancellationToken cancellationToken)
    {
        var allLogs = await repository.GetLogSummariesAsync(CreateLogsContext(key, [], 0, 0), cancellationToken).ConfigureAwait(false);
        List<TelemetryFilter> errorFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = nameof(LogLevel.Error) }];
        List<TelemetryFilter> warningFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.Equals, Value = nameof(LogLevel.Warning) }];
        List<TelemetryFilter> attentionFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = nameof(LogLevel.Warning) }];

        var errorLogs = await repository.GetLogSummariesAsync(CreateLogsContext(key, errorFilters, 0, 0), cancellationToken).ConfigureAwait(false);
        var warningLogs = await repository.GetLogSummariesAsync(CreateLogsContext(key, warningFilters, 0, 0), cancellationToken).ConfigureAwait(false);

        // Results are ordered oldest first, so read the last page to get the most recent entries.
        var attentionCount = errorLogs.TotalItemCount + warningLogs.TotalItemCount;
        var recentAttentionLogs = attentionCount > 0
            ? (await repository.GetLogSummariesAsync(CreateLogsContext(key, attentionFilters, Math.Max(0, attentionCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).Items
            : [];

        // A healthy resource has no warnings or errors to highlight, so show its latest logs instead of an empty card.
        var recentLogs = attentionCount == 0 && allLogs.TotalItemCount > 0
            ? (await repository.GetLogSummariesAsync(CreateLogsContext(key, [], Math.Max(0, allLogs.TotalItemCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).Items
            : [];

        var allTraces = await repository.GetTraceSummariesAsync(CreateTracesRequest(key, [], 0, 0), cancellationToken).ConfigureAwait(false);
        List<TelemetryFilter> failedTraceFilters = [new FieldTelemetryFilter { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = nameof(OtlpSpanStatusCode.Error) }];
        var failedTraces = await repository.GetTraceSummariesAsync(CreateTracesRequest(key, failedTraceFilters, 0, 0), cancellationToken).ConfigureAwait(false);
        var traceCount = allTraces.PagedResult.TotalItemCount;
        var recentTraces = traceCount > 0
            ? (await repository.GetTraceSummariesAsync(CreateTracesRequest(key, [], Math.Max(0, traceCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).PagedResult.Items
            : [];

        var instruments = repository.GetInstrumentSummaries(key)
            .OrderBy(i => i.Parent.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        return new OverviewTelemetry
        {
            LogCount = allLogs.TotalItemCount,
            ErrorLogCount = errorLogs.TotalItemCount,
            WarningLogCount = warningLogs.TotalItemCount,
            RecentAttentionLogs = recentAttentionLogs.AsEnumerable().Reverse().ToList(),
            RecentLogs = recentLogs.AsEnumerable().Reverse().ToList(),
            TraceCount = traceCount,
            FailedTraceCount = failedTraces.PagedResult.TotalItemCount,
            RecentTraces = recentTraces.AsEnumerable().Reverse().ToList(),
            Instruments = instruments
        };

        static GetLogsContext CreateLogsContext(ResourceKey key, List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = [key],
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };

        static GetTracesRequest CreateTracesRequest(ResourceKey key, List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = [key],
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };
    }
}
