// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Extensions;
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
    private static readonly FieldTelemetryFilter s_failedTraceFilter = new() { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = nameof(OtlpSpanStatusCode.Error) };

    internal static string SerializedFailedTraceFilter { get; } = TelemetryFilterFormatter.SerializeFiltersToString([s_failedTraceFilter]);

    public required int LogCount { get; init; }
    public required int ErrorLogCount { get; init; }
    public required int WarningLogCount { get; init; }
    public required List<OverviewLog> RecentAttentionLogs { get; init; }
    public required List<OverviewLog> RecentLogs { get; init; }
    public required int TraceCount { get; init; }
    public required int FailedTraceCount { get; init; }
    public required List<TraceSummary> RecentTraces { get; init; }
    public required List<OtlpInstrumentSummary> Instruments { get; init; }

    internal static async Task<OverviewTelemetry> LoadAsync(ITelemetryRepository repository, ResourceKey key, CancellationToken cancellationToken)
    {
        var logsTask = repository.GetOverviewLogsAsync([key], RecentItemCount, errorsOnly: false, cancellationToken);
        var tracesTask = repository.GetTraceSummariesAsync(CreateTracesRequest([], RecentItemCount), cancellationToken);
        var failedTracesTask = repository.GetTraceSummariesAsync(CreateTracesRequest([s_failedTraceFilter], 0), cancellationToken);
        await Task.WhenAll(logsTask, tracesTask, failedTracesTask).ConfigureAwait(false);
        var logs = await logsTask.ConfigureAwait(false);
        var traces = (await tracesTask.ConfigureAwait(false)).PagedResult;
        var failedTraces = (await failedTracesTask.ConfigureAwait(false)).PagedResult;

        var instruments = repository.GetInstrumentSummaries(key)
            .OrderBy(i => i.Parent.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        var hasAttentionLogs = logs.ErrorCount + logs.WarningCount > 0;
        return new OverviewTelemetry
        {
            LogCount = logs.TotalCount,
            ErrorLogCount = logs.ErrorCount,
            WarningLogCount = logs.WarningCount,
            RecentAttentionLogs = hasAttentionLogs ? logs.Recent : [],
            RecentLogs = hasAttentionLogs ? [] : logs.Recent,
            TraceCount = traces.TotalItemCount,
            FailedTraceCount = failedTraces.TotalItemCount,
            RecentTraces = traces.Items.AsEnumerable().Reverse().ToList(),
            Instruments = instruments
        };

        GetTracesRequest CreateTracesRequest(List<TelemetryFilter> filters, int count) => new()
        {
            ResourceKeys = [key],
            StartIndex = 0,
            Count = count,
            LatestItemCount = count,
            Filters = filters
        };
    }
}
