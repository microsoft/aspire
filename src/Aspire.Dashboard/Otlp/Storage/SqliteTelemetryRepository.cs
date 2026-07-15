// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Persists telemetry to SQLite and exposes it through the dashboard telemetry model.
/// </summary>
public sealed partial class SqliteTelemetryRepository : ITelemetryRepository, IMetricTelemetryRepository
{
    private readonly DashboardSqliteDatabase _database;
    private readonly OtlpContext _otlpContext;
    private readonly PauseManager _pauseManager;
    private readonly IReadOnlyList<IOutgoingPeerResolver> _outgoingPeerResolvers;
    private readonly List<IDisposable> _outgoingPeerSubscriptions = [];
    private readonly object _writeLock = new();

    private static string CreateContainsLikePattern(string value) => $"%{EscapeLikePattern(value)}%";

    private static string CreateStartsWithLikePattern(string value) => $"{EscapeLikePattern(value)}%";

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal);
    }

    public SqliteTelemetryRepository(
        string databasePath,
        ILoggerFactory loggerFactory,
        IOptions<DashboardOptions> dashboardOptions,
        PauseManager pauseManager,
        IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers,
        bool readOnly = false)
        : this(new DashboardSqliteDatabase(databasePath, readOnly), loggerFactory, dashboardOptions, pauseManager, outgoingPeerResolvers)
    {
    }

    internal SqliteTelemetryRepository(
        DashboardSqliteDatabase database,
        ILoggerFactory loggerFactory,
        IOptions<DashboardOptions> dashboardOptions,
        PauseManager pauseManager,
        IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        _database = database;
        _pauseManager = pauseManager;
        _outgoingPeerResolvers = outgoingPeerResolvers.ToList();
        _otlpContext = new OtlpContext
        {
            Logger = loggerFactory.CreateLogger<SqliteTelemetryRepository>(),
            Options = dashboardOptions.Value.TelemetryLimits
        };

        if (!database.IsReadOnly)
        {
            database.InitializeSchema();
            foreach (var resolver in _outgoingPeerResolvers)
            {
                _outgoingPeerSubscriptions.Add(resolver.OnPeerChanges(() =>
                {
                    RecalculateUninstrumentedPeers();
                    return Task.CompletedTask;
                }));
            }
        }

    }

    public List<OtlpResource> GetResources(bool includeUninstrumentedPeers = false) => GetTelemetryResources(includeUninstrumentedPeers, name: null);
    public List<OtlpResource> GetResourcesByName(string name, bool includeUninstrumentedPeers = false) => GetTelemetryResources(includeUninstrumentedPeers, name);
    public OtlpResource? GetResourceByCompositeName(string compositeName) => GetResources(includeUninstrumentedPeers: true).SingleOrDefault(resource => resource.ResourceKey.EqualsCompositeName(compositeName));
    public OtlpResource? GetResource(ResourceKey key) => GetResources(includeUninstrumentedPeers: true).SingleOrDefault(resource => resource.ResourceKey == key);
    public List<OtlpResource> GetResources(ResourceKey key, bool includeUninstrumentedPeers = false)
    {
        return key.InstanceId is null
            ? GetResourcesByName(key.Name, includeUninstrumentedPeers)
            : GetResources(includeUninstrumentedPeers).Where(resource => resource.ResourceKey == key).ToList();
    }
    public void AddLogs(AddContext context, RepeatedField<ResourceLogs> resourceLogs)
    {
        if (_pauseManager.AreStructuredLogsPaused(out _))
        {
            _otlpContext.Logger.LogTrace("{Count} incoming structured log resource(s) ignored because of an active pause.", resourceLogs.Count);
            return;
        }

        EnsureWritable();
        NotifyLogsAdded(AddLogsToDatabase(context, resourceLogs));
    }

    public void AddMetrics(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        if (_pauseManager.AreMetricsPaused(out _))
        {
            _otlpContext.Logger.LogTrace("{Count} incoming metric resource(s) ignored because of an active pause.", resourceMetrics.Count);
            return;
        }

        EnsureWritable();
        var successCount = context.SuccessCount;
        AddMetricsToDatabase(context, resourceMetrics);
        if (context.SuccessCount > successCount)
        {
            NotifyMetricsAdded();
        }
    }

    public void AddTraces(AddContext context, RepeatedField<ResourceSpans> resourceSpans)
    {
        if (_pauseManager.AreTracesPaused(out _))
        {
            _otlpContext.Logger.LogTrace("{Count} incoming trace resource(s) ignored because of an active pause.", resourceSpans.Count);
            return;
        }

        EnsureWritable();
        NotifySpansAdded(AddTracesToDatabase(context, resourceSpans));
    }

    public PagedResult<OtlpLogEntry> GetLogs(GetLogsContext context) => GetLogsFromDatabase(context);
    public OtlpLogEntry? GetLog(long logId) => GetLogFromDatabase(logId);
    public List<OtlpLogEntry> GetLogsForSpan(string traceId, string spanId) => GetLogsForSpanFromDatabase(traceId, spanId);
    public List<OtlpLogEntry> GetLogsForTrace(string traceId) => GetLogsForTraceFromDatabase(traceId);
    public List<string> GetLogPropertyKeys(ResourceKey? resourceKey) => GetLogPropertyKeysFromDatabase(resourceKey);
    public List<string> GetTracePropertyKeys(ResourceKey? resourceKey) => GetTracePropertyKeysFromDatabase(resourceKey);
    public GetTracesResponse GetTraces(GetTracesRequest context) => GetTracesFromDatabase(context);
    public GetTraceSummariesResponse GetTraceSummaries(GetTracesRequest context) => GetTraceSummariesFromDatabase(context);
    public GetSpansResponse GetSpans(GetSpansRequest context) => GetSpansFromDatabase(context);
    public Dictionary<string, int> GetTraceFieldValues(string attributeName) => GetTraceFieldValuesFromDatabase(attributeName);
    public Dictionary<string, int> GetLogsFieldValues(string attributeName) => GetLogsFieldValuesFromDatabase(attributeName);
    public bool HasUpdatedTrace(OtlpTrace trace) => HasUpdatedTraceInDatabase(trace);
    public OtlpTrace? GetTrace(string traceId) => GetTraceFromDatabase(traceId);
    public OtlpSpan? GetSpan(string traceId, string spanId) => GetSpanFromDatabase(traceId, spanId);
    public OtlpResource? GetPeerResource(OtlpSpan span)
    {
        if (span.UninstrumentedPeer is not null)
        {
            return span.UninstrumentedPeer;
        }

        foreach (var resolver in _outgoingPeerResolvers)
        {
            if (resolver.TryResolvePeer(span.Attributes, out _, out var matchedResource) && matchedResource is not null)
            {
                return GetResource(ResourceKey.Create(matchedResource.DisplayName, matchedResource.Name));
            }
        }

        return null;
    }
    public List<OtlpInstrumentSummary> GetInstrumentsSummaries(ResourceKey key) => GetInstrumentsSummariesFromDatabase(key);
    public OtlpInstrumentData? GetInstrument(GetInstrumentRequest request) => GetInstrumentFromDatabase(request);
    DateTime? IMetricTelemetryRepository.GetInstrumentLatestEndTime(ResourceKey resourceKey, string meterName, string instrumentName) =>
        GetInstrumentLatestEndTimeFromDatabase(resourceKey, meterName, instrumentName);
    public void ClearSelectedSignals(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        EnsureWritable();
        ClearSelectedLogsFromDatabase(selectedResources);
        ClearSelectedTracesFromDatabase(selectedResources);
        ClearSelectedMetricsFromDatabase(selectedResources);
        ClearUnviewedErrorCounts(selectedResources);
        RaiseSubscriptionChanged(_logSubscriptions);
        RaiseSubscriptionChanged(_tracesSubscriptions);
        RaiseSubscriptionChanged(_metricsSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public void ClearTraces(ResourceKey? resourceKey = null)
    {
        EnsureWritable();
        ClearTracesFromDatabase(resourceKey);
        RaiseSubscriptionChanged(_tracesSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public void ClearStructuredLogs(ResourceKey? resourceKey = null)
    {
        EnsureWritable();
        ClearStructuredLogsFromDatabase(resourceKey);
        ClearUnviewedErrorCounts(resourceKey);
        RaiseSubscriptionChanged(_logSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public void ClearMetrics(ResourceKey? resourceKey = null)
    {
        EnsureWritable();
        ClearMetricsFromDatabase(resourceKey);
        RaiseSubscriptionChanged(_metricsSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    private void EnsureWritable()
    {
        _database.EnsureWritable("Historical dashboard telemetry is read-only.");
    }

    public void Dispose()
    {
        foreach (var subscription in _outgoingPeerSubscriptions)
        {
            subscription.Dispose();
        }
        DisposeWatchers();
    }

}