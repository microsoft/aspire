// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Utils;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using static OpenTelemetry.Proto.Trace.V1.Span.Types;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class TelemetryRepository : IDisposable
{
    private readonly PauseManager _pauseManager;
    private readonly IOutgoingPeerResolver[] _outgoingPeerResolvers;
    private readonly ILogger _logger;

    private readonly object _lock = new();
    internal TimeSpan _subscriptionMinExecuteInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<Subscription> _resourceSubscriptions = new();
    private readonly List<Subscription> _logSubscriptions = new();
    private readonly List<Subscription> _metricsSubscriptions = new();
    private readonly List<Subscription> _tracesSubscriptions = new();

    // Push-based streaming watchers - lazily initialized
    private readonly object _watchersLock = new();
    private List<SpanWatcher>? _spanWatchers;
    private List<LogWatcher>? _logWatchers;

    private readonly ConcurrentDictionary<ResourceKey, OtlpResource> _resources = new();

    private readonly ReaderWriterLockSlim _logsLock = new();
    private readonly Dictionary<string, OtlpScope> _logScopes = new();
    private readonly CircularBuffer<OtlpLogEntry> _logs;
    private readonly HashSet<(OtlpResource Resource, string PropertyKey)> _logPropertyKeys = new();
    private readonly HashSet<(OtlpResource Resource, string PropertyKey)> _tracePropertyKeys = new();
    private readonly Dictionary<ResourceKey, int> _resourceUnviewedErrorLogs = new();

    private readonly ReaderWriterLockSlim _tracesLock = new();
    private readonly Dictionary<string, OtlpScope> _traceScopes = new();
    private readonly CircularBuffer<OtlpTrace> _traces;
    private readonly List<OtlpSpanLink> _spanLinks = new();
    private readonly List<IDisposable> _peerResolverSubscriptions = new();
    internal readonly OtlpContext _otlpContext;

    public bool HasDisplayedMaxLogLimitMessage { get; set; }
    public Message? MaxLogLimitMessage { get; set; }

    public bool HasDisplayedMaxTraceLimitMessage { get; set; }
    public Message? MaxTraceLimitMessage { get; set; }

    // For testing.
    internal List<OtlpSpanLink> SpanLinks => _spanLinks;
    internal List<Subscription> TracesSubscriptions => _tracesSubscriptions;

    public TelemetryRepository(ILoggerFactory loggerFactory, IOptions<DashboardOptions> dashboardOptions, PauseManager pauseManager, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        _logger = loggerFactory.CreateLogger(typeof(TelemetryRepository));
        _otlpContext = new OtlpContext
        {
            Logger = _logger,
            Options = dashboardOptions.Value.TelemetryLimits
        };
        _pauseManager = pauseManager;
        _outgoingPeerResolvers = outgoingPeerResolvers.ToArray();
        _logs = new(_otlpContext.Options.MaxLogCount);
        _traces = new(_otlpContext.Options.MaxTraceCount);
        _traces.ItemRemovedForCapacity += TracesItemRemovedForCapacity;

        foreach (var outgoingPeerResolver in _outgoingPeerResolvers)
        {
            _peerResolverSubscriptions.Add(outgoingPeerResolver.OnPeerChanges(OnPeerChanged));
        }
    }

    private void TracesItemRemovedForCapacity(OtlpTrace trace)
    {
        // Remove links from central collection when the span is removed.
        foreach (var span in trace.Spans)
        {
            foreach (var link in span.Links)
            {
                _spanLinks.Remove(link);
            }
        }
    }

    public List<OtlpResource> GetResources(bool includeUninstrumentedPeers = false)
    {
        return GetResourcesCore(includeUninstrumentedPeers, name: null);
    }

    public List<OtlpResource> GetResourcesByName(string name, bool includeUninstrumentedPeers = false)
    {
        return GetResourcesCore(includeUninstrumentedPeers, name);
    }

    private List<OtlpResource> GetResourcesCore(bool includeUninstrumentedPeers, string? name)
    {
        IEnumerable<OtlpResource> results = _resources.Values;
        if (!includeUninstrumentedPeers)
        {
            results = results.Where(a => !a.UninstrumentedPeer);
        }
        if (name != null)
        {
            results = results.Where(a => string.Equals(a.ResourceKey.Name, name, StringComparisons.ResourceName));
        }

        var resources = results.OrderBy(a => a.ResourceKey).ToList();
        return resources;
    }

    public OtlpResource? GetResourceByCompositeName(string compositeName)
    {
        foreach (var kvp in _resources)
        {
            if (kvp.Key.EqualsCompositeName(compositeName))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    public OtlpResource? GetResource(ResourceKey key)
    {
        if (key.InstanceId == null)
        {
            throw new InvalidOperationException($"{nameof(ResourceKey)} must have an instance ID.");
        }

        _resources.TryGetValue(key, out var resource);
        return resource;
    }

    public List<OtlpResource> GetResources(ResourceKey key, bool includeUninstrumentedPeers = false)
    {
        if (key.InstanceId == null)
        {
            return GetResourcesByName(key.Name, includeUninstrumentedPeers: includeUninstrumentedPeers);
        }

        var resource = GetResource(key);
        if (resource == null || (resource.UninstrumentedPeer && !includeUninstrumentedPeers))
        {
            return [];
        }

        return [resource];
    }

    public Dictionary<ResourceKey, int> GetResourceUnviewedErrorLogsCount()
    {
        _logsLock.EnterReadLock();

        try
        {
            return _resourceUnviewedErrorLogs.ToDictionary();
        }
        finally
        {
            _logsLock.ExitReadLock();
        }
    }

    internal void MarkViewedErrorLogs(ResourceKey? key)
    {
        _logsLock.EnterWriteLock();

        try
        {
            if (key == null)
            {
                // Mark all logs as viewed.
                if (_resourceUnviewedErrorLogs.Count > 0)
                {
                    _resourceUnviewedErrorLogs.Clear();
                    RaiseSubscriptionChanged(_logSubscriptions);
                }
                return;
            }
            var resources = GetResources(key.Value);
            foreach (var resource in resources)
            {
                // Mark one resource logs as viewed.
                if (_resourceUnviewedErrorLogs.Remove(resource.ResourceKey))
                {
                    RaiseSubscriptionChanged(_logSubscriptions);
                }
            }
        }
        finally
        {
            _logsLock.ExitWriteLock();
        }
    }

    private OtlpResourceView GetOrAddResourceView(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var key = resource.GetResourceKey();

        var (otlpResource, isNew) = GetOrAddResource(key, uninstrumentedPeer: false);
        if (isNew)
        {
            RaiseSubscriptionChanged(_resourceSubscriptions);
        }

        return otlpResource.GetView(resource.Attributes);
    }

    private (OtlpResource Resource, bool IsNew) GetOrAddResource(ResourceKey key, bool uninstrumentedPeer)
    {
        // Fast path.
        if (_resources.TryGetValue(key, out var resource))
        {
            resource.SetUninstrumentedPeer(uninstrumentedPeer);
            return (Resource: resource, IsNew: false);
        }

        // Slower get or add path.
        // This GetOrAdd allocates a closure, so we avoid it if possible.
        var newResource = false;
        resource = _resources.GetOrAdd(key, _ =>
        {
            newResource = true;
            return new OtlpResource(key.Name, key.InstanceId, uninstrumentedPeer, _otlpContext);
        });
        if (!newResource)
        {
            resource.SetUninstrumentedPeer(uninstrumentedPeer);
        }
        else
        {
            _logger.LogTrace("New resource added: {ResourceKey}", key);
        }
        return (Resource: resource, IsNew: newResource);
    }

    public Subscription OnNewResources(Func<Task> callback)
    {
        return AddSubscription(nameof(OnNewResources), null, SubscriptionType.Read, callback, _resourceSubscriptions);
    }

    public Subscription OnNewLogs(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback)
    {
        return AddSubscription(nameof(OnNewLogs), resourceKey, subscriptionType, callback, _logSubscriptions);
    }

    public Subscription OnNewMetrics(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback)
    {
        return AddSubscription(nameof(OnNewMetrics), resourceKey, subscriptionType, callback, _metricsSubscriptions);
    }

    public Subscription OnNewTraces(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback)
    {
        return AddSubscription(nameof(OnNewTraces), resourceKey, subscriptionType, callback, _tracesSubscriptions);
    }

    private Subscription AddSubscription(string name, ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback, List<Subscription> subscriptions)
    {
        Subscription? subscription = null;
        subscription = new Subscription(name, resourceKey, subscriptionType, callback, () =>
        {
            lock (_lock)
            {
                subscriptions.Remove(subscription!);
            }
        }, ExecutionContext.Capture(), this);

        lock (_lock)
        {
            subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void RaiseSubscriptionChanged(List<Subscription> subscriptions)
    {
        lock (_lock)
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Execute();
            }
        }
    }

    public void AddLogs(AddContext context, RepeatedField<ResourceLogs> resourceLogs)
    {
        if (_pauseManager.AreStructuredLogsPaused(out _))
        {
            _logger.LogTrace("{Count} incoming structured log(s) ignored because of an active pause.", resourceLogs.Count);
            return;
        }

        foreach (var rl in resourceLogs)
        {
            OtlpResourceView resourceView;
            try
            {
                resourceView = GetOrAddResourceView(rl.Resource);
            }
            catch (Exception ex)
            {
                context.FailureCount += rl.ScopeLogs.Count;
                _otlpContext.Logger.LogInformation(ex, "Error adding resource.");
                continue;
            }

            AddLogsCore(context, resourceView, rl.ScopeLogs);
            SetResourceHasLogs(resourceView.Resource, true);
        }

        RaiseSubscriptionChanged(_logSubscriptions);
    }

    public void AddLogsCore(AddContext context, OtlpResourceView resourceView, RepeatedField<ScopeLogs> scopeLogs)
    {
        List<OtlpLogEntry>? addedLogs = null;

        _logsLock.EnterWriteLock();

        try
        {
            foreach (var sl in scopeLogs)
            {
                if (!OtlpHelpers.TryGetOrAddScope(_logScopes, sl.Scope, _otlpContext, TelemetryType.Logs, out var scope))
                {
                    context.FailureCount += sl.LogRecords.Count;
                    continue;
                }

                foreach (var record in sl.LogRecords)
                {
                    try
                    {
                        var logEntry = new OtlpLogEntry(record, resourceView, scope, _otlpContext);

                        // Insert log entry in the correct position based on timestamp.
                        // Logs can be added out of order by different services.
                        var added = false;
                        for (var i = _logs.Count - 1; i >= 0; i--)
                        {
                            if (logEntry.TimeStamp > _logs[i].TimeStamp)
                            {
                                _logs.Insert(i + 1, logEntry);
                                added = true;
                                break;
                            }
                        }
                        if (!added)
                        {
                            _logs.Insert(0, logEntry);
                        }

                        // For log entries error and above, increment the unviewed count if there are no read log subscriptions for the resource.
                        // We don't increment the count if there are active read subscriptions because the count will be quickly decremented when the subscription callback is run.
                        // Notifying the user there are errors and then immediately clearing the notification is confusing.
                        if (logEntry.IsError)
                        {
                            if (!_logSubscriptions.Any(s => s.SubscriptionType == SubscriptionType.Read && (s.ResourceKey == resourceView.ResourceKey || s.ResourceKey == null)))
                            {
                                ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(_resourceUnviewedErrorLogs, resourceView.ResourceKey, out _);
                                // Adds to dictionary if not present.
                                count++;
                            }
                        }

                        foreach (var kvp in logEntry.Attributes)
                        {
                            _logPropertyKeys.Add((resourceView.Resource, kvp.Key));
                        }

                        // Collect log for push-based streaming (lazy init to avoid allocation when no watchers)
                        addedLogs ??= new List<OtlpLogEntry>();
                        addedLogs.Add(logEntry);

                        context.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        context.FailureCount++;
                        _otlpContext.Logger.LogInformation(ex, "Error adding log entry.");
                    }
                }
            }
        }
        finally
        {
            _logsLock.ExitWriteLock();
        }

        // Push logs to watchers outside the lock
        if (addedLogs is not null)
        {
            PushLogsToWatchers(addedLogs, resourceView.ResourceKey);
        }
    }

    public PagedResult<OtlpLogEntry> GetLogs(GetLogsContext context)
    {
        List<OtlpResource>? resources = null;
        if (context.ResourceKey is { } key)
        {
            resources = GetResources(key);

            if (resources.Count == 0)
            {
                return PagedResult<OtlpLogEntry>.Empty;
            }
        }

        _logsLock.EnterReadLock();

        try
        {
            var results = _logs.AsEnumerable();
            if (resources?.Count > 0)
            {
                results = results.Where(l => MatchResources(l.ResourceView.ResourceKey, resources));
            }

            foreach (var filter in context.Filters.GetEnabledFilters())
            {
                results = filter.Apply(results);
            }

            return OtlpHelpers.GetItems(results, context.StartIndex, context.Count, _logs.IsFull);
        }
        finally
        {
            _logsLock.ExitReadLock();
        }
    }

    public OtlpLogEntry? GetLog(long logId)
    {
        _logsLock.EnterReadLock();

        try
        {
            foreach (var logEntry in _logs)
            {
                if (logEntry.InternalId == logId)
                {
                    return logEntry;
                }
            }

            return null;
        }
        finally
        {
            _logsLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets logs associated with a specific span, filtered by trace ID and span ID.
    /// </summary>
    /// <param name="traceId">The trace ID.</param>
    /// <param name="spanId">The span ID.</param>
    /// <returns>A list of log entries associated with the span.</returns>
    public List<OtlpLogEntry> GetLogsForSpan(string traceId, string spanId)
    {
        var logsContext = new GetLogsContext
        {
            ResourceKey = null,
            Count = int.MaxValue,
            StartIndex = 0,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownStructuredLogFields.TraceIdField,
                    Condition = FilterCondition.Equals,
                    Value = traceId
                },
                new FieldTelemetryFilter
                {
                    Field = KnownStructuredLogFields.SpanIdField,
                    Condition = FilterCondition.Equals,
                    Value = spanId
                }
            ]
        };
        return GetLogs(logsContext).Items;
    }

    /// <summary>
    /// Gets logs associated with a specific trace, filtered by trace ID.
    /// </summary>
    /// <param name="traceId">The trace ID.</param>
    /// <returns>A list of log entries associated with the trace.</returns>
    public List<OtlpLogEntry> GetLogsForTrace(string traceId)
    {
        var logsContext = new GetLogsContext
        {
            ResourceKey = null,
            Count = int.MaxValue,
            StartIndex = 0,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownStructuredLogFields.TraceIdField,
                    Condition = FilterCondition.Equals,
                    Value = traceId
                }
            ]
        };
        return GetLogs(logsContext).Items;
    }

    public List<string> GetLogPropertyKeys(ResourceKey? resourceKey)
    {
        List<OtlpResource>? resources = null;
        if (resourceKey != null)
        {
            resources = GetResources(resourceKey.Value);
        }

        _logsLock.EnterReadLock();

        try
        {
            var resourceKeys = _logPropertyKeys.AsEnumerable();
            if (resources?.Count > 0)
            {
                resourceKeys = resourceKeys.Where(keys => MatchResources(keys.Resource.ResourceKey, resources));
            }

            var keys = resourceKeys.Select(keys => keys.PropertyKey).Distinct();
            return keys.OrderBy(k => k).ToList();
        }
        finally
        {
            _logsLock.ExitReadLock();
        }
    }

    public List<string> GetTracePropertyKeys(ResourceKey? resourceKey)
    {
        List<OtlpResource>? resources = null;
        if (resourceKey != null)
        {
            resources = GetResources(resourceKey.Value, includeUninstrumentedPeers: true);
        }

        _tracesLock.EnterReadLock();

        try
        {
            var resourceKeys = _tracePropertyKeys.AsEnumerable();
            if (resources?.Count > 0)
            {
                resourceKeys = resourceKeys.Where(keys => MatchResources(keys.Resource.ResourceKey, resources));
            }

            var keys = resourceKeys.Select(keys => keys.PropertyKey).Distinct();
            return keys.OrderBy(k => k).ToList();
        }
        finally
        {
            _tracesLock.ExitReadLock();
        }
    }

    public GetTracesResponse GetTraces(GetTracesRequest context)
    {
        List<OtlpResource>? resources = null;
        if (context.ResourceKey is { } key)
        {
            resources = GetResources(key, includeUninstrumentedPeers: true);

            if (resources.Count == 0)
            {
                return new GetTracesResponse
                {
                    PagedResult = PagedResult<OtlpTrace>.Empty,
                    MaxDuration = TimeSpan.Zero
                };
            }
        }

        _tracesLock.EnterReadLock();

        try
        {
            var results = _traces.AsEnumerable();
            if (resources?.Count > 0)
            {
                results = results.Where(t =>
                {
                    return MatchResources(t, resources);
                });
            }
            if (!string.IsNullOrWhiteSpace(context.FilterText))
            {
                results = results.Where(t => t.FullName.Contains(context.FilterText, StringComparison.OrdinalIgnoreCase));
            }

            var filters = context.Filters.GetEnabledFilters().ToList();

            if (filters.Count > 0)
            {
                results = results.Where(t =>
                {
                    // A trace matches when one of its span matches all filters.
                    foreach (var span in t.Spans)
                    {
                        var match = true;
                        foreach (var filter in filters)
                        {
                            if (!filter.Apply(span))
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
                        {
                            return true;
                        }
                    }

                    return false;
                });
            }

            // Traces can be modified as new spans are added. Copy traces before returning results to avoid concurrency issues.
            var copyFunc = static (OtlpTrace t) => OtlpTrace.Clone(t);

            var pagedResults = OtlpHelpers.GetItems(results, context.StartIndex, context.Count, _traces.IsFull, copyFunc);
            var maxDuration = pagedResults.TotalItemCount > 0 ? results.Max(r => r.Duration) : default;

            return new GetTracesResponse
            {
                PagedResult = pagedResults,
                MaxDuration = maxDuration
            };
        }
        finally
        {
            _tracesLock.ExitReadLock();
        }
    }

    private static bool MatchResources(ResourceKey resourceKey, List<OtlpResource> resources)
    {
        foreach (var resource in resources)
        {
            if (resourceKey == resource.ResourceKey)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchResources(OtlpTrace t, List<OtlpResource> resources)
    {
        for (var i = 0; i < resources.Count; i++)
        {
            var resourceKey = resources[i].ResourceKey;

            // Spans collection type returns a struct enumerator so it's ok to foreach inside another loop.
            foreach (var span in t.Spans)
            {
                if (span.Source.ResourceKey == resourceKey || span.UninstrumentedPeer?.ResourceKey == resourceKey)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void SetResourceHasLogs(OtlpResource resource, bool value)
    {
        if (resource.HasLogs != value)
        {
            resource.HasLogs = value;
            RaiseSubscriptionChanged(_resourceSubscriptions);
        }
    }

    private void SetResourceHasTraces(OtlpResource resource, bool value)
    {
        if (resource.HasTraces != value)
        {
            resource.HasTraces = value;
            RaiseSubscriptionChanged(_resourceSubscriptions);
        }
    }

    private void SetResourceHasMetrics(OtlpResource resource, bool value)
    {
        if (resource.HasMetrics != value)
        {
            resource.HasMetrics = value;
            RaiseSubscriptionChanged(_resourceSubscriptions);
        }
    }

    /// <summary>
    /// Clears selected telemetry signals for specified resources.
    /// </summary>
    /// <param name="selectedResources">Dictionary mapping resource names to the data types to clear.</param>
    public void ClearSelectedSignals(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        var allOtlpResources = GetResources();

        foreach (var otlpResource in allOtlpResources)
        {
            var resourceName = otlpResource.ResourceKey.GetCompositeName();

            if (!selectedResources.TryGetValue(resourceName, out var dataTypes))
            {
                continue;
            }

            var clearStructuredLogs = IsDataTypeSelected(dataTypes, AspireDataType.StructuredLogs);
            var clearTraces = IsDataTypeSelected(dataTypes, AspireDataType.Traces);
            var clearMetrics = IsDataTypeSelected(dataTypes, AspireDataType.Metrics);

            if (clearStructuredLogs)
            {
                ClearStructuredLogs(otlpResource.ResourceKey);
            }

            if (clearTraces)
            {
                ClearTraces(otlpResource.ResourceKey);
            }

            if (clearMetrics)
            {
                ClearMetrics(otlpResource.ResourceKey);
            }

            // If Resource flag is set, remove the resource itself
            if (dataTypes.Contains(AspireDataType.Resource))
            {
                ClearResource(otlpResource.ResourceKey);
            }
        }

        static bool IsDataTypeSelected(HashSet<AspireDataType> dataTypes, AspireDataType dataType)
        {
            // Always remove everything if the resource is being removed.
            return dataTypes.Contains(dataType) || dataTypes.Contains(AspireDataType.Resource);
        }
    }

    public void ClearTraces(ResourceKey? resourceKey = null)
    {
        List<OtlpResource>? resources = null;
        if (resourceKey.HasValue)
        {
            resources = GetResources(resourceKey.Value, includeUninstrumentedPeers: true);
        }

        _tracesLock.EnterWriteLock();
        try
        {
            if (resources is null || resources.Count == 0)
            {
                // Nothing selected, clear everything.
                _traces.Clear();
            }
            else
            {
                for (var i = _traces.Count - 1; i >= 0; i--)
                {
                    // Remove trace if any span matches one of the resources. This matches filter behavior.
                    if (MatchResources(_traces[i], resources))
                    {
                        _traces.RemoveAt(i);
                        continue;
                    }
                }

                // Update HasTraces flag for cleared resources
                foreach (var resource in resources)
                {
                    SetResourceHasTraces(resource, false);
                }
            }
        }
        finally
        {
            _tracesLock.ExitWriteLock();
        }

        RaiseSubscriptionChanged(_tracesSubscriptions);
    }

    public void ClearStructuredLogs(ResourceKey? resourceKey = null)
    {
        List<OtlpResource>? resources = null;
        if (resourceKey.HasValue)
        {
            resources = GetResources(resourceKey.Value);
        }

        _logsLock.EnterWriteLock();

        try
        {
            if (resources is null || resources.Count == 0)
            {
                // Nothing selected, clear everything.
                _logs.Clear();
            }
            else
            {
                for (var i = _logs.Count - 1; i >= 0; i--)
                {
                    if (MatchResources(_logs[i].ResourceView.ResourceKey, resources))
                    {
                        _logs.RemoveAt(i);
                        continue;
                    }
                }

                // Update HasLogs flag for cleared resources
                foreach (var resource in resources)
                {
                    SetResourceHasLogs(resource, false);
                    _resourceUnviewedErrorLogs.Remove(resource.ResourceKey);
                }
            }
        }
        finally
        {
            _logsLock.ExitWriteLock();
        }

        RaiseSubscriptionChanged(_logSubscriptions);
    }

    private void ClearResource(ResourceKey resourceKey)
    {
        if (_resources.TryRemove(resourceKey, out _))
        {
            RaiseSubscriptionChanged(_resourceSubscriptions);
        }
    }

    public void ClearMetrics(ResourceKey? resourceKey = null)
    {
        List<OtlpResource> resources;
        if (resourceKey.HasValue)
        {
            resources = GetResources(resourceKey.Value);
        }
        else
        {
            resources = _resources.Values.ToList();
        }

        foreach (var resource in resources)
        {
            resource.ClearMetrics();
            SetResourceHasMetrics(resource, false);
        }

        RaiseSubscriptionChanged(_metricsSubscriptions);
    }

    public Dictionary<string, int> GetTraceFieldValues(string attributeName)
    {
        _tracesLock.EnterReadLock();

        var attributesValues = new Dictionary<string, int>(StringComparers.OtlpAttribute);

        try
        {
            foreach (var trace in _traces)
            {
                foreach (var span in trace.Spans)
                {
                    var values = OtlpSpan.GetFieldValue(span, attributeName);
                    if (values.Value1 != null)
                    {
                        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(attributesValues, values.Value1, out _);
                        // Adds to dictionary if not present.
                        count++;
                    }
                    if (values.Value2 != null)
                    {
                        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(attributesValues, values.Value2, out _);
                        // Adds to dictionary if not present.
                        count++;
                    }
                }
            }
        }
        finally
        {
            _tracesLock.ExitReadLock();
        }

        return attributesValues;
    }

    public Dictionary<string, int> GetLogsFieldValues(string attributeName)
    {
        _logsLock.EnterReadLock();

        var attributesValues = new Dictionary<string, int>(StringComparers.OtlpAttribute);

        try
        {
            foreach (var log in _logs)
            {
                var value = OtlpLogEntry.GetFieldValue(log, attributeName);
                if (value != null)
                {
                    ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(attributesValues, value, out _);
                    // Adds to dictionary if not present.
                    count++;
                }
            }
        }
        finally
        {
            _logsLock.ExitReadLock();
        }

        return attributesValues;
    }

    public bool HasUpdatedTrace(OtlpTrace trace)
    {
        _tracesLock.EnterReadLock();

        try
        {
            var latestTrace = GetTraceUnsynchronized(trace.TraceId);
            if (latestTrace == null)
            {
                // Trace must have been removed. Technically there is an update (nothing).
                return true;
            }

            return latestTrace.LastUpdatedDate > trace.LastUpdatedDate;
        }
        finally
        {
            _tracesLock.ExitReadLock();
        }
    }

    public OtlpTrace? GetTrace(string traceId)
    {
        _tracesLock.EnterReadLock();

        try
        {
            return GetTraceAndCloneUnsynchronized(traceId);
        }
        finally
        {
            _tracesLock.ExitReadLock();
        }
    }

    private OtlpTrace? GetTraceUnsynchronized(string traceId)
    {
        Debug.Assert(_tracesLock.IsReadLockHeld || _tracesLock.IsWriteLockHeld, $"Must get lock before calling {nameof(GetTraceUnsynchronized)}.");

        foreach (var trace in _traces)
        {
            if (OtlpHelpers.MatchTelemetryId(traceId, trace.TraceId))
            {
                return trace;
            }
        }

        return null;
    }

    private OtlpTrace? GetTraceAndCloneUnsynchronized(string traceId)
    {
        Debug.Assert(_tracesLock.IsReadLockHeld || _tracesLock.IsWriteLockHeld, $"Must get lock before calling {nameof(GetTraceAndCloneUnsynchronized)}.");

        var trace = GetTraceUnsynchronized(traceId);

        return trace != null ? OtlpTrace.Clone(trace) : null;
    }

    private OtlpSpan? GetSpanAndCloneUnsynchronized(string traceId, string spanId)
    {
        Debug.Assert(_tracesLock.IsReadLockHeld || _tracesLock.IsWriteLockHeld, $"Must get lock before calling {nameof(GetSpanAndCloneUnsynchronized)}.");

        // Trace and its spans are cloned here.
        var trace = GetTraceAndCloneUnsynchronized(traceId);
        if (trace != null)
        {
            foreach (var span in trace.Spans)
            {
                if (span.SpanId == spanId)
                {
                    return span;
                }
            }
        }

        return null;
    }

    public OtlpSpan? GetSpan(string traceId, string spanId)
    {
        _tracesLock.EnterReadLock();

        try
        {
            return GetSpanAndCloneUnsynchronized(traceId, spanId);
        }
        finally
        {
            _tracesLock.ExitReadLock();
        }
    }

    public void AddMetrics(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        if (_pauseManager.AreMetricsPaused(out _))
        {
            _logger.LogTrace("{Count} incoming metric(s) ignored because of an active pause.", resourceMetrics.Count);
            return;
        }

        foreach (var rm in resourceMetrics)
        {
            OtlpResourceView resourceView;
            try
            {
                resourceView = GetOrAddResourceView(rm.Resource);
            }
            catch (Exception ex)
            {
                context.FailureCount += rm.ScopeMetrics.Sum(s => s.Metrics.Count);
                _otlpContext.Logger.LogInformation(ex, "Error adding resource.");
                continue;
            }

            resourceView.Resource.AddMetrics(context, rm.ScopeMetrics);
            SetResourceHasMetrics(resourceView.Resource, true);
        }

        RaiseSubscriptionChanged(_metricsSubscriptions);
    }

    public void AddTraces(AddContext context, RepeatedField<ResourceSpans> resourceSpans)
    {
        if (_pauseManager.AreTracesPaused(out _))
        {
            _logger.LogTrace("{Count} incoming trace(s) ignored because of an active pause.", resourceSpans.Count);
            return;
        }

        foreach (var rs in resourceSpans)
        {
            OtlpResourceView resourceView;
            try
            {
                resourceView = GetOrAddResourceView(rs.Resource);
            }
            catch (Exception ex)
            {
                context.FailureCount += rs.ScopeSpans.Sum(s => s.Spans.Count);
                _otlpContext.Logger.LogInformation(ex, "Error adding resource.");
                continue;
            }

            AddTracesCore(context, resourceView, rs.ScopeSpans);
            SetResourceHasTraces(resourceView.Resource, true);
        }

        RaiseSubscriptionChanged(_tracesSubscriptions);
    }

    private static OtlpSpanStatusCode ConvertStatus(Status? status)
    {
        return status?.Code switch
        {
            Status.Types.StatusCode.Ok => OtlpSpanStatusCode.Ok,
            Status.Types.StatusCode.Error => OtlpSpanStatusCode.Error,
            Status.Types.StatusCode.Unset => OtlpSpanStatusCode.Unset,
            _ => OtlpSpanStatusCode.Unset
        };
    }

    internal static OtlpSpanKind ConvertSpanKind(SpanKind? kind)
    {
        return kind switch
        {
            // Unspecified to Internal is intentional.
            // "Implementations MAY assume SpanKind to be INTERNAL when receiving UNSPECIFIED."
            SpanKind.Unspecified => OtlpSpanKind.Internal,
            SpanKind.Internal => OtlpSpanKind.Internal,
            SpanKind.Client => OtlpSpanKind.Client,
            SpanKind.Server => OtlpSpanKind.Server,
            SpanKind.Producer => OtlpSpanKind.Producer,
            SpanKind.Consumer => OtlpSpanKind.Consumer,
            _ => OtlpSpanKind.Unspecified
        };
    }

    internal void AddTracesCore(AddContext context, OtlpResourceView resourceView, RepeatedField<ScopeSpans> scopeSpans)
    {
        List<OtlpSpan>? addedSpans = null;

        _tracesLock.EnterWriteLock();

        try
        {
            foreach (var scopeSpan in scopeSpans)
            {
                if (!OtlpHelpers.TryGetOrAddScope(_traceScopes, scopeSpan.Scope, _otlpContext, TelemetryType.Traces, out var scope))
                {
                    context.FailureCount += scopeSpan.Spans.Count;
                    continue;
                }

                var updatedTraces = new Dictionary<ReadOnlyMemory<byte>, OtlpTrace>();

                foreach (var span in scopeSpan.Spans)
                {
                    try
                    {
                        OtlpTrace? trace;
                        var newTrace = false;

                        // Fast path to check if the span is in a trace that's been updated this add call.
                        if (!updatedTraces.TryGetValue(span.TraceId.Memory, out trace))
                        {
                            if (!TryGetTraceById(_traces, span.TraceId.Memory, out trace))
                            {
                                trace = new OtlpTrace(span.TraceId.Memory, DateTime.UtcNow);
                                newTrace = true;
                            }
                        }

                        var newSpan = CreateSpan(resourceView, span, trace, scope, _otlpContext);
                        trace.AddSpan(newSpan);

                        // The new span might be linked to by an existing span.
                        // Check current links to see if a backlink should be created.
                        foreach (var existingLink in _spanLinks)
                        {
                            if (existingLink.SpanId == newSpan.SpanId && existingLink.TraceId == newSpan.TraceId)
                            {
                                newSpan.BackLinks.Add(existingLink);
                            }
                        }

                        // Add links to central collection. Add backlinks to existing spans.
                        foreach (var link in newSpan.Links)
                        {
                            _spanLinks.Add(link);

                            var linkedSpan = GetSpanAndCloneUnsynchronized(link.TraceId, link.SpanId);
                            linkedSpan?.BackLinks.Add(link);
                        }

                        // Traces are sorted by the start time of the first span.
                        // We need to ensure traces are in the correct order if we're:
                        // 1. Adding a new trace.
                        // 2. The first span of the trace has changed.
                        if (newTrace)
                        {
                            var added = false;
                            for (var i = _traces.Count - 1; i >= 0; i--)
                            {
                                var currentTrace = _traces[i];
                                if (trace.FirstSpan.StartTime > currentTrace.FirstSpan.StartTime)
                                {
                                    _traces.Insert(i + 1, trace);
                                    added = true;
                                    break;
                                }
                            }
                            if (!added)
                            {
                                _traces.Insert(0, trace);
                            }
                        }
                        else
                        {
                            if (trace.FirstSpan == newSpan)
                            {
                                var moved = false;
                                var index = _traces.IndexOf(trace);

                                for (var i = index - 1; i >= 0; i--)
                                {
                                    var currentTrace = _traces[i];
                                    if (trace.FirstSpan.StartTime > currentTrace.FirstSpan.StartTime)
                                    {
                                        var insertPosition = i + 1;
                                        if (index != insertPosition)
                                        {
                                            _traces.RemoveAt(index);
                                            _traces.Insert(insertPosition, trace);
                                        }
                                        moved = true;
                                        break;
                                    }
                                }
                                if (!moved)
                                {
                                    if (index != 0)
                                    {
                                        _traces.RemoveAt(index);
                                        _traces.Insert(0, trace);
                                    }
                                }
                            }
                        }

                        foreach (var kvp in newSpan.Attributes)
                        {
                            _tracePropertyKeys.Add((resourceView.Resource, kvp.Key));
                        }

                        // Newly added or updated trace should always been in the collection.
                        Debug.Assert(_traces.Contains(trace), "Trace not found in traces collection.");

                        updatedTraces[trace.Key] = trace;

                        // Collect span for push-based streaming (lazy init to avoid allocation when no watchers)
                        addedSpans ??= new List<OtlpSpan>();
                        addedSpans.Add(newSpan);

                        context.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        context.FailureCount++;
                        _otlpContext.Logger.LogInformation(ex, "Error adding span.");
                    }

                    AssertTraceOrder();
                    AssertSpanLinks();
                }

                // After spans are updated, loop through traces and their spans and update uninstrumented peer values.
                // These can change
                foreach (var (_, updatedTrace) in updatedTraces)
                {
                    CalculateTraceUninstrumentedPeers(updatedTrace);
                }
            }
        }
        finally
        {
            _tracesLock.ExitWriteLock();
        }

        // Push spans to watchers outside the lock
        if (addedSpans is not null)
        {
            PushSpansToWatchers(addedSpans, resourceView.ResourceKey);
        }

        static bool TryGetTraceById(CircularBuffer<OtlpTrace> traces, ReadOnlyMemory<byte> traceId, [NotNullWhen(true)] out OtlpTrace? trace)
        {
            var s = traceId.Span;
            for (var i = traces.Count - 1; i >= 0; i--)
            {
                if (traces[i].Key.Span.SequenceEqual(s))
                {
                    trace = traces[i];
                    return true;
                }
            }

            trace = null;
            return false;
        }
    }

    public OtlpResource? GetPeerResource(OtlpSpan span)
    {
        var peer = ResolveUninstrumentedPeerResource(span, _outgoingPeerResolvers);
        if (peer == null)
        {
            return null;
        }

        var resourceKey = ResourceKey.Create(name: peer.DisplayName, instanceId: peer.Name);
        var (resource, _) = GetOrAddResource(resourceKey, uninstrumentedPeer: true);
        return resource;
    }

    private void CalculateTraceUninstrumentedPeers(OtlpTrace trace)
    {
        foreach (var span in trace.Spans)
        {
            // A span may indicate a call to another service but the service isn't instrumented.
            var hasPeerService = OtlpHelpers.GetPeerAddress(span.Attributes) != null;
            var hasUninstrumentedPeer = hasPeerService && span.Kind is OtlpSpanKind.Client or OtlpSpanKind.Producer && !span.GetChildSpans().Any();
            var uninstrumentedPeer = hasUninstrumentedPeer ? ResolveUninstrumentedPeerResource(span, _outgoingPeerResolvers) : null;

            if (uninstrumentedPeer != null)
            {
                if (span.UninstrumentedPeer?.ResourceKey.EqualsCompositeName(uninstrumentedPeer.Name) ?? false)
                {
                    // Already the correct value. No changes needed.
                    continue;
                }

                var resourceKey = ResourceKey.Create(name: uninstrumentedPeer.DisplayName, instanceId: uninstrumentedPeer.Name);
                var (resource, _) = GetOrAddResource(resourceKey, uninstrumentedPeer: true);
                trace.SetSpanUninstrumentedPeer(span, resource);
            }
            else
            {
                trace.SetSpanUninstrumentedPeer(span, null);
            }
        }
    }

    private static ResourceViewModel? ResolveUninstrumentedPeerResource(OtlpSpan span, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        // Attempt to resolve uninstrumented peer to a friendly name from the span.
        foreach (var resolver in outgoingPeerResolvers)
        {
            if (resolver.TryResolvePeer(span.Attributes, out _, out var matchedResourced))
            {
                return matchedResourced;
            }
        }

        return null;
    }

    [Conditional("DEBUG")]
    private void AssertTraceOrder()
    {
        DateTime current = default;
        for (var i = 0; i < _traces.Count; i++)
        {
            var trace = _traces[i];
            if (trace.FirstSpan.StartTime < current)
            {
                throw new InvalidOperationException($"Traces not in order at index {i}.");
            }

            current = trace.FirstSpan.StartTime;
        }
    }

    [Conditional("DEBUG")]
    private void AssertSpanLinks()
    {
        // Create a local copy of span links.
        var currentSpanLinks = _spanLinks.ToList();

        // Remove span links that match span links on spans.
        // Throw an error if an expected span link doesn't exist.
        foreach (var trace in _traces)
        {
            foreach (var span in trace.Spans)
            {
                foreach (var link in span.Links)
                {
                    if (!currentSpanLinks.Remove(link))
                    {
                        throw new InvalidOperationException($"Couldn't find expected link from span {span.SpanId} to span {link.SpanId}.");
                    }
                }
            }
        }

        // Throw error if there are orphaned span links.
        if (currentSpanLinks.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"There are {currentSpanLinks.Count} orphaned span links.");
            foreach (var link in currentSpanLinks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"\tSource span ID: {link.SourceSpanId}, Target span ID: {link.SpanId}");
            }

            throw new InvalidOperationException(sb.ToString());
        }
    }

    private static OtlpSpan CreateSpan(OtlpResourceView resourceView, Span span, OtlpTrace trace, OtlpScope scope, OtlpContext context)
    {
        var id = span.SpanId?.ToHexString();
        if (id is null)
        {
            throw new ArgumentException("Span has no SpanId");
        }

        var events = new List<OtlpSpanEvent>();

        var links = new List<OtlpSpanLink>();
        foreach (var e in span.Links)
        {
            links.Add(new OtlpSpanLink
            {
                SourceSpanId = id,
                SourceTraceId = trace.TraceId,
                TraceState = e.TraceState,
                SpanId = e.SpanId.ToHexString(),
                TraceId = e.TraceId.ToHexString(),
                Attributes = e.Attributes.ToKeyValuePairs(context)
            });
        }

        var newSpan = new OtlpSpan(resourceView, trace, scope)
        {
            SpanId = id,
            ParentSpanId = span.ParentSpanId?.ToHexString(),
            Name = span.Name,
            Kind = ConvertSpanKind(span.Kind),
            StartTime = OtlpHelpers.UnixNanoSecondsToDateTime(span.StartTimeUnixNano),
            EndTime = OtlpHelpers.UnixNanoSecondsToDateTime(span.EndTimeUnixNano),
            Status = ConvertStatus(span.Status),
            StatusMessage = span.Status?.Message,
            Attributes = span.Attributes.ToKeyValuePairs(context, filter: attribute => attribute.Key != OtlpHelpers.AspireDestinationNameAttribute),
            State = !string.IsNullOrEmpty(span.TraceState) ? span.TraceState : null,
            Events = events,
            Links = links,
            BackLinks = []
        };

        foreach (var e in span.Events.OrderBy(e => e.TimeUnixNano))
        {
            events.Add(new OtlpSpanEvent(newSpan)
            {
                InternalId = Guid.NewGuid(),
                Name = e.Name,
                Time = OtlpHelpers.UnixNanoSecondsToDateTime(e.TimeUnixNano),
                Attributes = e.Attributes.ToKeyValuePairs(context)
            });

            if (events.Count >= context.Options.MaxSpanEventCount)
            {
                break;
            }
        }
        return newSpan;
    }

    public List<OtlpInstrumentSummary> GetInstrumentsSummaries(ResourceKey key)
    {
        var resources = GetResources(key);
        if (resources.Count == 0)
        {
            return new List<OtlpInstrumentSummary>();
        }
        else if (resources.Count == 1)
        {
            return resources[0].GetInstrumentsSummary();
        }
        else
        {
            var allResourceSummaries = resources
                .SelectMany(a => a.GetInstrumentsSummary())
                .DistinctBy(s => s.GetKey())
                .ToList();

            return allResourceSummaries;
        }

    }

    public OtlpInstrumentData? GetInstrument(GetInstrumentRequest request)
    {
        var resources = GetResources(request.ResourceKey);
        var instruments = resources
            .Select(a => a.GetInstrument(request.MeterName, request.InstrumentName, request.StartTime, request.EndTime))
            .OfType<OtlpInstrument>()
            .ToList();

        if (instruments.Count == 0)
        {
            return null;
        }
        else if (instruments.Count == 1)
        {
            var instrument = instruments[0];
            return new OtlpInstrumentData
            {
                Summary = instrument.Summary,
                KnownAttributeValues = instrument.KnownAttributeValues,
                Dimensions = instrument.Dimensions.Values.ToList(),
                HasOverflow = instrument.HasOverflow
            };
        }
        else
        {
            var allDimensions = new List<DimensionScope>();
            var allKnownAttributes = new Dictionary<string, List<string?>>();
            var hasOverflow = false;

            foreach (var instrument in instruments)
            {
                allDimensions.AddRange(instrument.Dimensions.Values);

                foreach (var knownAttributeValues in instrument.KnownAttributeValues)
                {
                    ref var values = ref CollectionsMarshal.GetValueRefOrAddDefault(allKnownAttributes, knownAttributeValues.Key, out _);
                    // Adds to dictionary if not present.
                    if (values != null)
                    {
                        values = values.Union(knownAttributeValues.Value).ToList();
                    }
                    else
                    {
                        values = knownAttributeValues.Value.ToList();
                    }
                }

                hasOverflow = hasOverflow || instrument.HasOverflow;
            }

            return new OtlpInstrumentData
            {
                Summary = instruments[0].Summary,
                Dimensions = allDimensions,
                KnownAttributeValues = allKnownAttributes,
                HasOverflow = hasOverflow
            };
        }
    }

    private Task OnPeerChanged()
    {
        _tracesLock.EnterWriteLock();

        try
        {
            // When peers change then we need to recalculate the uninstrumented peers of spans.
            foreach (var trace in _traces)
            {
                CalculateTraceUninstrumentedPeers(trace);
            }
        }
        finally
        {
            _tracesLock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var subscription in _peerResolverSubscriptions)
        {
            subscription.Dispose();
        }

        DisposeWatchers();
    }
}
