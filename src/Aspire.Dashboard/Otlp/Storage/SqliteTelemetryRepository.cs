// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using System.Diagnostics;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using SQLitePCL;

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

    /// <summary>
    /// Runs a cancellable database read on the thread pool.
    /// </summary>
    /// <remarks>
    /// The read registers <c>sqlite3_interrupt</c> for <paramref name="cancellationToken"/>, which surfaces as
    /// a <see cref="SqliteException"/> with <c>SQLITE_INTERRUPT</c> rather than an <see cref="OperationCanceledException"/>.
    /// Translate it here so callers see normal cancellation semantics.
    /// </remarks>
    internal static Task<T> RunReadAsync<T>(Func<CancellationToken, T> read, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                return read(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }, cancellationToken);

    // SQLite database access is always synchronous. Potentially slow repository reads use Task.Run
    // to avoid blocking the Blazor UI thread.
    public Task<PagedResult<OtlpLogEntry>> GetLogsAsync(GetLogsContext context, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetLogsFromDatabase(context, token), cancellationToken);
    public Task<PagedResult<LogSummary>> GetLogSummariesAsync(GetLogsContext context, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetLogSummariesFromDatabase(context, token), cancellationToken);
    public OtlpLogEntry? GetLog(long logId) => GetLogFromDatabase(logId);
    public async Task<List<OtlpLogEntry>> GetLogsForSpanAsync(string traceId, string spanId, CancellationToken cancellationToken)
    {
        var result = await GetLogsAsync(new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter { Field = KnownStructuredLogFields.TraceIdField, Condition = FilterCondition.Equals, Value = traceId },
                new FieldTelemetryFilter { Field = KnownStructuredLogFields.SpanIdField, Condition = FilterCondition.Equals, Value = spanId }
            ]
        }, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }
    public async Task<List<OtlpLogEntry>> GetLogsForTraceAsync(string traceId, CancellationToken cancellationToken)
    {
        var result = await GetLogsAsync(new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter { Field = KnownStructuredLogFields.TraceIdField, Condition = FilterCondition.Equals, Value = traceId }
            ]
        }, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }
    public Task<List<string>> GetLogPropertyKeysAsync(ResourceKey? resourceKey, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetLogPropertyKeysFromDatabase(resourceKey, token), cancellationToken);
    public Task<List<string>> GetTracePropertyKeysAsync(ResourceKey? resourceKey, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetTracePropertyKeysFromDatabase(resourceKey, token), cancellationToken);
    public Task<GetTracesResponse> GetTracesAsync(GetTracesRequest context, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetTracesFromDatabase(context, token), cancellationToken);
    public Task<GetTraceSummariesResponse> GetTraceSummariesAsync(GetTracesRequest context, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetTraceSummariesFromDatabase(context, token), cancellationToken);
    public Task<GetSpansResponse> GetSpansAsync(GetSpansRequest context, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetSpansFromDatabase(context, token), cancellationToken);
    public Task<Dictionary<string, int>> GetTraceFieldValuesAsync(string attributeName, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetTraceFieldValuesFromDatabase(attributeName, token), cancellationToken);
    public Task<Dictionary<string, int>> GetLogsFieldValuesAsync(string attributeName, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetLogsFieldValuesFromDatabase(attributeName, token), cancellationToken);
    public bool HasUpdatedTrace(OtlpTrace trace) => HasUpdatedTraceInDatabase(trace);
    public OtlpTrace? GetTrace(string traceId) => GetTraceFromDatabase(traceId);
    public OtlpSpan? GetSpan(string traceId, string spanId) => GetSpanFromDatabase(traceId, spanId);
    public OtlpResource? GetPeerResource(OtlpSpan span) => span.UninstrumentedPeer;
    public List<OtlpInstrumentSummary> GetInstrumentSummaries(ResourceKey key) => GetCachedInstrumentSummaries(key);
    public OtlpInstrumentSummary? GetInstrumentSummary(ResourceKey resourceKey, string meterName, string instrumentName) =>
        GetCachedInstruments(resourceKey, meterName, instrumentName).FirstOrDefault()?.Summary;
    public Task<OtlpInstrumentData?> GetInstrumentAsync(GetInstrumentRequest request, CancellationToken cancellationToken) =>
        RunReadAsync(token => GetInstrumentFromDatabase(request, token), cancellationToken);
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