// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using System.Diagnostics;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Persists telemetry to SQLite and exposes it through the dashboard telemetry model.
/// </summary>
public sealed partial class SqliteTelemetryRepository : ITelemetryRepository, ITelemetryRepositoryWriter
{
    private readonly DashboardSqliteDatabase _database;
    private readonly OtlpContext _otlpContext;
    private readonly PauseManager _pauseManager;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<IOutgoingPeerResolver> _outgoingPeerResolvers;
    private readonly List<IDisposable> _outgoingPeerSubscriptions = [];
    private int _disposed;

    private static string CreateContainsLikePattern(string value) => $"%{EscapeLikePattern(value)}%";

    private static string CreateStartsWithLikePattern(string value) => $"{EscapeLikePattern(value)}%";

    public bool IsReadOnly => _database.IsReadOnly;

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal);
    }

    internal ActivitySource SqlActivitySource => _database.ActivitySource;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteTelemetryRepository"/> class.
    /// </summary>
    /// <param name="database">The dashboard database used to persist telemetry.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="dashboardOptions">The dashboard options.</param>
    /// <param name="pauseManager">The telemetry pause manager.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="outgoingPeerResolvers">The resolvers used to identify outgoing peer resources.</param>
    public SqliteTelemetryRepository(
        DashboardSqliteDatabase database,
        ILoggerFactory loggerFactory,
        IOptions<DashboardOptions> dashboardOptions,
        PauseManager pauseManager,
        TimeProvider timeProvider,
        IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        _database = database;
        _pauseManager = pauseManager;
        _timeProvider = timeProvider;
        _outgoingPeerResolvers = outgoingPeerResolvers.ToList();
        _otlpContext = new OtlpContext
        {
            Logger = loggerFactory.CreateLogger<SqliteTelemetryRepository>(),
            Options = dashboardOptions.Value.TelemetryLimits
        };

        if (!database.IsReadOnly)
        {
            foreach (var resolver in _outgoingPeerResolvers)
            {
                _outgoingPeerSubscriptions.Add(resolver.OnPeerChanges(async () =>
                {
                    await RecalculateUninstrumentedPeersAsync().ConfigureAwait(false);
                    NotifyPeersChanged();
                }));
            }
        }

    }

    public async Task AddLogsAsync(AddContext context, RepeatedField<ResourceLogs> resourceLogs)
    {
        if (_pauseManager.AreStructuredLogsPaused(out _))
        {
            _otlpContext.Logger.LogTrace("{Count} incoming structured log resource(s) ignored because of an active pause.", resourceLogs.Count);
            return;
        }

        EnsureWritable();
        try
        {
            NotifyLogsAdded(await AddLogsToDatabaseAsync(context, resourceLogs).ConfigureAwait(false));
        }
        catch
        {
            using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
            {
                ClearIngestionCaches();
            }
            throw;
        }
    }

    public async Task AddMetricsAsync(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        if (_pauseManager.AreMetricsPaused(out _))
        {
            _otlpContext.Logger.LogTrace("{Count} incoming metric resource(s) ignored because of an active pause.", resourceMetrics.Count);
            return;
        }

        EnsureWritable();
        var successCount = context.SuccessCount;
        await AddMetricsToDatabaseAsync(context, resourceMetrics).ConfigureAwait(false);
        if (context.SuccessCount > successCount)
        {
            NotifyMetricsAdded();
        }
    }

    public async Task AddTracesAsync(AddContext context, RepeatedField<ResourceSpans> resourceSpans)
    {
        if (_pauseManager.AreTracesPaused(out _))
        {
            _otlpContext.Logger.LogTrace("{Count} incoming trace resource(s) ignored because of an active pause.", resourceSpans.Count);
            return;
        }

        EnsureWritable();
        try
        {
            NotifySpansAdded(await AddTracesToDatabaseAsync(context, resourceSpans).ConfigureAwait(false));
        }
        catch
        {
            using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
            {
                ClearIngestionCaches();
            }
            throw;
        }
    }

    public PagedResult<OtlpLogEntry> GetLogs(GetLogsContext context) => GetLogsFromDatabase(context);
    public PagedResult<LogSummary> GetLogSummaries(GetLogsContext context) => GetLogSummariesFromDatabase(context);
    public OtlpLogEntry? GetLog(long logId) => GetLogFromDatabase(logId);
    public List<OtlpLogEntry> GetLogsForSpan(string traceId, string spanId) => GetLogs(new GetLogsContext
    {
        ResourceKeys = [],
        StartIndex = 0,
        Count = int.MaxValue,
        Filters =
        [
            new FieldTelemetryFilter { Field = KnownStructuredLogFields.TraceIdField, Condition = FilterCondition.Equals, Value = traceId },
            new FieldTelemetryFilter { Field = KnownStructuredLogFields.SpanIdField, Condition = FilterCondition.Equals, Value = spanId }
        ]
    }).Items;
    public List<OtlpLogEntry> GetLogsForTrace(string traceId) => GetLogs(new GetLogsContext
    {
        ResourceKeys = [],
        StartIndex = 0,
        Count = int.MaxValue,
        Filters =
        [
            new FieldTelemetryFilter { Field = KnownStructuredLogFields.TraceIdField, Condition = FilterCondition.Equals, Value = traceId }
        ]
    }).Items;
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
    public OtlpResource? GetPeerResource(OtlpSpan span) => span.UninstrumentedPeer;
    public List<OtlpInstrumentSummary> GetInstrumentSummaries(ResourceKey key) => GetCachedInstrumentSummaries(key);
    public OtlpInstrumentSummary? GetInstrumentSummary(ResourceKey resourceKey, string meterName, string instrumentName) =>
        GetCachedInstruments(resourceKey, meterName, instrumentName).FirstOrDefault()?.Summary;
    public OtlpInstrumentData? GetInstrument(GetInstrumentRequest request) => GetInstrumentFromDatabase(request);
    public DateTime? GetInstrumentLatestEndTime(ResourceKey resourceKey, string meterName, string instrumentName) =>
        GetInstrumentLatestEndTimeFromDatabase(resourceKey, meterName, instrumentName);
    public async Task ClearSelectedSignalsAsync(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        EnsureWritable();
        await ClearSelectedLogsFromDatabaseAsync(selectedResources).ConfigureAwait(false);
        await ClearSelectedTracesFromDatabaseAsync(selectedResources).ConfigureAwait(false);
        await ClearSelectedMetricsFromDatabaseAsync(selectedResources).ConfigureAwait(false);
        ClearUnviewedErrorCounts(selectedResources);
        RaiseSubscriptionChanged(_logSubscriptions);
        RaiseSubscriptionChanged(_tracesSubscriptions);
        RaiseSubscriptionChanged(_metricsSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public async Task ClearTracesAsync(ResourceKey? resourceKey = null)
    {
        EnsureWritable();
        await ClearTracesFromDatabaseAsync(resourceKey).ConfigureAwait(false);
        RaiseSubscriptionChanged(_tracesSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public async Task ClearStructuredLogsAsync(ResourceKey? resourceKey = null)
    {
        EnsureWritable();
        await ClearStructuredLogsFromDatabaseAsync(resourceKey).ConfigureAwait(false);
        ClearUnviewedErrorCounts(resourceKey);
        RaiseSubscriptionChanged(_logSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    public async Task ClearMetricsAsync(ResourceKey? resourceKey = null)
    {
        EnsureWritable();
        await ClearMetricsFromDatabaseAsync(resourceKey).ConfigureAwait(false);
        RaiseSubscriptionChanged(_metricsSubscriptions);
        RaiseSubscriptionChanged(_resourceSubscriptions);
    }

    private void EnsureWritable()
    {
        _database.EnsureWritable("Historical dashboard telemetry is read-only.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var subscription in _outgoingPeerSubscriptions)
        {
            subscription.Dispose();
        }
        DisposeWatchers();
    }

}