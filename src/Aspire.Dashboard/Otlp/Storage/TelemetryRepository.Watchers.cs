// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Partial class containing push-based streaming (watcher) functionality.
/// </summary>
public sealed partial class TelemetryRepository
{
    // Watcher fields are defined in the main file:
    // private readonly object _watchersLock;
    // private List<SpanWatcher>? _spanWatchers;
    // private List<LogWatcher>? _logWatchers;

    /// <summary>
    /// Maximum number of items to fetch in the initial snapshot for streaming.
    /// Prevents unbounded memory usage on large repositories.
    /// </summary>
    private const int MaxWatcherSnapshotCount = 10000;

    /// <summary>
    /// Streams spans as they arrive using push-based delivery.
    /// Yields existing spans first, then new ones as they're added.
    /// Filtering (resource, traceId, hasError, telemetry filters, text fragments) is applied
    /// inside the repository before yielding.
    /// O(1) per new span instead of O(n) re-query.
    /// </summary>
    public async IAsyncEnumerable<OtlpSpan> WatchSpansAsync(
        WatchSpansRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create a bounded channel to receive pushed spans
        var channel = Channel.CreateBounded<OtlpSpan>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var watcher = new SpanWatcher(request, channel);

        // Register watcher FIRST to avoid race condition where spans could be
        // added between getting the snapshot and registering.
        lock (_watchersLock)
        {
            _spanWatchers ??= new List<SpanWatcher>();
            _spanWatchers.Add(watcher);
        }

        try
        {
            // Get existing spans directly using GetSpans, which applies all filters
            // (resource, traceId, hasError, telemetry filters, text fragments) at the query level.
            var existingSpans = GetSpans(new GetSpansRequest
            {
                ResourceKeys = request.ResourceKeys,
                StartIndex = 0,
                Count = MaxWatcherSnapshotCount,
                Filters = request.Filters,
                TraceId = request.TraceId,
                HasError = request.HasError,
                TextFragments = request.TextFragments
            });

            // Track seen span IDs to deduplicate spans that arrive during the snapshot read.
            // Race condition: watcher is registered BEFORE GetSpans, so spans arriving during
            // the snapshot read are pushed to the channel AND included in the snapshot.
            // Unlike logs (which have monotonically increasing InternalId), span IDs are random
            // hex strings, so we need a HashSet rather than a simple counter.
            // The HashSet is cleared after draining to prevent unbounded memory growth.
            var seenSpanIds = new HashSet<string>();

            // Sort spans by start time so streaming clients receive the initial snapshot in
            // chronological order. GetSpans returns spans grouped by trace (trace-order within
            // each trace), but spans from different traces can overlap in time.
            var orderedSpans = existingSpans.PagedResult.Items.OrderBy(s => s.StartTime);

            // Yield existing spans ordered by start time
            foreach (var span in orderedSpans)
            {
                seenSpanIds.Add(span.SpanId);
                yield return span;
            }

            // Drain any spans that arrived during the snapshot to ensure we don't miss them
            while (channel.Reader.TryRead(out var pendingSpan))
            {
                if (seenSpanIds.Add(pendingSpan.SpanId))
                {
                    yield return pendingSpan;
                }
            }

            // Clear the HashSet - spans arriving after the drain are guaranteed to be new
            // (they weren't in the snapshot taken earlier). This prevents unbounded memory growth.
            seenSpanIds.Clear();

            // Stream new spans as they're pushed (already filtered in PushSpansToWatchers)
            await foreach (var span in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return span;
            }
        }
        finally
        {
            // Clean up watcher
            lock (_watchersLock)
            {
                _spanWatchers?.Remove(watcher);
            }
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Streams logs as they arrive using push-based delivery.
    /// Yields existing logs first, then new ones as they're added.
    /// Filtering (resource, telemetry filters, text fragments) is applied
    /// inside the repository before yielding.
    /// O(1) per new log instead of O(n) re-query.
    /// </summary>
    public async IAsyncEnumerable<OtlpLogEntry> WatchLogsAsync(
        WatchLogsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Create a bounded channel to receive pushed logs
        var channel = Channel.CreateBounded<OtlpLogEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var watcher = new LogWatcher(request, channel);

        // Register watcher FIRST to avoid race condition where logs could be
        // added between getting the snapshot and registering.
        lock (_watchersLock)
        {
            _logWatchers ??= new List<LogWatcher>();
            _logWatchers.Add(watcher);
        }

        try
        {
            // Get existing logs snapshot (capped to prevent OOM)
            var existingLogs = GetLogs(new GetLogsContext
            {
                ResourceKeys = request.ResourceKeys,
                StartIndex = 0,
                Count = MaxWatcherSnapshotCount,
                Filters = request.Filters,
                TextFragments = request.TextFragments
            });

            // Track the highest log ID we've yielded to deduplicate
            long maxYieldedLogId = 0;

            // Yield existing logs
            foreach (var log in existingLogs.Items)
            {
                if (log.InternalId > maxYieldedLogId)
                {
                    maxYieldedLogId = log.InternalId;
                }
                yield return log;
            }

            // Drain any logs that arrived during the snapshot
            // Filters are already applied when pushing to channel
            while (channel.Reader.TryRead(out var pendingLog))
            {
                if (pendingLog.InternalId > maxYieldedLogId)
                {
                    maxYieldedLogId = pendingLog.InternalId;
                    yield return pendingLog;
                }
            }

            // Stream new logs as they're pushed, deduplicating by ID
            // Filters are already applied when pushing to channel
            await foreach (var log in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Skip if we already yielded this log in the initial batch
                if (log.InternalId <= maxYieldedLogId)
                {
                    continue;
                }

                maxYieldedLogId = log.InternalId;
                yield return log;
            }
        }
        finally
        {
            // Clean up watcher
            lock (_watchersLock)
            {
                _logWatchers?.Remove(watcher);
            }
            channel.Writer.TryComplete();
        }
    }

    private void PushSpansToWatchers(List<OtlpSpan> spans, ResourceKey resourceKey)
    {
        // Take a snapshot of watchers to avoid holding the lock while writing
        SpanWatcher[]? watchers;
        lock (_watchersLock)
        {
            if (_spanWatchers is null || _spanWatchers.Count == 0)
            {
                return;
            }
            watchers = _spanWatchers.ToArray();
        }

        foreach (var span in spans)
        {
            foreach (var watcher in watchers)
            {
                var request = watcher.Request;

                // Check if watcher is filtering by resource
                if (request.ResourceKeys is { Count: > 0 } keys && !keys.Contains(resourceKey))
                {
                    continue;
                }

                // Apply all watcher filters before pushing to channel
                if (!MatchesSpanCriteria(span, request.TraceId, request.HasError, request.Filters, request.TextFragments))
                {
                    continue;
                }

                // TryWrite is non-blocking - if channel is full, oldest item is dropped
                if (!watcher.Channel.Writer.TryWrite(span))
                {
                    _logger.LogWarning("Span watcher channel is full, dropping span {SpanId}. Consumer may be slow.", span.SpanId);
                }
            }
        }
    }

    private void PushLogsToWatchers(List<OtlpLogEntry> logs, ResourceKey resourceKey)
    {
        if (logs.Count == 0)
        {
            return;
        }

        // Take a snapshot of watchers to avoid holding the lock while writing
        LogWatcher[]? watchers;
        lock (_watchersLock)
        {
            if (_logWatchers is null || _logWatchers.Count == 0)
            {
                return;
            }
            watchers = _logWatchers.ToArray();
        }

        foreach (var log in logs)
        {
            foreach (var watcher in watchers)
            {
                var request = watcher.Request;

                // Check if watcher is filtering by resource
                if (request.ResourceKeys is { Count: > 0 } keys && !keys.Contains(resourceKey))
                {
                    continue;
                }

                // Apply watcher telemetry filters before pushing to channel
                if (request.Filters.Count > 0 && !MatchesFilters(log, request.Filters))
                {
                    continue;
                }

                // Apply text fragment matching
                if (request.TextFragments is { Length: > 0 } fragments && !MatchesLogTextFragments(log, fragments))
                {
                    continue;
                }

                // TryWrite is non-blocking - if channel is full, oldest item is dropped
                if (!watcher.Channel.Writer.TryWrite(log))
                {
                    _logger.LogWarning("Log watcher channel is full, dropping log {LogId}. Consumer may be slow.", log.InternalId);
                }
            }
        }
    }

    private static bool MatchesFilters(OtlpLogEntry log, List<TelemetryFilter> filters)
    {
        // Check if log passes all enabled filters
        // Apply filters returns items that match, so we use a single-item enumerable
        IEnumerable<OtlpLogEntry> result = [log];
        foreach (var filter in filters)
        {
            if (!filter.Enabled)
            {
                continue;
            }
            result = filter.Apply(result);
        }
        return result.Any();
    }

    private void DisposeWatchers()
    {
        // Complete all watcher channels to signal consumers to stop
        lock (_watchersLock)
        {
            if (_spanWatchers is not null)
            {
                foreach (var watcher in _spanWatchers)
                {
                    watcher.Channel.Writer.TryComplete();
                }
                _spanWatchers.Clear();
            }

            if (_logWatchers is not null)
            {
                foreach (var watcher in _logWatchers)
                {
                    watcher.Channel.Writer.TryComplete();
                }
                _logWatchers.Clear();
            }
        }
    }

    /// <summary>
    /// Represents a span watcher for push-based streaming.
    /// </summary>
    private sealed class SpanWatcher(WatchSpansRequest request, Channel<OtlpSpan> channel)
    {
        public WatchSpansRequest Request => request;
        public Channel<OtlpSpan> Channel => channel;
    }

    /// <summary>
    /// Represents a log watcher for push-based streaming.
    /// Includes filters to apply when pushing logs to the channel.
    /// </summary>
    private sealed class LogWatcher(WatchLogsRequest request, Channel<OtlpLogEntry> channel)
    {
        public WatchLogsRequest Request => request;
        public Channel<OtlpLogEntry> Channel => channel;
    }
}
