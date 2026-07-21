// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Dapper;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int MaxTraceBatchSize = 100;
    // Microsoft.Data.Sqlite resolves every named parameter when binding. Reusing prepared commands avoids
    // repeated preparation, while these batch sizes keep each binding pass small enough to avoid nonlinear cost.
    private const int MaxSpanBatchSize = 25;
    private const int MaxSpanDetailBatchSize = 50;

    private List<OtlpSpan> AddTracesToDatabase(AddContext context, RepeatedField<ResourceSpans> resourceSpans)
    {
        var addedSpans = new List<OtlpSpan>();
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var incomingSpans = resourceSpans
                .SelectMany(resource => resource.ScopeSpans)
                .SelectMany(scope => scope.Spans)
                .Select(span => new IncomingSpanIdentity(
                    span.TraceId.ToHexString(),
                    span.SpanId.ToHexString(),
                    span.ParentSpanId.IsEmpty ? null : span.ParentSpanId.ToHexString()))
                .ToArray();
            var traceIds = incomingSpans
                .Select(span => span.TraceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var ingestionState = LoadTraceIngestionState(connection, transaction, incomingSpans, traceIds);
            var traces = new Dictionary<string, OtlpTrace>(StringComparer.Ordinal);
            var pendingSpans = new List<PendingSpan>();
            var resourcesWithTraces = new HashSet<CachedResource>();

            foreach (var resourceSpansItem in resourceSpans)
            {
                OtlpResourceView resourceView;
                CachedResource cachedResource;
                long resourceId;
                long resourceViewId;
                try
                {
                    var resourceKey = resourceSpansItem.Resource.GetResourceKey();
                    cachedResource = GetOrAddCachedResource(connection, transaction, resourceKey);
                    resourceId = cachedResource.ResourceId;
                    var cachedView = GetOrAddCachedResourceView(connection, transaction, cachedResource, resourceSpansItem.Resource.Attributes);
                    resourceView = cachedView.View;
                    resourceViewId = cachedView.ResourceViewId;
                }
                catch (Exception exception)
                {
                    context.FailureCount += resourceSpansItem.ScopeSpans.Sum(scope => scope.Spans.Count);
                    _otlpContext.Logger.LogInformation(exception, "Error adding resource.");
                    continue;
                }
                resourcesWithTraces.Add(cachedResource);

                foreach (var scopeSpans in resourceSpansItem.ScopeSpans)
                {
                    OtlpScope scope;
                    long scopeId;
                    try
                    {
                        var cachedScope = GetOrAddCachedScope(connection, transaction, cachedResource, scopeSpans.Scope, CachedTelemetryType.Traces);
                        scopeId = cachedScope.Scope.ScopeId;
                        scope = cachedScope.Scope.Scope;
                    }
                    catch (Exception exception)
                    {
                        context.FailureCount += scopeSpans.Spans.Count;
                        _otlpContext.Logger.LogInformation(exception, "Error adding trace scope.");
                        continue;
                    }

                    foreach (var span in scopeSpans.Spans)
                    {
                        try
                        {
                            var pendingSpan = PrepareSpan(traces, ingestionState, resourceId, resourceViewId, resourceView, scopeId, scope, span);
                            pendingSpans.Add(pendingSpan);
                            addedSpans.Add(pendingSpan.Span);
                            context.SuccessCount++;
                        }
                        catch (Exception exception)
                        {
                            context.FailureCount++;
                            _otlpContext.Logger.LogInformation(exception, "Error adding span.");
                        }
                    }
                }

            }

            UpsertTraces(connection, transaction, traces.Values, ingestionState.ExistingTraces);
            InsertSpans(connection, transaction, pendingSpans);
            InsertSpanDetails(connection, transaction, pendingSpans);
            var peerChangedTraceIds = UpdateUninstrumentedPeers(connection, transaction, traces.Values, ingestionState);
            UpdateTraceResourceSummaries(
                connection,
                transaction,
                pendingSpans,
                ingestionState,
                peerChangedTraceIds);
            MarkResourcesHaveTraces(connection, transaction, resourcesWithTraces);
            TrimTracesToCapacity(connection, transaction);
            transaction.Commit();
        }

        return addedSpans;
    }

    private static TraceIngestionState LoadTraceIngestionState(
        SqliteConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<IncomingSpanIdentity> incomingSpans,
        IReadOnlyList<string> traceIds)
    {
        var existingTraces = new Dictionary<string, IngestionTraceRecord>(StringComparer.Ordinal);
        foreach (var batch in traceIds.Chunk(MaxTraceBatchSize))
        {
            foreach (var record in connection.Query<IngestionTraceRecord>("""
                SELECT
                    t.trace_id AS TraceId,
                    t.first_span_timestamp_ticks AS FirstSpanTimestampTicks,
                    t.last_span_end_timestamp_ticks AS LastSpanEndTimestampTicks,
                    t.last_updated_timestamp_ticks AS LastUpdatedTimestampTicks,
                    t.full_name AS FullName,
                    t.primary_span_id AS PrimarySpanId,
                    t.has_error AS HasError,
                    t.has_gen_ai AS HasGenAI,
                    p.parent_span_id AS PrimaryParentSpanId,
                    p.start_time_ticks AS PrimaryStartTimeTicks
                FROM telemetry_traces t
                JOIN telemetry_spans p ON p.trace_id = t.trace_id AND p.span_id = t.primary_span_id
                WHERE t.trace_id IN @TraceIds;
                """, new { TraceIds = batch }, transaction))
            {
                existingTraces.Add(record.TraceId, record);
            }
        }

        var existingSpans = new Dictionary<(string TraceId, string SpanId), IngestionExistingSpanRecord>();
        var existingParentReferences = new HashSet<(string TraceId, string ParentSpanId)>();
        var circularSpanIds = new HashSet<(string TraceId, string SpanId)>();
        if (existingTraces.Count == 0)
        {
            return new TraceIngestionState
            {
                ExistingTraces = existingTraces,
                ExistingSpans = existingSpans,
                ExistingParentReferences = existingParentReferences,
                CircularSpanIds = circularSpanIds
            };
        }

        var incomingSpansForExistingTraces = incomingSpans
            .Where(span => existingTraces.ContainsKey(span.TraceId))
            .ToArray();
        var incomingIdentities = incomingSpansForExistingTraces
            .Select(span => (span.TraceId, span.SpanId))
            .ToHashSet();
        var parentIdentities = incomingSpansForExistingTraces
            .Where(span => span.ParentSpanId is not null)
            .Select(span => (span.TraceId, SpanId: span.ParentSpanId!))
            .ToHashSet();
        var ancestors = new List<IngestionAncestorRecord>();
        foreach (var traceBatch in existingTraces.Keys.Chunk(MaxTraceBatchSize))
        {
            var traceIdSet = traceBatch.ToHashSet(StringComparer.Ordinal);
            var traceIncomingSpans = incomingSpansForExistingTraces
                .Where(span => traceIdSet.Contains(span.TraceId))
                .ToArray();

            var relevantSpanIds = traceIncomingSpans
                .Select(span => span.SpanId)
                .Concat(traceIncomingSpans.Where(span => span.ParentSpanId is not null).Select(span => span.ParentSpanId!))
                .Distinct(StringComparer.Ordinal);
            foreach (var spanIdBatch in relevantSpanIds.Chunk(MaxSpanDetailBatchSize))
            {
                foreach (var record in connection.Query<IngestionExistingSpanRecord>("""
                    SELECT
                        s.trace_id AS TraceId,
                        s.span_id AS SpanId,
                        s.resource_order_ticks AS ResourceOrderTicks,
                        s.uninstrumented_peer_resource_id AS UninstrumentedPeerResourceId
                    FROM telemetry_spans s
                    WHERE s.trace_id IN @TraceIds AND s.span_id IN @SpanIds;
                    """, new { TraceIds = traceBatch, SpanIds = spanIdBatch }, transaction))
                {
                    var identity = (record.TraceId, record.SpanId);
                    if (incomingIdentities.Contains(identity) || parentIdentities.Contains(identity))
                    {
                        existingSpans.TryAdd(identity, record);
                    }
                }
            }

            foreach (var spanIdBatch in traceIncomingSpans
                .Select(span => span.SpanId)
                .Distinct(StringComparer.Ordinal)
                .Chunk(MaxSpanDetailBatchSize))
            {
                foreach (var record in connection.Query<IngestionParentReferenceRecord>("""
                    SELECT
                        s.trace_id AS TraceId,
                        s.parent_span_id AS ParentSpanId
                    FROM telemetry_spans s
                    WHERE s.trace_id IN @TraceIds AND s.parent_span_id IN @ParentSpanIds;
                    """, new { TraceIds = traceBatch, ParentSpanIds = spanIdBatch }, transaction))
                {
                    var parentIdentity = (record.TraceId, record.ParentSpanId);
                    if (incomingIdentities.Contains(parentIdentity))
                    {
                        existingParentReferences.Add(parentIdentity);
                    }
                }
            }

            foreach (var parentSpanIdBatch in traceIncomingSpans
                .Where(span => span.ParentSpanId is not null)
                .Select(span => span.ParentSpanId!)
                .Distinct(StringComparer.Ordinal)
                .Chunk(MaxSpanDetailBatchSize))
            {
                ancestors.AddRange(connection.Query<IngestionAncestorRecord>("""
                    WITH RECURSIVE ancestors(trace_id, span_id, parent_span_id) AS (
                        SELECT s.trace_id, s.span_id, s.parent_span_id
                        FROM telemetry_spans s
                        WHERE s.trace_id IN @TraceIds AND s.span_id IN @ParentSpanIds
                        UNION
                        SELECT parent.trace_id, parent.span_id, parent.parent_span_id
                        FROM ancestors child
                        JOIN telemetry_spans parent ON parent.trace_id = child.trace_id AND parent.span_id = child.parent_span_id
                    )
                    SELECT
                        trace_id AS TraceId,
                        span_id AS SpanId,
                        parent_span_id AS ParentSpanId
                    FROM ancestors;
                    """, new { TraceIds = traceBatch, ParentSpanIds = parentSpanIdBatch }, transaction));
            }
        }

        var parentSpanIds = new Dictionary<(string TraceId, string SpanId), string?>();
        foreach (var ancestor in ancestors)
        {
            parentSpanIds.TryAdd((ancestor.TraceId, ancestor.SpanId), ancestor.ParentSpanId);
        }
        foreach (var span in incomingSpansForExistingTraces)
        {
            var identity = (span.TraceId, span.SpanId);
            if (span.ParentSpanId is null || existingSpans.ContainsKey(identity) || !parentSpanIds.TryAdd(identity, span.ParentSpanId))
            {
                continue;
            }

            // Persisted and incoming edges can jointly complete a cycle. Add incoming edges in ingestion order so
            // only the edge that closes the cycle is rejected, matching OtlpTrace.AddSpan's partial-batch behavior.
            var visitedSpanIds = new HashSet<string>(StringComparer.Ordinal);
            var parentSpanId = span.ParentSpanId;
            while (parentSpanId is not null && visitedSpanIds.Add(parentSpanId))
            {
                if (string.Equals(parentSpanId, span.SpanId, StringComparison.Ordinal))
                {
                    circularSpanIds.Add(identity);
                    parentSpanIds.Remove(identity);
                    break;
                }

                parentSpanIds.TryGetValue((span.TraceId, parentSpanId), out parentSpanId);
            }
        }

        return new TraceIngestionState
        {
            ExistingTraces = existingTraces,
            ExistingSpans = existingSpans,
            ExistingParentReferences = existingParentReferences,
            CircularSpanIds = circularSpanIds
        };
    }

    private PendingSpan PrepareSpan(
        Dictionary<string, OtlpTrace> traces,
        TraceIngestionState ingestionState,
        long resourceId,
        long resourceViewId,
        OtlpResourceView resourceView,
        long scopeId,
        OtlpScope scope,
        Span span)
    {
        var traceId = span.TraceId.ToHexString();
        var spanId = span.SpanId.ToHexString();
        if (ingestionState.ExistingSpans.ContainsKey((traceId, spanId)))
        {
            throw new InvalidOperationException($"Duplicate span id '{spanId}' detected.");
        }
        if (ingestionState.CircularSpanIds.Contains((traceId, spanId)))
        {
            throw new InvalidOperationException($"Circular loop detected for span '{spanId}' with parent '{span.ParentSpanId.ToHexString()}'.");
        }

        var registerTrace = false;
        if (!traces.TryGetValue(traceId, out var trace))
        {
            var lastUpdatedDate = ingestionState.ExistingTraces.TryGetValue(traceId, out var existingTrace)
                ? new DateTime(existingTrace.LastUpdatedTimestampTicks, DateTimeKind.Utc)
                : DateTime.UtcNow;
            trace = new OtlpTrace(span.TraceId.Memory, lastUpdatedDate);
            registerTrace = true;
        }
        var modelSpan = CreateSqliteSpan(resourceView, trace, scope, span);
        trace.AddSpan(modelSpan);
        if (registerTrace)
        {
            traces.Add(traceId, trace);
        }

        return new PendingSpan(resourceId, resourceViewId, scopeId, modelSpan);
    }

    private static void UpsertTraces(
        SqliteConnection connection,
        IDbTransaction transaction,
        IEnumerable<OtlpTrace> traces,
        IReadOnlyDictionary<string, IngestionTraceRecord> existingTraces)
    {
        foreach (var batch in traces
            .Select(trace => CreateTraceUpsertRecord(trace, existingTraces.GetValueOrDefault(trace.TraceId)))
            .Chunk(MaxTraceBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_traces (
                    trace_id, first_span_timestamp_ticks, last_span_end_timestamp_ticks, duration_ticks, last_updated_timestamp_ticks, full_name,
                    primary_span_id, has_error, has_gen_ai)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                var trace = batch[index];
                sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @FirstSpanTimestampTicks{index}, @LastSpanEndTimestampTicks{index}, @DurationTicks{index}, @LastUpdatedTimestampTicks{index}, @FullName{index}, @PrimarySpanId{index}, @HasError{index}, @HasGenAI{index})");
                parameters.Add($"TraceId{index}", trace.TraceId);
                parameters.Add($"FirstSpanTimestampTicks{index}", trace.FirstSpanTimestampTicks);
                parameters.Add($"LastSpanEndTimestampTicks{index}", trace.LastSpanEndTimestampTicks);
                parameters.Add($"DurationTicks{index}", trace.LastSpanEndTimestampTicks - trace.FirstSpanTimestampTicks);
                parameters.Add($"LastUpdatedTimestampTicks{index}", trace.LastUpdatedTimestampTicks);
                parameters.Add($"FullName{index}", trace.FullName);
                parameters.Add($"PrimarySpanId{index}", trace.PrimarySpanId);
                parameters.Add($"HasError{index}", trace.HasError);
                parameters.Add($"HasGenAI{index}", trace.HasGenAI);
            }
            sql.Append("""
                ON CONFLICT(trace_id) DO UPDATE SET
                    first_span_timestamp_ticks = excluded.first_span_timestamp_ticks,
                    last_span_end_timestamp_ticks = excluded.last_span_end_timestamp_ticks,
                    duration_ticks = excluded.duration_ticks,
                    last_updated_timestamp_ticks = excluded.last_updated_timestamp_ticks,
                    full_name = excluded.full_name,
                    primary_span_id = excluded.primary_span_id,
                    has_error = excluded.has_error,
                    has_gen_ai = excluded.has_gen_ai;
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static TraceUpsertRecord CreateTraceUpsertRecord(OtlpTrace trace, IngestionTraceRecord? existingTrace)
    {
        var incomingPrimarySpan = GetPrimarySpan(trace);
        if (existingTrace is null)
        {
            return new TraceUpsertRecord(
                trace.TraceId,
                trace.TimeStamp.Ticks,
                trace.Spans.Max(span => span.EndTime.Ticks),
                trace.LastUpdatedDate.Ticks,
                trace.FullName,
                incomingPrimarySpan.SpanId,
                trace.Spans.Any(span => span.Status == OtlpSpanStatusCode.Error),
                HasGenAI(trace.Spans));
        }

        var existingPrimaryIsRoot = string.IsNullOrEmpty(existingTrace.PrimaryParentSpanId);
        var incomingPrimaryIsRoot = string.IsNullOrEmpty(incomingPrimarySpan.ParentSpanId);
        var useIncomingPrimary = IsPreferredPrimarySpan(
            incomingPrimaryIsRoot,
            incomingPrimarySpan.StartTime.Ticks,
            incomingPrimarySpan.SpanId,
            existingPrimaryIsRoot,
            existingTrace.PrimaryStartTimeTicks,
            existingTrace.PrimarySpanId);
        return new TraceUpsertRecord(
            trace.TraceId,
            Math.Min(existingTrace.FirstSpanTimestampTicks, trace.TimeStamp.Ticks),
            Math.Max(existingTrace.LastSpanEndTimestampTicks, trace.Spans.Max(span => span.EndTime.Ticks)),
            trace.LastUpdatedDate.Ticks,
            useIncomingPrimary
                ? $"{incomingPrimarySpan.Source.Resource.ResourceName}: {incomingPrimarySpan.Name}"
                : existingTrace.FullName,
            useIncomingPrimary ? incomingPrimarySpan.SpanId : existingTrace.PrimarySpanId,
            existingTrace.HasError || trace.Spans.Any(span => span.Status == OtlpSpanStatusCode.Error),
            existingTrace.HasGenAI || HasGenAI(trace.Spans));

        static bool HasGenAI(IEnumerable<OtlpSpan> spans) => spans.Any(span => span.Attributes.Any(attribute =>
            (attribute.Key is "gen_ai.system" or "gen_ai.provider.name") && attribute.Value.Length > 0));
    }

    private static OtlpSpan GetPrimarySpan(OtlpTrace trace)
    {
        var primarySpan = trace.Spans[0];
        foreach (var span in trace.Spans.Skip(1))
        {
            if (IsPreferredPrimarySpan(
                string.IsNullOrEmpty(span.ParentSpanId),
                span.StartTime.Ticks,
                span.SpanId,
                string.IsNullOrEmpty(primarySpan.ParentSpanId),
                primarySpan.StartTime.Ticks,
                primarySpan.SpanId))
            {
                primarySpan = span;
            }
        }

        return primarySpan;
    }

    private static bool IsPreferredPrimarySpan(
        bool candidateIsRoot,
        long candidateStartTimeTicks,
        string candidateSpanId,
        bool currentIsRoot,
        long currentStartTimeTicks,
        string currentSpanId)
    {
        if (candidateIsRoot != currentIsRoot)
        {
            return candidateIsRoot;
        }

        if (candidateStartTimeTicks != currentStartTimeTicks)
        {
            return candidateStartTimeTicks < currentStartTimeTicks;
        }

        // Equal-time spans are inserted before existing spans by OtlpTrace, so the last span ID in
        // database order becomes primary when a trace is materialized.
        return string.CompareOrdinal(candidateSpanId, currentSpanId) > 0;
    }

    private static void InsertSpans(SqliteConnection connection, IDbTransaction transaction, List<PendingSpan> spans)
    {
        DbCommand? command = null;
        try
        {
            foreach (var batch in spans.Chunk(MaxSpanBatchSize))
            {
                if (command is null || command.Parameters.Count != batch.Length * 13)
                {
                    command?.Dispose();
                    command = CreateInsertSpansCommand(connection, transaction, batch.Length);
                }

                for (var index = 0; index < batch.Length; index++)
                {
                    var parameterIndex = index * 13;
                    var pendingSpan = batch[index];
                    var span = pendingSpan.Span;
                    command.Parameters[parameterIndex++].Value = span.TraceId;
                    command.Parameters[parameterIndex++].Value = span.SpanId;
                    command.Parameters[parameterIndex++].Value = span.ParentSpanId ?? (object)DBNull.Value;
                    command.Parameters[parameterIndex++].Value = pendingSpan.ResourceId;
                    command.Parameters[parameterIndex++].Value = pendingSpan.ResourceViewId;
                    command.Parameters[parameterIndex++].Value = pendingSpan.ScopeId;
                    command.Parameters[parameterIndex++].Value = span.Name;
                    command.Parameters[parameterIndex++].Value = (int)span.Kind;
                    command.Parameters[parameterIndex++].Value = span.StartTime.Ticks;
                    command.Parameters[parameterIndex++].Value = span.EndTime.Ticks;
                    command.Parameters[parameterIndex++].Value = (int)span.Status;
                    command.Parameters[parameterIndex++].Value = span.StatusMessage ?? (object)DBNull.Value;
                    command.Parameters[parameterIndex].Value = span.State ?? (object)DBNull.Value;
                }

                command.ExecuteNonQuery();
            }
        }
        finally
        {
            command?.Dispose();
        }
    }

    private static DbCommand CreateInsertSpansCommand(SqliteConnection connection, IDbTransaction transaction, int batchSize)
    {
        var command = ((DbConnection)connection).CreateCommand();
        command.Transaction = (DbTransaction)transaction;
        var sql = new StringBuilder("""
                INSERT INTO telemetry_spans (
                    trace_id, span_id, parent_span_id, resource_id, resource_view_id, scope_id, name, kind,
                    start_time_ticks, end_time_ticks, status, status_message, trace_state, resource_order_ticks)
                VALUES
                """);
        for (var index = 0; index < batchSize; index++)
        {
            if (index > 0)
            {
                sql.AppendLine(",");
            }
            sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @SpanId{index}, @ParentSpanId{index}, @ResourceId{index}, @ResourceViewId{index}, @ScopeId{index}, @Name{index}, @Kind{index}, @StartTimeTicks{index}, @EndTimeTicks{index}, @Status{index}, @StatusMessage{index}, @State{index}, @StartTimeTicks{index})");
            AddParameter(command, $"@TraceId{index}");
            AddParameter(command, $"@SpanId{index}");
            AddParameter(command, $"@ParentSpanId{index}");
            AddParameter(command, $"@ResourceId{index}");
            AddParameter(command, $"@ResourceViewId{index}");
            AddParameter(command, $"@ScopeId{index}");
            AddParameter(command, $"@Name{index}");
            AddParameter(command, $"@Kind{index}");
            AddParameter(command, $"@StartTimeTicks{index}");
            AddParameter(command, $"@EndTimeTicks{index}");
            AddParameter(command, $"@Status{index}");
            AddParameter(command, $"@StatusMessage{index}");
            AddParameter(command, $"@State{index}");
        }
        sql.Append(';');
        command.CommandText = sql.ToString();
        command.Prepare();
        return command;
    }

    private static void MarkResourcesHaveTraces(SqliteConnection connection, IDbTransaction transaction, HashSet<CachedResource> resources)
    {
        var resourcesToUpdate = resources.Where(resource => !resource.Resource.HasTraces).ToArray();
        foreach (var batch in resourcesToUpdate.Chunk(MaxTraceBatchSize))
        {
            connection.Execute(
                "UPDATE telemetry_resources SET has_traces = 1 WHERE resource_id IN @ResourceIds;",
                new { ResourceIds = batch.Select(resource => resource.ResourceId).ToArray() },
                transaction);
        }
        foreach (var resource in resourcesToUpdate)
        {
            resource.Resource.HasTraces = true;
        }
    }

    private HashSet<string> UpdateUninstrumentedPeers(
        SqliteConnection connection,
        IDbTransaction transaction,
        IEnumerable<OtlpTrace> traces,
        TraceIngestionState ingestionState)
    {
        var traceList = traces.ToList();
        var incomingParentReferences = traceList
            .SelectMany(trace => trace.Spans)
            .Where(span => span.ParentSpanId is not null)
            .Select(span => (span.TraceId, ParentSpanId: span.ParentSpanId!))
            .ToHashSet();
        var peerResourceIds = new HashSet<long>();
        var spanUpdates = new List<PeerSpanUpdateRecord>();
        var peerChangedTraceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trace in traceList)
        {
            foreach (var span in trace.Spans)
            {
                OtlpResource? peer = null;
                long? peerResourceId = null;
                var hasPeerAddress = OtlpHelpers.GetPeerAddress(span.Attributes) is not null;
                var hasChildren = incomingParentReferences.Contains((span.TraceId, span.SpanId)) ||
                    ingestionState.ExistingParentReferences.Contains((span.TraceId, span.SpanId));
                if (hasPeerAddress && span.Kind is OtlpSpanKind.Client or OtlpSpanKind.Producer && !hasChildren)
                {
                    if (TryResolvePeerResourceKey(span.Attributes, out var peerKey))
                    {
                        var cachedPeerResource = GetOrAddCachedResource(connection, transaction, peerKey, uninstrumentedPeer: true);
                        peerResourceId = cachedPeerResource.ResourceId;
                        peerResourceIds.Add(cachedPeerResource.ResourceId);
                        peer = cachedPeerResource.Resource;
                    }
                }

                trace.SetSpanUninstrumentedPeer(span, peer);
                spanUpdates.Add(new PeerSpanUpdateRecord
                {
                    PeerResourceId = peerResourceId,
                    TraceId = span.TraceId,
                    SpanId = span.SpanId
                });
            }

            foreach (var span in trace.Spans)
            {
                if (span.ParentSpanId is not null &&
                    ingestionState.ExistingSpans.TryGetValue((trace.TraceId, span.ParentSpanId), out var existingParent) &&
                    existingParent.UninstrumentedPeerResourceId is not null)
                {
                    spanUpdates.Add(new PeerSpanUpdateRecord
                    {
                        PeerResourceId = null,
                        TraceId = trace.TraceId,
                        SpanId = span.ParentSpanId!
                    });
                    peerChangedTraceIds.Add(trace.TraceId);
                }
            }
        }

        if (peerResourceIds.Count > 0)
        {
            connection.Execute(
                "UPDATE telemetry_resources SET uninstrumented_peer = 1 WHERE resource_id IN @ResourceIds;",
                new { ResourceIds = peerResourceIds.ToArray() },
                transaction);
        }

        UpdatePeerSpans(connection, transaction, spanUpdates);
        return peerChangedTraceIds;
    }

    private static void UpdatePeerSpans(SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<PeerSpanUpdateRecord> spanUpdates)
    {
        foreach (var batch in spanUpdates.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("""
                WITH peer_updates(trace_id, span_id, peer_resource_id) AS (
                    VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"        (@TraceId{index}, @SpanId{index}, @PeerResourceId{index})");
                parameters.Add($"TraceId{index}", batch[index].TraceId);
                parameters.Add($"SpanId{index}", batch[index].SpanId);
                parameters.Add($"PeerResourceId{index}", batch[index].PeerResourceId);
            }
            sql.Append("""
                )
                UPDATE telemetry_spans AS spans
                SET uninstrumented_peer_resource_id = peer_updates.peer_resource_id
                FROM peer_updates
                WHERE spans.trace_id = peer_updates.trace_id
                  AND spans.span_id = peer_updates.span_id;
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void UpdateTraceResourceSummaries(
        SqliteConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<PendingSpan> pendingSpans,
        TraceIngestionState ingestionState,
        IReadOnlySet<string> peerChangedTraceIds)
    {
        var rebuildTraceIds = new HashSet<string>(peerChangedTraceIds, StringComparer.Ordinal);
        foreach (var pendingSpan in pendingSpans)
        {
            var span = pendingSpan.Span;
            if (!ingestionState.ExistingTraces.ContainsKey(span.TraceId) ||
                ingestionState.ExistingParentReferences.Contains((span.TraceId, span.SpanId)))
            {
                rebuildTraceIds.Add(span.TraceId);
            }
        }

        RebuildTraceResourceSummaries(connection, transaction, rebuildTraceIds);

        var incrementalSpans = pendingSpans
            .Where(pendingSpan => !rebuildTraceIds.Contains(pendingSpan.Span.TraceId))
            .ToArray();
        UpdateSpanOrders(
            connection,
            transaction,
            incrementalSpans.Select(pendingSpan =>
            {
                var span = pendingSpan.Span;
                var resourceOrderTicks = span.StartTime.Ticks;
                var currentSpan = span;
                while (currentSpan.ParentSpanId is { } parentSpanId)
                {
                    if (!currentSpan.Trace.Spans.TryGetValue(parentSpanId, out var parent))
                    {
                        if (ingestionState.ExistingSpans.TryGetValue((span.TraceId, parentSpanId), out var existingParent))
                        {
                            resourceOrderTicks = Math.Max(resourceOrderTicks, existingParent.ResourceOrderTicks);
                        }
                        break;
                    }

                    resourceOrderTicks = Math.Max(resourceOrderTicks, parent.StartTime.Ticks);
                    currentSpan = parent;
                }
                return new SpanOrderUpdateRecord(span.TraceId, span.SpanId, resourceOrderTicks);
            }));
        AddTraceResourceAggregateDeltas(connection, transaction, incrementalSpans);
    }

    private static void RebuildTraceResourceSummaries(SqliteConnection connection, IDbTransaction transaction, IEnumerable<string> traceIds)
    {
        foreach (var traceBatch in traceIds.Chunk(MaxTraceBatchSize))
        {
            connection.Execute("""
                WITH RECURSIVE span_tree(trace_id, span_id, resource_order_ticks) AS (
                    SELECT s.trace_id, s.span_id, s.start_time_ticks
                    FROM telemetry_spans s
                    WHERE s.trace_id IN @TraceIds
                      AND (s.parent_span_id IS NULL OR NOT EXISTS (
                          SELECT 1
                          FROM telemetry_spans parent
                          WHERE parent.trace_id = s.trace_id AND parent.span_id = s.parent_span_id))
                    UNION ALL
                    SELECT child.trace_id, child.span_id, MAX(child.start_time_ticks, parent.resource_order_ticks)
                    FROM span_tree parent
                    JOIN telemetry_spans child ON child.trace_id = parent.trace_id AND child.parent_span_id = parent.span_id
                )
                UPDATE telemetry_spans AS spans
                SET resource_order_ticks = span_tree.resource_order_ticks
                FROM span_tree
                WHERE spans.trace_id = span_tree.trace_id AND spans.span_id = span_tree.span_id;
                """, new { TraceIds = traceBatch }, transaction);

            RebuildTraceResourceAggregates(connection, transaction, traceBatch);
        }
    }

    private static void UpdateSpanOrders(
        SqliteConnection connection,
        IDbTransaction transaction,
        IEnumerable<SpanOrderUpdateRecord> orderUpdates)
    {
        foreach (var orderBatch in orderUpdates.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("WITH span_orders(trace_id, span_id, resource_order_ticks) AS (VALUES\n");
            var parameters = new DynamicParameters();
            for (var index = 0; index < orderBatch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @SpanId{index}, @ResourceOrderTicks{index})");
                parameters.Add($"TraceId{index}", orderBatch[index].TraceId);
                parameters.Add($"SpanId{index}", orderBatch[index].SpanId);
                parameters.Add($"ResourceOrderTicks{index}", orderBatch[index].ResourceOrderTicks);
            }
            sql.Append("""
                )
                UPDATE telemetry_spans AS spans
                SET resource_order_ticks = span_orders.resource_order_ticks
                FROM span_orders
                WHERE spans.trace_id = span_orders.trace_id
                  AND spans.span_id = span_orders.span_id;
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void AddTraceResourceAggregateDeltas(
        SqliteConnection connection,
        IDbTransaction transaction,
        IEnumerable<PendingSpan> pendingSpans)
    {
        foreach (var spanBatch in pendingSpans.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("WITH new_spans(trace_id, span_id) AS (VALUES\n");
            var parameters = new DynamicParameters();
            for (var index = 0; index < spanBatch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @SpanId{index})");
                parameters.Add($"TraceId{index}", spanBatch[index].Span.TraceId);
                parameters.Add($"SpanId{index}", spanBatch[index].Span.SpanId);
            }
            sql.Append("""
                )
                INSERT INTO telemetry_trace_resources (
                    trace_id, resource_id, resource_order_ticks, total_spans, errored_spans)
                SELECT
                    resources.trace_id,
                    resources.resource_id,
                    MIN(resources.resource_order_ticks),
                    COUNT(*),
                    SUM(CASE WHEN resources.status = 2 THEN 1 ELSE 0 END)
                FROM (
                    SELECT s.trace_id, s.resource_id, s.resource_order_ticks, s.status
                    FROM telemetry_spans s
                    JOIN new_spans n ON n.trace_id = s.trace_id AND n.span_id = s.span_id

                    UNION ALL

                    SELECT s.trace_id, s.uninstrumented_peer_resource_id, s.resource_order_ticks, s.status
                    FROM telemetry_spans s
                    JOIN new_spans n ON n.trace_id = s.trace_id AND n.span_id = s.span_id
                    WHERE s.uninstrumented_peer_resource_id IS NOT NULL
                ) resources
                GROUP BY resources.trace_id, resources.resource_id
                ON CONFLICT(trace_id, resource_id) DO UPDATE SET
                    resource_order_ticks = MIN(telemetry_trace_resources.resource_order_ticks, excluded.resource_order_ticks),
                    total_spans = telemetry_trace_resources.total_spans + excluded.total_spans,
                    errored_spans = telemetry_trace_resources.errored_spans + excluded.errored_spans;
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void RebuildTraceResourceAggregates(SqliteConnection connection, IDbTransaction transaction, IEnumerable<string> traceIds)
    {
        foreach (var traceIdBatch in traceIds.Chunk(MaxTraceBatchSize))
        {
            connection.Execute("""
                DELETE FROM telemetry_trace_resources
                WHERE trace_id IN @TraceIds;

                INSERT INTO telemetry_trace_resources (
                    trace_id, resource_id, resource_order_ticks, total_spans, errored_spans)
                SELECT
                    resources.trace_id,
                    resources.resource_id,
                    MIN(resources.resource_order_ticks),
                    COUNT(*),
                    SUM(CASE WHEN resources.status = 2 THEN 1 ELSE 0 END)
                FROM (
                    SELECT trace_id, resource_id, resource_order_ticks, status
                    FROM telemetry_spans
                    WHERE trace_id IN @TraceIds

                    UNION ALL

                    SELECT trace_id, uninstrumented_peer_resource_id, resource_order_ticks, status
                    FROM telemetry_spans
                    WHERE trace_id IN @TraceIds
                      AND uninstrumented_peer_resource_id IS NOT NULL
                ) resources
                GROUP BY resources.trace_id, resources.resource_id;
                """, new { TraceIds = traceIdBatch }, transaction);
        }
    }

    private static void InsertSpanDetails(SqliteConnection connection, IDbTransaction transaction, List<PendingSpan> pendingSpans)
    {
        InsertSpanAttributes(connection, transaction, pendingSpans);

        var events = pendingSpans
            .SelectMany(pendingSpan => pendingSpan.Span.Events.Select((spanEvent, ordinal) => new PendingSpanEvent(
                spanEvent.InternalId.ToString("D"),
                pendingSpan.Span.TraceId,
                pendingSpan.Span.SpanId,
                ordinal,
                spanEvent)))
            .ToArray();
        InsertSpanEvents(connection, transaction, events);
        InsertSpanEventAttributes(connection, transaction, events);

        var links = pendingSpans
            .SelectMany(pendingSpan => pendingSpan.Span.Links.Select(link => new PendingSpanLink(link)))
            .ToArray();
        InsertSpanLinks(connection, transaction, links);
        InsertSpanLinkAttributes(connection, transaction, links);
    }

    private static void InsertSpanAttributes(SqliteConnection connection, IDbTransaction transaction, List<PendingSpan> pendingSpans)
    {
        var attributes = pendingSpans
            .SelectMany(pendingSpan => pendingSpan.Span.Attributes.Select((attribute, ordinal) => new
            {
                pendingSpan.Span.TraceId,
                pendingSpan.Span.SpanId,
                Ordinal = ordinal,
                attribute.Key,
                attribute.Value
            }))
            .ToArray();
        DbCommand? command = null;
        try
        {
            foreach (var batch in attributes.Chunk(MaxSpanDetailBatchSize))
            {
                if (command is null || command.Parameters.Count != batch.Length * 5)
                {
                    command?.Dispose();
                    command = CreateInsertSpanAttributesCommand(connection, transaction, batch.Length);
                }

                for (var index = 0; index < batch.Length; index++)
                {
                    var parameterIndex = index * 5;
                    command.Parameters[parameterIndex++].Value = batch[index].TraceId;
                    command.Parameters[parameterIndex++].Value = batch[index].SpanId;
                    command.Parameters[parameterIndex++].Value = batch[index].Ordinal;
                    command.Parameters[parameterIndex++].Value = batch[index].Key;
                    command.Parameters[parameterIndex].Value = batch[index].Value;
                }

                command.ExecuteNonQuery();
            }
        }
        finally
        {
            command?.Dispose();
        }
    }

    private static DbCommand CreateInsertSpanAttributesCommand(SqliteConnection connection, IDbTransaction transaction, int batchSize)
    {
        var command = ((DbConnection)connection).CreateCommand();
        command.Transaction = (DbTransaction)transaction;
        var sql = new StringBuilder("""
                INSERT INTO telemetry_span_attributes (trace_id, span_id, ordinal, attribute_key, attribute_value)
                VALUES
                """);
        for (var index = 0; index < batchSize; index++)
        {
            if (index > 0)
            {
                sql.AppendLine(",");
            }
            sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @SpanId{index}, @Ordinal{index}, @Key{index}, @Value{index})");
            AddParameter(command, $"@TraceId{index}");
            AddParameter(command, $"@SpanId{index}");
            AddParameter(command, $"@Ordinal{index}");
            AddParameter(command, $"@Key{index}");
            AddParameter(command, $"@Value{index}");
        }
        sql.Append(';');
        command.CommandText = sql.ToString();
        command.Prepare();
        return command;
    }

    private static void AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void InsertSpanEvents(SqliteConnection connection, IDbTransaction transaction, PendingSpanEvent[] events)
    {
        foreach (var batch in events.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_span_events (event_id, trace_id, span_id, ordinal, event_name, event_time_ticks)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@EventId{index}, @TraceId{index}, @SpanId{index}, @Ordinal{index}, @Name{index}, @TimeTicks{index})");
                parameters.Add($"EventId{index}", batch[index].EventId);
                parameters.Add($"TraceId{index}", batch[index].TraceId);
                parameters.Add($"SpanId{index}", batch[index].SpanId);
                parameters.Add($"Ordinal{index}", batch[index].Ordinal);
                parameters.Add($"Name{index}", batch[index].Event.Name);
                parameters.Add($"TimeTicks{index}", batch[index].Event.Time.Ticks);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void InsertSpanEventAttributes(SqliteConnection connection, IDbTransaction transaction, PendingSpanEvent[] events)
    {
        var attributes = events
            .SelectMany(spanEvent => spanEvent.Event.Attributes.Select((attribute, ordinal) => new
            {
                spanEvent.EventId,
                Ordinal = ordinal,
                attribute.Key,
                attribute.Value
            }))
            .ToArray();
        foreach (var batch in attributes.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_span_event_attributes (event_id, ordinal, attribute_key, attribute_value)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@EventId{index}, @Ordinal{index}, @Key{index}, @Value{index})");
                parameters.Add($"EventId{index}", batch[index].EventId);
                parameters.Add($"Ordinal{index}", batch[index].Ordinal);
                parameters.Add($"Key{index}", batch[index].Key);
                parameters.Add($"Value{index}", batch[index].Value);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void InsertSpanLinks(SqliteConnection connection, IDbTransaction transaction, PendingSpanLink[] links)
    {
        foreach (var batch in links.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder();
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                var link = batch[index].Link;
                sql.Append(CultureInfo.InvariantCulture, $$"""
                    INSERT INTO telemetry_span_links (
                        source_trace_id, source_span_id, target_trace_id, target_span_id, trace_state)
                    VALUES (@SourceTraceId{{index}}, @SourceSpanId{{index}}, @TraceId{{index}}, @SpanId{{index}}, @TraceState{{index}})
                    RETURNING link_id;
                    """);
                parameters.Add($"SourceTraceId{index}", link.SourceTraceId);
                parameters.Add($"SourceSpanId{index}", link.SourceSpanId);
                parameters.Add($"TraceId{index}", link.TraceId);
                parameters.Add($"SpanId{index}", link.SpanId);
                parameters.Add($"TraceState{index}", link.TraceState);
            }

            using var reader = connection.QueryMultiple(sql.ToString(), parameters, transaction);
            for (var index = 0; index < batch.Length; index++)
            {
                batch[index].LinkId = reader.ReadSingle<long>();
            }
        }
    }

    private static void InsertSpanLinkAttributes(SqliteConnection connection, IDbTransaction transaction, PendingSpanLink[] links)
    {
        var attributes = links
            .SelectMany(link => link.Link.Attributes.Select((attribute, ordinal) => new
            {
                link.LinkId,
                Ordinal = ordinal,
                attribute.Key,
                attribute.Value
            }))
            .ToArray();
        foreach (var batch in attributes.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_span_link_attributes (link_id, ordinal, attribute_key, attribute_value)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@LinkId{index}, @Ordinal{index}, @Key{index}, @Value{index})");
                parameters.Add($"LinkId{index}", batch[index].LinkId);
                parameters.Add($"Ordinal{index}", batch[index].Ordinal);
                parameters.Add($"Key{index}", batch[index].Key);
                parameters.Add($"Value{index}", batch[index].Value);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private OtlpSpan CreateSqliteSpan(OtlpResourceView resourceView, OtlpTrace trace, OtlpScope scope, Span span)
    {
        var spanId = span.SpanId?.ToHexString();
        if (spanId is null)
        {
            throw new ArgumentException("Span has no SpanId");
        }

        var modelSpan = new OtlpSpan(resourceView, trace, scope)
        {
            SpanId = spanId,
            ParentSpanId = span.ParentSpanId?.ToHexString(),
            Name = span.Name,
            Kind = OtlpHelpers.ConvertSpanKind(span.Kind),
            StartTime = OtlpHelpers.UnixNanoSecondsToDateTime(span.StartTimeUnixNano),
            EndTime = OtlpHelpers.UnixNanoSecondsToDateTime(span.EndTimeUnixNano),
            Status = ConvertSqliteStatus(span.Status),
            StatusMessage = span.Status?.Message,
            Attributes = span.Attributes.ToKeyValuePairs(_otlpContext, filter: attribute => attribute.Key != OtlpHelpers.AspireDestinationNameAttribute),
            State = !string.IsNullOrEmpty(span.TraceState) ? span.TraceState : null,
            Events = [],
            Links = [],
            BackLinks = []
        };

        foreach (var spanEvent in span.Events.OrderBy(spanEvent => spanEvent.TimeUnixNano).Take(_otlpContext.Options.MaxSpanEventCount))
        {
            modelSpan.Events.Add(new OtlpSpanEvent(modelSpan)
            {
                InternalId = Guid.NewGuid(),
                Name = spanEvent.Name,
                Time = OtlpHelpers.UnixNanoSecondsToDateTime(spanEvent.TimeUnixNano),
                Attributes = spanEvent.Attributes.ToKeyValuePairs(_otlpContext)
            });
        }

        foreach (var link in span.Links)
        {
            modelSpan.Links.Add(new OtlpSpanLink
            {
                SourceSpanId = spanId,
                SourceTraceId = trace.TraceId,
                TraceState = link.TraceState,
                SpanId = link.SpanId.ToHexString(),
                TraceId = link.TraceId.ToHexString(),
                Attributes = link.Attributes.ToKeyValuePairs(_otlpContext)
            });
        }

        return modelSpan;
    }

    private static OtlpSpanStatusCode ConvertSqliteStatus(Status? status)
    {
        return status?.Code switch
        {
            Status.Types.StatusCode.Ok => OtlpSpanStatusCode.Ok,
            Status.Types.StatusCode.Error => OtlpSpanStatusCode.Error,
            _ => OtlpSpanStatusCode.Unset
        };
    }

    private void TrimTracesToCapacity(SqliteConnection connection, IDbTransaction transaction)
    {
        connection.Execute("""
            DELETE FROM telemetry_traces
            WHERE trace_id IN (
                SELECT trace_id
                FROM telemetry_traces
                ORDER BY first_span_timestamp_ticks, trace_id
                LIMIT MAX((SELECT COUNT(*) FROM telemetry_traces) - @MaxTraceCount, 0)
            );

            DELETE FROM telemetry_resource_views
            WHERE NOT EXISTS (SELECT 1 FROM telemetry_logs WHERE telemetry_logs.resource_view_id = telemetry_resource_views.resource_view_id)
              AND NOT EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_view_id = telemetry_resource_views.resource_view_id);

            UPDATE telemetry_resources
            SET has_traces = EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_id = telemetry_resources.resource_id);
            """, new { _otlpContext.Options.MaxTraceCount }, transaction);
    }

    private void ClearSelectedTracesFromDatabase(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        using var connection = _database.OpenConnection();
        var resources = connection.Query<TelemetryResourceRecord>("""
            SELECT resource_name AS ResourceName, instance_id AS InstanceId
            FROM telemetry_resources;
            """);
        foreach (var resource in resources)
        {
            var key = new ResourceKey(resource.ResourceName, resource.InstanceId);
            if (selectedResources.TryGetValue(key.GetCompositeName(), out var dataTypes) &&
                dataTypes.Contains(AspireDataType.Traces) &&
                !dataTypes.Contains(AspireDataType.Resource))
            {
                ClearTracesFromDatabase(key);
            }
        }
    }

    private void RecalculateUninstrumentedPeers()
    {
        lock (_writeLock)
        {
            using var writeConnection = _database.OpenConnection();
            using var transaction = writeConnection.BeginTransaction();
            using var readConnection = _database.OpenConnection();
            // Return one row per span attribute (or one row with null attributes for spans without any).
            // The parents CTE marks spans that have children so processing below can restrict peer
            // resolution to client/producer leaf spans that have a peer address. Ordering keeps every
            // span's attributes contiguous, which lets the loop finalize one span when its identity
            // changes instead of buffering all spans and attributes. A separate connection keeps the
            // unbuffered reader open while completed spans are written in batches through the transaction.
            var rows = readConnection.Query<PeerRecalculationRowRecord>("""
                WITH parents AS (
                    SELECT DISTINCT trace_id, parent_span_id AS span_id
                    FROM telemetry_spans
                    WHERE parent_span_id IS NOT NULL
                )
                SELECT
                    spans.trace_id AS TraceId,
                    spans.span_id AS SpanId,
                    spans.kind AS Kind,
                    parents.span_id IS NOT NULL AS HasChildren,
                    attributes.attribute_key AS AttributeKey,
                    attributes.attribute_value AS AttributeValue
                FROM telemetry_spans AS spans
                LEFT JOIN parents
                    ON parents.trace_id = spans.trace_id
                   AND parents.span_id = spans.span_id
                LEFT JOIN telemetry_span_attributes AS attributes
                    ON attributes.trace_id = spans.trace_id
                   AND attributes.span_id = spans.span_id
                ORDER BY spans.trace_id, spans.span_id, attributes.ordinal;
                """, buffered: false);
            var spanUpdates = new List<PeerSpanUpdateRecord>(MaxSpanDetailBatchSize);
            var spanAttributes = new List<KeyValuePair<string, string>>();
            PeerRecalculationRowRecord? currentSpan = null;

            foreach (var row in rows)
            {
                if (currentSpan is not null &&
                    (!string.Equals(currentSpan.TraceId, row.TraceId, StringComparison.Ordinal) ||
                     !string.Equals(currentSpan.SpanId, row.SpanId, StringComparison.Ordinal)))
                {
                    ProcessSpan(currentSpan, spanAttributes);
                    spanAttributes.Clear();
                }

                currentSpan = row;
                if (row.AttributeKey is not null)
                {
                    spanAttributes.Add(KeyValuePair.Create(row.AttributeKey, row.AttributeValue!));
                }
            }
            if (currentSpan is not null)
            {
                ProcessSpan(currentSpan, spanAttributes);
            }
            FlushSpanUpdates();
            RebuildTraceResourceAggregates(
                writeConnection,
                transaction,
                writeConnection.Query<string>("SELECT trace_id FROM telemetry_traces;", transaction: transaction));

            writeConnection.Execute("""
                UPDATE telemetry_resources
                SET uninstrumented_peer = 1
                WHERE resource_id IN (
                    SELECT uninstrumented_peer_resource_id
                    FROM telemetry_spans
                    WHERE uninstrumented_peer_resource_id IS NOT NULL
                );
                """, transaction: transaction);
            var lastUpdatedTimestampTicks = DateTime.UtcNow.Ticks;
            writeConnection.Execute("""
                UPDATE telemetry_traces
                SET last_updated_timestamp_ticks = @LastUpdatedTimestampTicks
                WHERE trace_id IN (SELECT trace_id FROM telemetry_spans);
                """, new
            {
                LastUpdatedTimestampTicks = lastUpdatedTimestampTicks
            }, transaction);
            transaction.Commit();

            void ProcessSpan(PeerRecalculationRowRecord span, IReadOnlyList<KeyValuePair<string, string>> attributes)
            {
                long? peerResourceId = null;
                if ((OtlpSpanKind)span.Kind is OtlpSpanKind.Client or OtlpSpanKind.Producer &&
                    !span.HasChildren &&
                    attributes.Count > 0)
                {
                    var attributeArray = attributes.ToArray();
                    if (attributeArray.GetPeerAddress() is not null &&
                        TryResolvePeerResourceKey(attributeArray, out var peerKey))
                    {
                        var cachedPeerResource = GetOrAddCachedResource(writeConnection, transaction, peerKey, uninstrumentedPeer: true);
                        peerResourceId = cachedPeerResource.ResourceId;
                    }
                }

                spanUpdates.Add(new PeerSpanUpdateRecord
                {
                    PeerResourceId = peerResourceId,
                    TraceId = span.TraceId,
                    SpanId = span.SpanId
                });
                if (spanUpdates.Count == MaxSpanDetailBatchSize)
                {
                    FlushSpanUpdates();
                }
            }

            void FlushSpanUpdates()
            {
                if (spanUpdates.Count == 0)
                {
                    return;
                }

                UpdatePeerSpans(writeConnection, transaction, spanUpdates);
                spanUpdates.Clear();
            }
        }
    }

    private bool TryResolvePeerResourceKey(KeyValuePair<string, string>[] attributes, out ResourceKey peerKey)
    {
        foreach (var resolver in _outgoingPeerResolvers)
        {
            if (!resolver.TryResolvePeer(attributes, out var name, out var matchedResource))
            {
                continue;
            }

            if (matchedResource is not null)
            {
                peerKey = ResourceKey.Create(matchedResource.DisplayName, matchedResource.Name);
                return true;
            }

            if (!string.IsNullOrEmpty(name))
            {
                peerKey = new ResourceKey(name, InstanceId: null);
                return true;
            }
        }

        peerKey = default;
        return false;
    }

    private void ClearTracesFromDatabase(ResourceKey? resourceKey)
    {
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var parameters = new DynamicParameters();
            var where = string.Empty;
            if (resourceKey is not null)
            {
                where = " WHERE resource_name = @ResourceName COLLATE NOCASE";
                parameters.Add("ResourceName", resourceKey.Value.Name);
                if (resourceKey.Value.InstanceId is not null)
                {
                    where += " AND instance_id = @InstanceId COLLATE NOCASE";
                    parameters.Add("InstanceId", resourceKey.Value.InstanceId);
                }
            }
            parameters.Add("ClearAll", resourceKey is null);

            connection.Execute($"""
                DELETE FROM telemetry_traces
                WHERE @ClearAll OR trace_id IN (
                    SELECT DISTINCT trace_id
                    FROM telemetry_spans
                    WHERE resource_id IN (SELECT resource_id FROM telemetry_resources{where})
                );

                DELETE FROM telemetry_resource_views
                WHERE NOT EXISTS (SELECT 1 FROM telemetry_logs WHERE telemetry_logs.resource_view_id = telemetry_resource_views.resource_view_id)
                  AND NOT EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_view_id = telemetry_resource_views.resource_view_id);

                UPDATE telemetry_resources
                SET has_traces = EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_id = telemetry_resources.resource_id);
                """, parameters, transaction);
            DeleteOrphanedScopes(connection, transaction);
            transaction.Commit();
            ClearMetadataCache();
        }
    }

    private sealed record PendingSpan(long ResourceId, long ResourceViewId, long ScopeId, OtlpSpan Span);

    private readonly record struct IncomingSpanIdentity(string TraceId, string SpanId, string? ParentSpanId);

    private sealed record SpanOrderUpdateRecord(string TraceId, string SpanId, long ResourceOrderTicks);

    private sealed record PendingSpanEvent(string EventId, string TraceId, string SpanId, int Ordinal, OtlpSpanEvent Event);

    private sealed class TraceIngestionState
    {
        public required IReadOnlyDictionary<string, IngestionTraceRecord> ExistingTraces { get; init; }
        public required IReadOnlyDictionary<(string TraceId, string SpanId), IngestionExistingSpanRecord> ExistingSpans { get; init; }
        public required IReadOnlySet<(string TraceId, string ParentSpanId)> ExistingParentReferences { get; init; }
        public required IReadOnlySet<(string TraceId, string SpanId)> CircularSpanIds { get; init; }
    }

    private sealed class IngestionTraceRecord
    {
        public required string TraceId { get; init; }
        public required long FirstSpanTimestampTicks { get; init; }
        public required long LastSpanEndTimestampTicks { get; init; }
        public required long LastUpdatedTimestampTicks { get; init; }
        public required string FullName { get; init; }
        public required string PrimarySpanId { get; init; }
        public required bool HasError { get; init; }
        public required bool HasGenAI { get; init; }
        public string? PrimaryParentSpanId { get; init; }
        public required long PrimaryStartTimeTicks { get; init; }
    }

    private sealed class IngestionExistingSpanRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public required long ResourceOrderTicks { get; init; }
        public long? UninstrumentedPeerResourceId { get; init; }
    }

    private sealed class IngestionParentReferenceRecord
    {
        public required string TraceId { get; init; }
        public required string ParentSpanId { get; init; }
    }

    private sealed class IngestionAncestorRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public string? ParentSpanId { get; init; }
    }

    private sealed record TraceUpsertRecord(
        string TraceId,
        long FirstSpanTimestampTicks,
        long LastSpanEndTimestampTicks,
        long LastUpdatedTimestampTicks,
        string FullName,
        string PrimarySpanId,
        bool HasError,
        bool HasGenAI);

    private sealed class PendingSpanLink(OtlpSpanLink link)
    {
        public OtlpSpanLink Link { get; } = link;
        public long LinkId { get; set; }
    }

    private sealed class PeerRecalculationRowRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public required int Kind { get; init; }
        public required bool HasChildren { get; init; }
        public string? AttributeKey { get; init; }
        public string? AttributeValue { get; init; }
    }

    private sealed class PeerSpanUpdateRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public long? PeerResourceId { get; init; }
    }
}