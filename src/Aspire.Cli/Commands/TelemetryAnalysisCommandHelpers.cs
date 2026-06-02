// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Resources;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.ConsoleLogs;

namespace Aspire.Cli.Commands;

internal enum SpanStatsGroupBy
{
    Name,
    Resource
}

internal sealed record TelemetryTraceInfo(
    string TraceId,
    string Name,
    string ResourceName,
    int SpanCount,
    bool HasError,
    TimeSpan Duration,
    ulong? StartTimeUnixNano);

internal sealed record TelemetrySummaryOutput(
    int ResourceCount,
    int TraceCount,
    int SpanCount,
    int ErrorTraceCount,
    int ErrorSpanCount,
    double AverageDurationMs,
    double P50DurationMs,
    double P95DurationMs,
    double P99DurationMs,
    double MaxDurationMs);

internal sealed record TelemetrySlowTraceOutput(
    string TraceId,
    string Name,
    string Resource,
    int SpanCount,
    bool HasError,
    double DurationMs,
    string? Timestamp,
    string? DashboardUrl);

internal sealed record TelemetryWallTimeOutput(
    string TraceId,
    string Name,
    string Resource,
    int SpanCount,
    bool HasError,
    double WallClockMs,
    double SpanSumMs,
    double CoveredMs,
    double GapMs,
    double OverlapMs,
    double SpanSumToWallRatio,
    string? Timestamp,
    string? DashboardUrl);

internal sealed record TelemetrySpanStatsOutput(
    string Group,
    int Count,
    int ErrorCount,
    double AverageDurationMs,
    double P95DurationMs,
    double MaxDurationMs,
    double TotalDurationMs);

[JsonSerializable(typeof(TelemetrySummaryOutput))]
[JsonSerializable(typeof(TelemetrySlowTraceOutput[]))]
[JsonSerializable(typeof(TelemetryWallTimeOutput[]))]
[JsonSerializable(typeof(TelemetrySpanStatsOutput[]))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class TelemetryAnalysisJsonContext : JsonSerializerContext
{
    private static TelemetryAnalysisJsonContext? s_relaxedEscaping;

    public static TelemetryAnalysisJsonContext RelaxedEscaping => s_relaxedEscaping ??= new TelemetryAnalysisJsonContext(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        });
}

internal static class TelemetryAnalysisCommandHelpers
{
    internal static Option<FileInfo?> CreateFileOption() => new("--file")
    {
        Description = TelemetryCommandStrings.TelemetryArchiveOptionDescription
    };

    internal static Option<int> CreateTopOption(int defaultValue = 20) => new("--top")
    {
        Description = TelemetryCommandStrings.TopOptionDescription,
        DefaultValueFactory = _ => defaultValue
    };

    internal static CommandResult? ValidateInputOptions(FileInfo? archiveFile, FileInfo? appHost, string? dashboardUrl, string? apiKey)
    {
        if (archiveFile is not null && (appHost is not null || dashboardUrl is not null || apiKey is not null))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, TelemetryCommandStrings.FileAndLiveOptionsExclusive);
        }

        return null;
    }

    internal static CommandResult? ValidateTop(int top)
    {
        if (top < 1)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, TelemetryCommandStrings.TopMustBePositive);
        }

        return null;
    }

    internal static TelemetryTraceInfo[] GetTraceInfos(TelemetryAnalysisData data)
    {
        var traces = SharedAIHelpers.GetTracesFromOtlpData(data.ResourceSpans);
        return traces
            .Where(t => !string.IsNullOrEmpty(t.TraceId))
            .Select(t => CreateTraceInfo(t, data.AllResources))
            .OrderBy(t => t.StartTimeUnixNano ?? 0)
            .ToArray();
    }

    internal static TelemetrySummaryOutput CreateSummary(TelemetryAnalysisData data, TelemetryTraceInfo[] traceInfos)
    {
        var spans = SharedAIHelpers.GetSpanDtosFromOtlpData(data.ResourceSpans);
        var traceDurations = traceInfos.Select(t => t.Duration).OrderBy(d => d).ToArray();

        return new TelemetrySummaryOutput(
            ResourceCount: data.AllResources.Count,
            TraceCount: traceInfos.Length,
            SpanCount: spans.Count,
            ErrorTraceCount: traceInfos.Count(t => t.HasError),
            ErrorSpanCount: spans.Count(s => IsError(s.Span)),
            AverageDurationMs: ToMilliseconds(traceDurations.Length == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)traceDurations.Average(d => d.Ticks))),
            P50DurationMs: ToMilliseconds(GetPercentile(traceDurations, 50)),
            P95DurationMs: ToMilliseconds(GetPercentile(traceDurations, 95)),
            P99DurationMs: ToMilliseconds(GetPercentile(traceDurations, 99)),
            MaxDurationMs: ToMilliseconds(traceDurations.Length == 0 ? TimeSpan.Zero : traceDurations[^1]));
    }

    internal static TelemetrySlowTraceOutput[] CreateSlowTraceOutputs(TelemetryAnalysisData data, IEnumerable<TelemetryTraceInfo> traceInfos)
    {
        return traceInfos.Select(t => new TelemetrySlowTraceOutput(
            t.TraceId,
            t.Name,
            t.ResourceName,
            t.SpanCount,
            t.HasError,
            ToMilliseconds(t.Duration),
            FormatTimestamp(t.StartTimeUnixNano),
            GetDashboardTraceUrl(data.DashboardUrl, t.TraceId))).ToArray();
    }

    internal static TelemetryWallTimeOutput[] CreateWallTimeOutputs(TelemetryAnalysisData data)
    {
        var traces = SharedAIHelpers.GetTracesFromOtlpData(data.ResourceSpans);
        return traces
            .Where(t => !string.IsNullOrEmpty(t.TraceId))
            .Select(t => CreateWallTimeOutput(t, data.AllResources, data.DashboardUrl))
            .OrderByDescending(t => t.WallClockMs)
            .ThenByDescending(t => t.SpanSumMs)
            .ThenBy(t => t.TraceId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static TelemetrySpanStatsOutput[] CreateSpanStats(TelemetryAnalysisData data, SpanStatsGroupBy groupBy)
    {
        var spans = SharedAIHelpers.GetSpanDtosFromOtlpData(data.ResourceSpans);
        return spans
            .GroupBy(s => GetSpanGroupKey(s, data.AllResources, groupBy), StringComparer.OrdinalIgnoreCase)
            .Select(g => CreateSpanStatsOutput(g.Key, g.ToArray()))
            .OrderByDescending(s => s.TotalDurationMs)
            .ThenBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string FormatDuration(double milliseconds)
    {
        return TelemetryCommandHelpers.FormatDuration(TimeSpan.FromMilliseconds(milliseconds));
    }

    private static TelemetryTraceInfo CreateTraceInfo(OtlpTraceDto trace, IReadOnlyList<IOtlpResource> allResources)
    {
        var root = GetRootOrFirstSpan(trace);
        var intervals = GetSpanIntervals(trace);
        var duration = CalculateWallClockDuration(intervals);
        var start = intervals.Length == 0 ? (ulong?)null : intervals[0].StartTimeUnixNano;

        return new TelemetryTraceInfo(
            trace.TraceId,
            root?.Span.Name ?? "unknown",
            root is null ? "unknown" : OtlpHelpers.GetResourceName(root.Resource, allResources),
            trace.Spans.Count,
            trace.Spans.Any(s => IsError(s.Span)),
            duration,
            start);
    }

    private static TelemetryWallTimeOutput CreateWallTimeOutput(OtlpTraceDto trace, IReadOnlyList<IOtlpResource> allResources, string? dashboardUrl)
    {
        var traceInfo = CreateTraceInfo(trace, allResources);
        var intervals = GetSpanIntervals(trace);

        // Treat each span as an absolute OTLP interval:
        //   span A: start=0ms, end=100ms
        //   span B: start=25ms, end=75ms
        // The trace wall clock is the full envelope (100ms), covered time is the union of covered
        // intervals (100ms), and span sum is each span duration added together (150ms).
        // Gaps point at uncovered time inside the envelope; overlap points at nested or concurrent
        // spans that make the span sum exceed covered elapsed time.
        var wallClock = CalculateWallClockDuration(intervals);
        var spanSum = CalculateSpanSumDuration(intervals);
        var covered = CalculateCoveredDuration(intervals);
        var gap = wallClock > covered ? wallClock - covered : TimeSpan.Zero;
        var overlap = spanSum > covered ? spanSum - covered : TimeSpan.Zero;
        var spanSumToWallRatio = wallClock > TimeSpan.Zero ? spanSum.TotalMilliseconds / wallClock.TotalMilliseconds : 0;

        return new TelemetryWallTimeOutput(
            traceInfo.TraceId,
            traceInfo.Name,
            traceInfo.ResourceName,
            traceInfo.SpanCount,
            traceInfo.HasError,
            ToMilliseconds(wallClock),
            ToMilliseconds(spanSum),
            ToMilliseconds(covered),
            ToMilliseconds(gap),
            ToMilliseconds(overlap),
            Math.Round(spanSumToWallRatio, 3),
            FormatTimestamp(traceInfo.StartTimeUnixNano),
            GetDashboardTraceUrl(dashboardUrl, traceInfo.TraceId));
    }

    private static OtlpSpanDto? GetRootOrFirstSpan(OtlpTraceDto trace)
    {
        var spanIds = trace.Spans
            .Select(s => s.Span.SpanId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);

        return trace.Spans
            .Where(s => string.IsNullOrEmpty(s.Span.ParentSpanId) || !spanIds.Contains(s.Span.ParentSpanId))
            .OrderBy(s => s.Span.StartTimeUnixNano ?? ulong.MaxValue)
            .FirstOrDefault()
            ?? trace.Spans.OrderBy(s => s.Span.StartTimeUnixNano ?? ulong.MaxValue).FirstOrDefault();
    }

    private static TimeSpan CalculateWallClockDuration(SpanInterval[] intervals)
    {
        if (intervals.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var start = intervals[0].StartTimeUnixNano;
        var end = intervals.Max(i => i.EndTimeUnixNano);
        return end >= start ? OtlpHelpers.NanosecondsToTimeSpan(end - start) : TimeSpan.Zero;
    }

    private static TimeSpan CalculateSpanSumDuration(SpanInterval[] intervals)
    {
        var ticks = 0L;
        foreach (var interval in intervals)
        {
            ticks += OtlpHelpers.NanosecondsToTicks(interval.EndTimeUnixNano - interval.StartTimeUnixNano);
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan CalculateCoveredDuration(SpanInterval[] intervals)
    {
        if (intervals.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var coveredTicks = 0L;
        var currentStart = intervals[0].StartTimeUnixNano;
        var currentEnd = intervals[0].EndTimeUnixNano;

        foreach (var interval in intervals.Skip(1))
        {
            if (interval.StartTimeUnixNano > currentEnd)
            {
                coveredTicks += OtlpHelpers.NanosecondsToTicks(currentEnd - currentStart);
                currentStart = interval.StartTimeUnixNano;
                currentEnd = interval.EndTimeUnixNano;
                continue;
            }

            if (interval.EndTimeUnixNano > currentEnd)
            {
                currentEnd = interval.EndTimeUnixNano;
            }
        }

        coveredTicks += OtlpHelpers.NanosecondsToTicks(currentEnd - currentStart);
        return TimeSpan.FromTicks(coveredTicks);
    }

    private static SpanInterval[] GetSpanIntervals(OtlpTraceDto trace)
    {
        var intervals = new List<SpanInterval>();
        foreach (var span in trace.Spans)
        {
            var start = span.Span.StartTimeUnixNano;
            var end = span.Span.EndTimeUnixNano;
            if (start.HasValue && end.HasValue && end.Value >= start.Value)
            {
                intervals.Add(new SpanInterval(start.Value, end.Value));
            }
        }

        return intervals
            .OrderBy(i => i.StartTimeUnixNano)
            .ThenBy(i => i.EndTimeUnixNano)
            .ToArray();
    }

    private static TelemetrySpanStatsOutput CreateSpanStatsOutput(string group, OtlpSpanDto[] spans)
    {
        var durations = spans
            .Select(s => OtlpHelpers.CalculateDuration(s.Span.StartTimeUnixNano, s.Span.EndTimeUnixNano))
            .OrderBy(d => d)
            .ToArray();
        var totalTicks = durations.Sum(d => d.Ticks);
        var average = durations.Length == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(totalTicks / durations.Length);

        return new TelemetrySpanStatsOutput(
            group,
            spans.Length,
            spans.Count(s => IsError(s.Span)),
            ToMilliseconds(average),
            ToMilliseconds(GetPercentile(durations, 95)),
            ToMilliseconds(durations.Length == 0 ? TimeSpan.Zero : durations[^1]),
            ToMilliseconds(TimeSpan.FromTicks(totalTicks)));
    }

    private static string GetSpanGroupKey(OtlpSpanDto span, IReadOnlyList<IOtlpResource> allResources, SpanStatsGroupBy groupBy)
    {
        return groupBy switch
        {
            SpanStatsGroupBy.Resource => OtlpHelpers.GetResourceName(span.Resource, allResources),
            _ => string.IsNullOrEmpty(span.Span.Name) ? "unknown" : span.Span.Name
        };
    }

    private static bool IsError(OtlpSpanJson span) => span.Status?.Code == 2;

    private static TimeSpan GetPercentile(TimeSpan[] sortedDurations, int percentile)
    {
        if (sortedDurations.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var index = (int)Math.Ceiling(percentile / 100d * sortedDurations.Length) - 1;
        index = Math.Clamp(index, 0, sortedDurations.Length - 1);
        return sortedDurations[index];
    }

    private static double ToMilliseconds(TimeSpan duration) => Math.Round(duration.TotalMilliseconds, 3);

    private static string? FormatTimestamp(ulong? startTimeUnixNano)
    {
        return startTimeUnixNano.HasValue
            ? OtlpHelpers.UnixNanoSecondsToDateTime(startTimeUnixNano.Value).ToString("O", CultureInfo.InvariantCulture)
            : null;
    }

    private static string? GetDashboardTraceUrl(string? dashboardUrl, string traceId)
    {
        return dashboardUrl is null ? null : DashboardUrls.CombineUrl(dashboardUrl, DashboardUrls.TraceDetailUrl(traceId));
    }

    private readonly record struct SpanInterval(ulong StartTimeUnixNano, ulong EndTimeUnixNano);
}
