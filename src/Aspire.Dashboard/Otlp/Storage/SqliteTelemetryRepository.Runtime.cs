// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int MaxWatcherSnapshotCount = 10_000;

    private readonly object _subscriptionLock = new();
    private readonly List<Subscription> _resourceSubscriptions = [];
    private readonly List<Subscription> _logSubscriptions = [];
    private readonly List<Subscription> _metricsSubscriptions = [];
    private readonly List<Subscription> _tracesSubscriptions = [];
    private readonly Dictionary<ResourceKey, int> _resourceUnviewedErrorLogs = [];
    private TimeSpan _subscriptionMinExecuteInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _watchersLock = new();
    private List<SpanWatcher>? _spanWatchers;
    private List<LogWatcher>? _logWatchers;

    internal TimeSpan SubscriptionMinExecuteInterval
    {
        set => _subscriptionMinExecuteInterval = value;
    }

    public bool HasDisplayedMaxLogLimitMessage { get; set; }
    public Message? MaxLogLimitMessage { get; set; }
    public bool HasDisplayedMaxTraceLimitMessage { get; set; }
    public Message? MaxTraceLimitMessage { get; set; }

    public Dictionary<ResourceKey, int> GetResourceUnviewedErrorLogsCount()
    {
        lock (_subscriptionLock)
        {
            return _resourceUnviewedErrorLogs.ToDictionary();
        }
    }

    public void MarkViewedErrorLogs(ResourceKey? key)
    {
        var changed = false;
        lock (_subscriptionLock)
        {
            if (key is null)
            {
                changed = _resourceUnviewedErrorLogs.Count > 0;
                _resourceUnviewedErrorLogs.Clear();
            }
            else if (key.Value.InstanceId is null)
            {
                changed = _resourceUnviewedErrorLogs.Keys
                    .Where(resourceKey => string.Equals(resourceKey.Name, key.Value.Name, StringComparisons.ResourceName))
                    .ToList()
                    .Aggregate(false, (removed, resourceKey) => _resourceUnviewedErrorLogs.Remove(resourceKey) || removed);
            }
            else
            {
                changed = _resourceUnviewedErrorLogs.Remove(key.Value);
            }
        }

        if (changed)
        {
            RaiseSubscriptionChanged(_logSubscriptions);
        }
    }

    private void ClearUnviewedErrorCounts(ResourceKey? key)
    {
        lock (_subscriptionLock)
        {
            if (key is null)
            {
                _resourceUnviewedErrorLogs.Clear();
                return;
            }

            foreach (var resourceKey in _resourceUnviewedErrorLogs.Keys
                .Where(resourceKey => key.Value.InstanceId is null
                    ? string.Equals(resourceKey.Name, key.Value.Name, StringComparisons.ResourceName)
                    : resourceKey == key.Value)
                .ToList())
            {
                _resourceUnviewedErrorLogs.Remove(resourceKey);
            }
        }
    }

    private void ClearUnviewedErrorCounts(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        lock (_subscriptionLock)
        {
            foreach (var resourceKey in _resourceUnviewedErrorLogs.Keys.ToList())
            {
                if (selectedResources.TryGetValue(resourceKey.GetCompositeName(), out var dataTypes) &&
                    (dataTypes.Contains(AspireDataType.StructuredLogs) || dataTypes.Contains(AspireDataType.Resource)))
                {
                    _resourceUnviewedErrorLogs.Remove(resourceKey);
                }
            }
        }
    }

    public Subscription OnNewResources(Func<Task> callback) =>
        AddSubscription(nameof(OnNewResources), null, SubscriptionType.Read, callback, _resourceSubscriptions);

    public Subscription OnNewLogs(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) =>
        AddSubscription(nameof(OnNewLogs), resourceKey, subscriptionType, callback, _logSubscriptions);

    public Subscription OnNewMetrics(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) =>
        AddSubscription(nameof(OnNewMetrics), resourceKey, subscriptionType, callback, _metricsSubscriptions);

    public Subscription OnNewTraces(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) =>
        AddSubscription(nameof(OnNewTraces), resourceKey, subscriptionType, callback, _tracesSubscriptions);

    private Subscription AddSubscription(
        string name,
        ResourceKey? resourceKey,
        SubscriptionType subscriptionType,
        Func<Task> callback,
        List<Subscription> subscriptions)
    {
        Subscription? subscription = null;
        subscription = new Subscription(name, resourceKey, subscriptionType, callback, () =>
        {
            lock (_subscriptionLock)
            {
                subscriptions.Remove(subscription!);
            }
        }, ExecutionContext.Capture(), _otlpContext.Logger, _subscriptionMinExecuteInterval);

        lock (_subscriptionLock)
        {
            subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void RaiseSubscriptionChanged(List<Subscription> subscriptions)
    {
        Subscription[] snapshot;
        lock (_subscriptionLock)
        {
            snapshot = subscriptions.ToArray();
        }

        foreach (var subscription in snapshot)
        {
            subscription.Execute();
        }
    }

    private void NotifyLogsAdded(List<OtlpLogEntry> logs)
    {
        lock (_subscriptionLock)
        {
            foreach (var log in logs)
            {
                if (!log.IsError || _logSubscriptions.Any(subscription =>
                    subscription.SubscriptionType == SubscriptionType.Read &&
                    (subscription.ResourceKey is null || subscription.ResourceKey == log.ResourceView.ResourceKey)))
                {
                    continue;
                }

                _resourceUnviewedErrorLogs.TryGetValue(log.ResourceView.ResourceKey, out var count);
                _resourceUnviewedErrorLogs[log.ResourceView.ResourceKey] = count + 1;
            }
        }

        PushLogsToWatchers(logs);
        RaiseSubscriptionChanged(_logSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    private void NotifySpansAdded(List<OtlpSpan> spans)
    {
        PushSpansToWatchers(spans);
        RaiseSubscriptionChanged(_tracesSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    private void NotifyPeersChanged()
    {
        RaiseSubscriptionChanged(_tracesSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    private void NotifyMetricsAdded()
    {
        RaiseSubscriptionChanged(_metricsSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public async IAsyncEnumerable<OtlpSpan> WatchSpansAsync(
        WatchSpansRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<OtlpSpan>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        var watcher = new SpanWatcher(request, channel);
        lock (_watchersLock)
        {
            _spanWatchers ??= [];
            _spanWatchers.Add(watcher);
        }

        try
        {
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
            var seenSpans = new HashSet<(string TraceId, string SpanId)>();
            foreach (var span in existingSpans.PagedResult.Items.OrderBy(span => span.StartTime))
            {
                seenSpans.Add((span.TraceId, span.SpanId));
                yield return span;
            }
            while (channel.Reader.TryRead(out var pendingSpan))
            {
                if (seenSpans.Add((pendingSpan.TraceId, pendingSpan.SpanId)))
                {
                    yield return pendingSpan;
                }
            }

            await foreach (var span in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return span;
            }
        }
        finally
        {
            lock (_watchersLock)
            {
                _spanWatchers?.Remove(watcher);
            }
            channel.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<OtlpLogEntry> WatchLogsAsync(
        WatchLogsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<OtlpLogEntry>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        var watcher = new LogWatcher(request, channel);
        lock (_watchersLock)
        {
            _logWatchers ??= [];
            _logWatchers.Add(watcher);
        }

        try
        {
            var existingLogs = GetLogs(new GetLogsContext
            {
                ResourceKeys = request.ResourceKeys,
                StartIndex = 0,
                Count = MaxWatcherSnapshotCount,
                Filters = request.Filters,
                TextFragments = request.TextFragments
            });
            long maxYieldedLogId = 0;
            foreach (var log in existingLogs.Items)
            {
                maxYieldedLogId = Math.Max(maxYieldedLogId, log.InternalId);
                yield return log;
            }
            while (channel.Reader.TryRead(out var pendingLog))
            {
                if (pendingLog.InternalId > maxYieldedLogId)
                {
                    maxYieldedLogId = pendingLog.InternalId;
                    yield return pendingLog;
                }
            }

            await foreach (var log in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (log.InternalId > maxYieldedLogId)
                {
                    maxYieldedLogId = log.InternalId;
                    yield return log;
                }
            }
        }
        finally
        {
            lock (_watchersLock)
            {
                _logWatchers?.Remove(watcher);
            }
            channel.Writer.TryComplete();
        }
    }

    private void PushSpansToWatchers(List<OtlpSpan> spans)
    {
        SpanWatcher[] watchers;
        lock (_watchersLock)
        {
            watchers = _spanWatchers?.ToArray() ?? [];
        }

        foreach (var span in spans)
        {
            foreach (var watcher in watchers)
            {
                var request = watcher.Request;
                if (request.ResourceKeys is { Count: > 0 } keys &&
                    !keys.Contains(span.Source.ResourceKey) &&
                    (span.UninstrumentedPeer is null || !keys.Contains(span.UninstrumentedPeer.ResourceKey)))
                {
                    continue;
                }
                if (!MatchesSpanWatcherRequest(span, request))
                {
                    continue;
                }
                watcher.Channel.Writer.TryWrite(span);
            }
        }
    }

    private void PushLogsToWatchers(List<OtlpLogEntry> logs)
    {
        LogWatcher[] watchers;
        lock (_watchersLock)
        {
            watchers = _logWatchers?.ToArray() ?? [];
        }

        foreach (var log in logs)
        {
            foreach (var watcher in watchers)
            {
                var request = watcher.Request;
                if (request.ResourceKeys is { Count: > 0 } keys && !keys.Contains(log.ResourceView.ResourceKey))
                {
                    continue;
                }
                if (!MatchesLogWatcherRequest(log, request))
                {
                    continue;
                }
                watcher.Channel.Writer.TryWrite(log);
            }
        }
    }

    private static bool MatchesSpanWatcherRequest(OtlpSpan span, WatchSpansRequest request)
    {
        if (!string.IsNullOrEmpty(request.TraceId) && !OtlpHelpers.MatchTelemetryId(request.TraceId, span.TraceId))
        {
            return false;
        }
        if (request.HasError.HasValue && (span.Status == OtlpSpanStatusCode.Error) != request.HasError.Value)
        {
            return false;
        }
        if (request.Filters.Any(filter => filter.Enabled && !filter.Apply(span)))
        {
            return false;
        }
        return request.TextFragments is not { Length: > 0 } fragments || MatchesSpanTextFragments(span, fragments);
    }

    private static bool MatchesLogWatcherRequest(OtlpLogEntry log, WatchLogsRequest request)
    {
        IEnumerable<OtlpLogEntry> matches = [log];
        foreach (var filter in request.Filters.Where(filter => filter.Enabled))
        {
            matches = filter.Apply(matches);
        }
        return matches.Any() &&
            (request.TextFragments is not { Length: > 0 } fragments || MatchesLogTextFragments(log, fragments));
    }

    private static bool MatchesSpanTextFragments(OtlpSpan span, string[] fragments)
    {
        return SearchTextParser.MatchesAllFragments(fragments, span, static (candidate, fragment) =>
            candidate.Name.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.SpanId.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.TraceId.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.Scope.Name.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.Source.Resource.ResourceName.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.Status.ToString().Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.Kind.ToString().Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.StatusMessage?.Contains(fragment, StringComparisons.FullTextSearch) == true ||
            candidate.Attributes.Any(attribute =>
                attribute.Key.Contains(fragment, StringComparisons.FullTextSearch) ||
                attribute.Value.Contains(fragment, StringComparisons.FullTextSearch)) ||
            candidate.Events.Any(spanEvent => spanEvent.Name.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    private static bool MatchesLogTextFragments(OtlpLogEntry log, string[] fragments)
    {
        return SearchTextParser.MatchesAllFragments(fragments, log, static (candidate, fragment) =>
            candidate.Message.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.Scope.Name.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.TraceId.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.SpanId.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.Severity.ToString().Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.ResourceView.Resource.ResourceName.Contains(fragment, StringComparisons.FullTextSearch) ||
            candidate.EventName?.Contains(fragment, StringComparisons.FullTextSearch) == true ||
            candidate.Attributes.Any(attribute =>
                attribute.Key.Contains(fragment, StringComparisons.FullTextSearch) ||
                attribute.Value.Contains(fragment, StringComparisons.FullTextSearch)));
    }

    private void DisposeWatchers()
    {
        lock (_watchersLock)
        {
            foreach (var watcher in _spanWatchers ?? [])
            {
                watcher.Channel.Writer.TryComplete();
            }
            foreach (var watcher in _logWatchers ?? [])
            {
                watcher.Channel.Writer.TryComplete();
            }
            _spanWatchers?.Clear();
            _logWatchers?.Clear();
        }
    }

    private sealed class SpanWatcher(WatchSpansRequest request, Channel<OtlpSpan> channel)
    {
        public WatchSpansRequest Request => request;
        public Channel<OtlpSpan> Channel => channel;
    }

    private sealed class LogWatcher(WatchLogsRequest request, Channel<OtlpLogEntry> channel)
    {
        public WatchLogsRequest Request => request;
        public Channel<OtlpLogEntry> Channel => channel;
    }
}
