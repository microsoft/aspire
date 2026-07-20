// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Globalization;
using System.Text;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Dapper;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int MaxTraceBatchSize = 100;
    private const int MaxSpanBatchSize = 50;
    private const int MaxSpanDetailBatchSize = 100;

    private List<OtlpSpan> AddTracesToDatabase(AddContext context, RepeatedField<ResourceSpans> resourceSpans)
    {
        var addedSpans = new List<OtlpSpan>();
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var traceIds = resourceSpans
                .SelectMany(resource => resource.ScopeSpans)
                .SelectMany(scope => scope.Spans)
                .Select(span => span.TraceId.ToHexString())
                .Distinct(StringComparer.Ordinal);
            var traces = LoadTracesForIngestion(connection, transaction, traceIds);
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
                            var pendingSpan = PrepareSpan(traces, resourceId, resourceViewId, resourceView, scopeId, scope, span);
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

            UpsertTraces(connection, transaction, traces.Values);
            InsertSpans(connection, transaction, pendingSpans);
            InsertSpanDetails(connection, transaction, pendingSpans);
            UpdateUninstrumentedPeers(connection, transaction, traces.Values);
            MarkResourcesHaveTraces(connection, transaction, resourcesWithTraces);
            TrimTracesToCapacity(connection, transaction);
            transaction.Commit();
        }

        return addedSpans;
    }

    private Dictionary<string, OtlpTrace> LoadTracesForIngestion(
        SqliteConnection connection,
        IDbTransaction transaction,
        IEnumerable<string> traceIds)
    {
        var traces = new Dictionary<string, OtlpTrace>(StringComparer.Ordinal);
        foreach (var batch in traceIds.Chunk(MaxTraceBatchSize))
        {
            List<IngestionSpanRecord> spanRecords;
            List<IngestionSpanAttributeRecord> attributeRecords;
            using (var reader = connection.QueryMultiple("""
                SELECT
                    s.trace_id AS TraceId,
                    s.span_id AS SpanId,
                    s.parent_span_id AS ParentSpanId,
                    s.resource_id AS ResourceId,
                    s.resource_view_id AS ResourceViewId,
                    s.scope_id AS ScopeId,
                    s.name AS Name,
                    s.kind AS Kind,
                    s.start_time_ticks AS StartTimeTicks,
                    s.end_time_ticks AS EndTimeTicks,
                    s.status AS Status,
                    s.status_message AS StatusMessage,
                    s.trace_state AS State,
                    t.last_updated_timestamp_ticks AS LastUpdatedTimestampTicks
                FROM telemetry_spans s
                JOIN telemetry_traces t ON t.trace_id = s.trace_id
                WHERE s.trace_id IN @TraceIds
                ORDER BY s.trace_id, s.start_time_ticks, s.span_id;

                SELECT
                    trace_id AS TraceId,
                    span_id AS SpanId,
                    ordinal AS Ordinal,
                    attribute_key AS AttributeKey,
                    attribute_value AS AttributeValue
                FROM telemetry_span_attributes
                WHERE trace_id IN @TraceIds
                ORDER BY trace_id, span_id, ordinal;
                """, new { TraceIds = batch }, transaction))
            {
                spanRecords = reader.Read<IngestionSpanRecord>().AsList();
                attributeRecords = reader.Read<IngestionSpanAttributeRecord>().AsList();
            }

            var attributes = attributeRecords.ToLookup(record => (record.TraceId, record.SpanId));
            foreach (var traceRecords in spanRecords.GroupBy(record => record.TraceId, StringComparer.Ordinal))
            {
                var firstRecord = traceRecords.First();
                var trace = new OtlpTrace(Convert.FromHexString(firstRecord.TraceId), new DateTime(firstRecord.LastUpdatedTimestampTicks, DateTimeKind.Utc));
                foreach (var record in traceRecords)
                {
                    var (_, view, scope) = GetCachedTelemetryMetadata(record.ResourceId, record.ResourceViewId, record.ScopeId, CachedTelemetryType.Traces);
                    trace.AddSpan(new OtlpSpan(view, trace, scope)
                    {
                        SpanId = record.SpanId,
                        ParentSpanId = record.ParentSpanId,
                        Name = record.Name,
                        Kind = (OtlpSpanKind)record.Kind,
                        StartTime = new DateTime(record.StartTimeTicks, DateTimeKind.Utc),
                        EndTime = new DateTime(record.EndTimeTicks, DateTimeKind.Utc),
                        Status = (OtlpSpanStatusCode)record.Status,
                        StatusMessage = record.StatusMessage,
                        State = record.State,
                        Attributes = attributes[(record.TraceId, record.SpanId)]
                            .Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue))
                            .ToArray(),
                        Events = [],
                        Links = [],
                        BackLinks = []
                    }, skipLastUpdatedDate: true);
                }
                traces.Add(trace.TraceId, trace);
            }
        }

        return traces;
    }

    private PendingSpan PrepareSpan(
        Dictionary<string, OtlpTrace> traces,
        long resourceId,
        long resourceViewId,
        OtlpResourceView resourceView,
        long scopeId,
        OtlpScope scope,
        Span span)
    {
        var traceId = span.TraceId.ToHexString();
        var registerTrace = false;
        if (!traces.TryGetValue(traceId, out var trace))
        {
            trace = new OtlpTrace(span.TraceId.Memory, DateTime.UtcNow);
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

    private static void UpsertTraces(SqliteConnection connection, IDbTransaction transaction, IEnumerable<OtlpTrace> traces)
    {
        foreach (var batch in traces.Chunk(MaxTraceBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_traces (
                    trace_id, first_span_timestamp_ticks, duration_ticks, last_updated_timestamp_ticks, full_name)
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
                sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @FirstSpanTimestampTicks{index}, @DurationTicks{index}, @LastUpdatedTimestampTicks{index}, @FullName{index})");
                parameters.Add($"TraceId{index}", trace.TraceId);
                parameters.Add($"FirstSpanTimestampTicks{index}", trace.TimeStamp.Ticks);
                parameters.Add($"DurationTicks{index}", trace.Duration.Ticks);
                parameters.Add($"LastUpdatedTimestampTicks{index}", trace.LastUpdatedDate.Ticks);
                parameters.Add($"FullName{index}", trace.FullName);
            }
            sql.Append("""
                ON CONFLICT(trace_id) DO UPDATE SET
                    first_span_timestamp_ticks = excluded.first_span_timestamp_ticks,
                    duration_ticks = excluded.duration_ticks,
                    last_updated_timestamp_ticks = excluded.last_updated_timestamp_ticks,
                    full_name = excluded.full_name;
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void InsertSpans(SqliteConnection connection, IDbTransaction transaction, List<PendingSpan> spans)
    {
        foreach (var batch in spans.Chunk(MaxSpanBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_spans (
                    trace_id, span_id, parent_span_id, resource_id, resource_view_id, scope_id, name, kind,
                    start_time_ticks, end_time_ticks, status, status_message, trace_state)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                var pendingSpan = batch[index];
                var span = pendingSpan.Span;
                sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @SpanId{index}, @ParentSpanId{index}, @ResourceId{index}, @ResourceViewId{index}, @ScopeId{index}, @Name{index}, @Kind{index}, @StartTimeTicks{index}, @EndTimeTicks{index}, @Status{index}, @StatusMessage{index}, @State{index})");
                parameters.Add($"TraceId{index}", span.TraceId);
                parameters.Add($"SpanId{index}", span.SpanId);
                parameters.Add($"ParentSpanId{index}", span.ParentSpanId);
                parameters.Add($"ResourceId{index}", pendingSpan.ResourceId);
                parameters.Add($"ResourceViewId{index}", pendingSpan.ResourceViewId);
                parameters.Add($"ScopeId{index}", pendingSpan.ScopeId);
                parameters.Add($"Name{index}", span.Name);
                parameters.Add($"Kind{index}", (int)span.Kind);
                parameters.Add($"StartTimeTicks{index}", span.StartTime.Ticks);
                parameters.Add($"EndTimeTicks{index}", span.EndTime.Ticks);
                parameters.Add($"Status{index}", (int)span.Status);
                parameters.Add($"StatusMessage{index}", span.StatusMessage);
                parameters.Add($"State{index}", span.State);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
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

    private void UpdateUninstrumentedPeers(SqliteConnection connection, IDbTransaction transaction, IEnumerable<OtlpTrace> traces)
    {
        var peerResourceIds = new HashSet<long>();
        var spanUpdates = new List<PeerSpanUpdateRecord>();
        foreach (var trace in traces)
        {
            foreach (var span in trace.Spans)
            {
                OtlpResource? peer = null;
                long? peerResourceId = null;
                var hasPeerAddress = OtlpHelpers.GetPeerAddress(span.Attributes) is not null;
                if (hasPeerAddress && span.Kind is OtlpSpanKind.Client or OtlpSpanKind.Producer && !span.GetChildSpans().Any())
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
        }

        if (peerResourceIds.Count > 0)
        {
            connection.Execute(
                "UPDATE telemetry_resources SET uninstrumented_peer = 1 WHERE resource_id IN @ResourceIds;",
                new { ResourceIds = peerResourceIds.ToArray() },
                transaction);
        }

        UpdatePeerSpans(connection, transaction, spanUpdates);
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
        foreach (var batch in attributes.Chunk(MaxSpanDetailBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_span_attributes (trace_id, span_id, ordinal, attribute_key, attribute_value)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@TraceId{index}, @SpanId{index}, @Ordinal{index}, @Key{index}, @Value{index})");
                parameters.Add($"TraceId{index}", batch[index].TraceId);
                parameters.Add($"SpanId{index}", batch[index].SpanId);
                parameters.Add($"Ordinal{index}", batch[index].Ordinal);
                parameters.Add($"Key{index}", batch[index].Key);
                parameters.Add($"Value{index}", batch[index].Value);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
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

    private GetTracesResponse GetTracesFromDatabase(GetTracesRequest context)
    {
        using var connection = _database.OpenConnection();
        var query = BuildTraceQuery(context);
        var aggregate = connection.QuerySingle<TraceAggregateRecord>($"""
            SELECT COUNT(*) AS TotalItemCount, COALESCE(MAX(t.duration_ticks), 0) AS MaxDurationTicks
            {query.FromAndWhere};
            """, query.Parameters);
        query.Parameters.Add("StartIndex", Math.Max(context.StartIndex, 0));
        query.Parameters.Add("Count", Math.Max(context.Count, 0));
        var records = connection.Query<TraceSummaryRecord>($"""
            SELECT
                t.trace_id AS TraceId,
                t.last_updated_timestamp_ticks AS LastUpdatedTimestampTicks
            {query.FromAndWhere}
            ORDER BY t.first_span_timestamp_ticks, t.trace_id
            LIMIT @Count OFFSET @StartIndex;
            """, query.Parameters).AsList();
        var traces = records.Select(record => MaterializeTrace(connection, record.TraceId)!).ToList();
        return new GetTracesResponse
        {
            PagedResult = new PagedResult<OtlpTrace>
            {
                Items = traces,
                TotalItemCount = aggregate.TotalItemCount,
                IsFull = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_traces;") >= _otlpContext.Options.MaxTraceCount
            },
            MaxDuration = TimeSpan.FromTicks(aggregate.MaxDurationTicks)
        };
    }

    private GetTraceSummariesResponse GetTraceSummariesFromDatabase(GetTracesRequest context)
    {
        using var connection = _database.OpenConnection();
        var query = BuildTraceQuery(context);
        query.Parameters.Add("StartIndex", Math.Max(context.StartIndex, 0));
        query.Parameters.Add("Count", Math.Max(context.Count, 0));
        query.Parameters.Add("MaxTraceCount", _otlpContext.Options.MaxTraceCount);

        // Build the page and its resource groups in one query. The recursive span tree preserves
        // the resource ordering used by TraceHelpers when a child span starts before its parent.
        var records = connection.Query<TracePageSummaryRecord>($"""
            WITH RECURSIVE
            filtered_traces AS (
                SELECT t.*
                {query.FromAndWhere}
            ),
            trace_aggregate AS (
                SELECT COUNT(*) AS TotalItemCount, COALESCE(MAX(duration_ticks), 0) AS MaxDurationTicks
                FROM filtered_traces
            ),
            paged_traces AS (
                SELECT *
                FROM filtered_traces
                ORDER BY first_span_timestamp_ticks, trace_id
                LIMIT @Count OFFSET @StartIndex
            ),
            span_tree AS (
                SELECT
                    s.trace_id,
                    s.span_id,
                    s.resource_id,
                    s.uninstrumented_peer_resource_id,
                    s.status,
                    s.start_time_ticks AS resource_order_ticks
                FROM telemetry_spans s
                JOIN paged_traces pt ON pt.trace_id = s.trace_id
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM telemetry_spans parent
                    WHERE parent.trace_id = s.trace_id AND parent.span_id = s.parent_span_id
                )

                UNION ALL

                SELECT
                    child.trace_id,
                    child.span_id,
                    child.resource_id,
                    child.uninstrumented_peer_resource_id,
                    child.status,
                    MAX(child.start_time_ticks, parent.resource_order_ticks)
                FROM telemetry_spans child
                JOIN span_tree parent ON parent.trace_id = child.trace_id AND parent.span_id = child.parent_span_id
            ),
            span_resources AS (
                SELECT
                    st.trace_id,
                    st.resource_id,
                    st.status,
                    st.resource_order_ticks
                FROM span_tree st

                UNION ALL

                SELECT
                    st.trace_id,
                    st.uninstrumented_peer_resource_id,
                    st.status,
                    st.resource_order_ticks
                FROM span_tree st
                WHERE st.uninstrumented_peer_resource_id IS NOT NULL
            ),
            resource_summaries AS (
                SELECT
                    sr.trace_id,
                    r.resource_name,
                    r.instance_id,
                    r.uninstrumented_peer,
                    MIN(sr.resource_order_ticks) AS resource_order_ticks,
                    COUNT(*) AS total_spans,
                    SUM(CASE WHEN sr.status = 2 THEN 1 ELSE 0 END) AS errored_spans
                FROM span_resources sr
                JOIN telemetry_resources r ON r.resource_id = sr.resource_id
                GROUP BY sr.trace_id, sr.resource_id
            ),
            primary_spans AS (
                SELECT
                    s.trace_id,
                    r.resource_name,
                    r.instance_id,
                    r.uninstrumented_peer,
                    ROW_NUMBER() OVER (
                        PARTITION BY s.trace_id
                        ORDER BY CASE WHEN s.parent_span_id IS NULL OR s.parent_span_id = '' THEN 0 ELSE 1 END, s.start_time_ticks, s.span_id
                    ) AS row_number
                FROM telemetry_spans s
                JOIN paged_traces pt ON pt.trace_id = s.trace_id
                JOIN telemetry_resources r ON r.resource_id = s.resource_id
            ),
            trace_summaries AS (
                SELECT
                    pt.trace_id,
                    pt.full_name,
                    pt.first_span_timestamp_ticks,
                    pt.duration_ticks,
                    ps.resource_name AS root_resource_name,
                    ps.instance_id AS root_instance_id,
                    ps.uninstrumented_peer AS root_uninstrumented_peer,
                    EXISTS (SELECT 1 FROM telemetry_spans s WHERE s.trace_id = pt.trace_id AND s.status = 2) AS has_error,
                    EXISTS (
                        SELECT 1
                        FROM telemetry_span_attributes a
                        WHERE a.trace_id = pt.trace_id
                          AND a.attribute_key IN ('gen_ai.system', 'gen_ai.provider.name')
                          AND LENGTH(a.attribute_value) > 0
                    ) AS has_gen_ai,
                    pt.first_span_timestamp_ticks AS trace_order_ticks
                FROM paged_traces pt
                JOIN primary_spans ps ON ps.trace_id = pt.trace_id AND ps.row_number = 1
            )
            SELECT
                a.TotalItemCount,
                a.MaxDurationTicks,
                (SELECT COUNT(*) FROM telemetry_traces) >= @MaxTraceCount AS IsFull,
                ts.trace_id AS TraceId,
                ts.full_name AS FullName,
                ts.first_span_timestamp_ticks AS StartTimeTicks,
                ts.duration_ticks AS DurationTicks,
                ts.root_resource_name AS RootResourceName,
                ts.root_instance_id AS RootInstanceId,
                ts.root_uninstrumented_peer AS RootUninstrumentedPeer,
                ts.has_error AS HasError,
                ts.has_gen_ai AS HasGenAI,
                rs.resource_name AS ResourceName,
                rs.instance_id AS InstanceId,
                rs.uninstrumented_peer AS UninstrumentedPeer,
                rs.total_spans AS TotalSpans,
                rs.errored_spans AS ErroredSpans
            FROM trace_aggregate a
            LEFT JOIN trace_summaries ts ON 1 = 1
            LEFT JOIN resource_summaries rs ON rs.trace_id = ts.trace_id
            ORDER BY ts.trace_order_ticks, ts.trace_id, rs.resource_order_ticks, rs.resource_name, rs.instance_id;
            """, query.Parameters).AsList();

        var firstRecord = records[0];
        var summaries = records
            .Where(record => record.TraceId is not null)
            .GroupBy(record => record.TraceId!, StringComparer.Ordinal)
            .Select(group =>
            {
                var trace = group.First();
                return new TraceSummary
                {
                    TraceId = trace.TraceId!,
                    FullName = trace.FullName!,
                    StartTime = new DateTime(trace.StartTimeTicks!.Value, DateTimeKind.Utc),
                    Duration = TimeSpan.FromTicks(trace.DurationTicks!.Value),
                    RootResource = CreateSummaryResource(trace.RootResourceName!, trace.RootInstanceId, trace.RootUninstrumentedPeer!.Value),
                    Resources = group.Select(resource => new TraceResourceSummary
                    {
                        Resource = CreateSummaryResource(resource.ResourceName!, resource.InstanceId, resource.UninstrumentedPeer!.Value),
                        TotalSpans = resource.TotalSpans!.Value,
                        ErroredSpans = resource.ErroredSpans!.Value
                    }).ToList(),
                    HasError = trace.HasError!.Value,
                    HasGenAI = trace.HasGenAI!.Value
                };
            }).ToList();

        return new GetTraceSummariesResponse
        {
            PagedResult = new PagedResult<TraceSummary>
            {
                Items = summaries,
                TotalItemCount = firstRecord.TotalItemCount,
                IsFull = firstRecord.IsFull
            },
            MaxDuration = TimeSpan.FromTicks(firstRecord.MaxDurationTicks)
        };

        OtlpResource CreateSummaryResource(string resourceName, string? instanceId, bool uninstrumentedPeer) =>
            new(resourceName, instanceId, uninstrumentedPeer, _otlpContext);
    }

    private static TraceQuery BuildTraceQuery(GetTracesRequest context)
    {
        var sql = new StringBuilder("FROM telemetry_traces t WHERE 1 = 1");
        var parameters = new DynamicParameters();
        if (context.ResourceKeys.Count > 0)
        {
            var resourcePredicates = new List<string>(context.ResourceKeys.Count);
            for (var index = 0; index < context.ResourceKeys.Count; index++)
            {
                var key = context.ResourceKeys[index];
                parameters.Add($"ResourceName{index}", key.Name);
                var sourcePredicate = $"r.resource_name = @ResourceName{index} COLLATE NOCASE";
                var peerPredicate = $"pr.resource_name = @ResourceName{index} COLLATE NOCASE";
                if (key.InstanceId is not null)
                {
                    parameters.Add($"InstanceId{index}", key.InstanceId);
                    sourcePredicate += $" AND r.instance_id = @InstanceId{index} COLLATE NOCASE";
                    peerPredicate += $" AND pr.instance_id = @InstanceId{index} COLLATE NOCASE";
                }
                resourcePredicates.Add($"(({sourcePredicate}) OR ({peerPredicate}))");
            }
            sql.Append(" AND EXISTS (SELECT 1 FROM telemetry_spans s JOIN telemetry_resources r ON r.resource_id = s.resource_id LEFT JOIN telemetry_resources pr ON pr.resource_id = s.uninstrumented_peer_resource_id WHERE s.trace_id = t.trace_id AND (");
            sql.AppendJoin(" OR ", resourcePredicates);
            sql.Append("))");
        }
        if (!string.IsNullOrWhiteSpace(context.TraceNameFilterText))
        {
            sql.Append(" AND t.full_name LIKE @TraceNameFilterText ESCAPE '!'");
            parameters.Add("TraceNameFilterText", CreateContainsLikePattern(context.TraceNameFilterText));
        }

        var positivePredicates = new List<string>();
        var filterIndex = 0;
        foreach (var filter in context.Filters.Where(filter => filter.Enabled))
        {
            if (filter is not FieldTelemetryFilter fieldFilter)
            {
                continue;
            }

            if (fieldFilter.Field == KnownTraceFields.DurationField)
            {
                sql.Append(" AND ");
                sql.Append(BuildTraceDurationPredicate(fieldFilter, parameters, filterIndex++));
                continue;
            }

            if (fieldFilter.Condition is FilterCondition.NotEqual or FilterCondition.NotContains)
            {
                sql.Append(" AND NOT EXISTS (SELECT 1 FROM telemetry_spans s JOIN telemetry_resources r ON r.resource_id = s.resource_id JOIN telemetry_scopes sc ON sc.scope_id = s.scope_id LEFT JOIN telemetry_resources pr ON pr.resource_id = s.uninstrumented_peer_resource_id WHERE s.trace_id = t.trace_id AND ");
                sql.Append(BuildSpanFieldPredicate(fieldFilter, parameters, filterIndex++, invertNegative: true));
                sql.Append(')');
            }
            else
            {
                positivePredicates.Add(BuildSpanFieldPredicate(fieldFilter, parameters, filterIndex++, invertNegative: false));
            }
        }
        if (positivePredicates.Count > 0)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM telemetry_spans s JOIN telemetry_resources r ON r.resource_id = s.resource_id JOIN telemetry_scopes sc ON sc.scope_id = s.scope_id LEFT JOIN telemetry_resources pr ON pr.resource_id = s.uninstrumented_peer_resource_id WHERE s.trace_id = t.trace_id AND ");
            sql.AppendJoin(" AND ", positivePredicates);
            sql.Append(')');
        }

        if (context.TextFragments is { Length: > 0 })
        {
            var fullNamePredicates = new List<string>(context.TextFragments.Length);
            var spanPredicates = new List<string>(context.TextFragments.Length);
            for (var index = 0; index < context.TextFragments.Length; index++)
            {
                var parameterName = $"TextFragment{index}";
                parameters.Add(parameterName, CreateContainsLikePattern(context.TextFragments[index]));
                fullNamePredicates.Add($"t.full_name LIKE @{parameterName} ESCAPE '!'");
                spanPredicates.Add($"""
                    (
                        s.name LIKE @{parameterName} ESCAPE '!' OR
                        s.span_id LIKE @{parameterName} ESCAPE '!' OR
                        s.trace_id LIKE @{parameterName} ESCAPE '!' OR
                        sc.scope_name LIKE @{parameterName} ESCAPE '!' OR
                        r.resource_name LIKE @{parameterName} ESCAPE '!' OR
                        CASE s.status WHEN 0 THEN 'Unset' WHEN 1 THEN 'Ok' WHEN 2 THEN 'Error' END LIKE @{parameterName} ESCAPE '!' OR
                        CASE s.kind WHEN 0 THEN 'Unspecified' WHEN 1 THEN 'Internal' WHEN 2 THEN 'Server' WHEN 3 THEN 'Client' WHEN 4 THEN 'Producer' WHEN 5 THEN 'Consumer' END LIKE @{parameterName} ESCAPE '!' OR
                        COALESCE(s.status_message, '') LIKE @{parameterName} ESCAPE '!' OR
                        EXISTS (SELECT 1 FROM telemetry_span_attributes a WHERE a.trace_id = s.trace_id AND a.span_id = s.span_id AND (a.attribute_key LIKE @{parameterName} ESCAPE '!' OR a.attribute_value LIKE @{parameterName} ESCAPE '!')) OR
                        EXISTS (SELECT 1 FROM telemetry_span_events e WHERE e.trace_id = s.trace_id AND e.span_id = s.span_id AND e.event_name LIKE @{parameterName} ESCAPE '!')
                    )
                    """);
            }
            sql.Append(" AND ((");
            sql.AppendJoin(" AND ", fullNamePredicates);
            sql.Append(") OR EXISTS (SELECT 1 FROM telemetry_spans s JOIN telemetry_resources r ON r.resource_id = s.resource_id JOIN telemetry_scopes sc ON sc.scope_id = s.scope_id WHERE s.trace_id = t.trace_id AND ");
            sql.AppendJoin(" AND ", spanPredicates);
            sql.Append("))");
        }
        return new TraceQuery(sql.ToString(), parameters);
    }

    private static string BuildTraceDurationPredicate(FieldTelemetryFilter filter, DynamicParameters parameters, int filterIndex)
    {
        var parameterName = $"TraceDuration{filterIndex}";
        if (!double.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) || !double.IsFinite(milliseconds))
        {
            return "0 = 1";
        }

        parameters.Add(parameterName, milliseconds);
        return BuildNumericPredicate($"(CAST(t.duration_ticks AS REAL) / {TimeSpan.TicksPerMillisecond})", filter.Condition, parameterName);
    }

    private static string BuildSpanFieldPredicate(
        FieldTelemetryFilter filter,
        DynamicParameters parameters,
        int filterIndex,
        bool invertNegative)
    {
        var parameterName = $"TraceFilter{filterIndex}";
        var condition = invertNegative
            ? filter.Condition switch
            {
                FilterCondition.NotEqual => FilterCondition.Equals,
                FilterCondition.NotContains => FilterCondition.Contains,
                _ => filter.Condition
            }
            : filter.Condition;
        parameters.Add(
            parameterName,
            condition is FilterCondition.Contains or FilterCondition.NotContains
                ? CreateContainsLikePattern(filter.Value)
                : filter.Value);

        var expression = filter.Field switch
        {
            KnownResourceFields.ServiceNameField => null,
            KnownTraceFields.TraceIdField => "s.trace_id",
            KnownTraceFields.SpanIdField => "s.span_id",
            KnownTraceFields.NameField => "s.name",
            KnownTraceFields.KindField => "CASE s.kind WHEN 0 THEN 'Unspecified' WHEN 1 THEN 'Internal' WHEN 2 THEN 'Server' WHEN 3 THEN 'Client' WHEN 4 THEN 'Producer' WHEN 5 THEN 'Consumer' END",
            KnownTraceFields.StatusField => "CASE s.status WHEN 0 THEN 'Unset' WHEN 1 THEN 'Ok' WHEN 2 THEN 'Error' END",
            KnownSourceFields.NameField => "sc.scope_name",
            KnownTraceFields.TimestampField => "s.start_time_ticks / 10000",
            _ => null
        };
        if (filter.Field == KnownTraceFields.TimestampField)
        {
            if (!DateTime.TryParse(filter.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var date))
            {
                return "0 = 1";
            }
            parameters.Add(parameterName, date.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond);
            return BuildNumericPredicate(expression!, condition, parameterName);
        }
        if (expression is not null)
        {
            return BuildStringPredicate(expression, condition, parameterName);
        }
        if (filter.Field == KnownResourceFields.ServiceNameField)
        {
            var sourcePredicate = BuildStringPredicate("r.resource_name", condition, parameterName);
            var peerPredicate = BuildStringPredicate("pr.resource_name", condition, parameterName);
            return $"({sourcePredicate} OR (pr.resource_id IS NOT NULL AND {peerPredicate}))";
        }

        var attributePredicate = BuildStringPredicate("a.attribute_value", condition, parameterName);
        parameters.Add($"TraceField{filterIndex}", filter.Field);
        return $"EXISTS (SELECT 1 FROM telemetry_span_attributes a WHERE a.trace_id = s.trace_id AND a.span_id = s.span_id AND a.attribute_key = @TraceField{filterIndex} COLLATE NOCASE AND {attributePredicate})";
    }

    private GetSpansResponse GetSpansFromDatabase(GetSpansRequest context)
    {
        using var connection = _database.OpenConnection();
        var query = BuildSpanQuery(context);
        var totalCount = connection.QuerySingle<int>($"SELECT COUNT(*) {query.FromAndWhere};", query.Parameters);
        query.Parameters.Add("StartIndex", Math.Max(context.StartIndex, 0));
        query.Parameters.Add("Count", Math.Max(context.Count, 0));
        var identities = connection.Query<SpanIdentityRecord>($"""
            SELECT s.trace_id AS TraceId, s.span_id AS SpanId
            {query.FromAndWhere}
            ORDER BY t.first_span_timestamp_ticks, t.trace_id, s.start_time_ticks, s.span_id
            LIMIT @Count OFFSET @StartIndex;
            """, query.Parameters).AsList();
        var traces = identities.Select(identity => identity.TraceId).Distinct(StringComparer.Ordinal)
            .ToDictionary(traceId => traceId, traceId => MaterializeTrace(connection, traceId)!, StringComparer.Ordinal);
        return new GetSpansResponse
        {
            PagedResult = new PagedResult<OtlpSpan>
            {
                Items = identities.Select(identity => traces[identity.TraceId].Spans.Single(span => span.SpanId == identity.SpanId)).ToList(),
                TotalItemCount = totalCount,
                IsFull = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_traces;") >= _otlpContext.Options.MaxTraceCount
            }
        };
    }

    private static TraceQuery BuildSpanQuery(GetSpansRequest context)
    {
        var sql = new StringBuilder("""
            FROM telemetry_spans s
            JOIN telemetry_traces t ON t.trace_id = s.trace_id
            JOIN telemetry_resources r ON r.resource_id = s.resource_id
            JOIN telemetry_scopes sc ON sc.scope_id = s.scope_id
            LEFT JOIN telemetry_resources pr ON pr.resource_id = s.uninstrumented_peer_resource_id
            WHERE 1 = 1
            """);
        var parameters = new DynamicParameters();
        if (context.ResourceKeys.Count > 0)
        {
            var predicates = new List<string>(context.ResourceKeys.Count);
            for (var index = 0; index < context.ResourceKeys.Count; index++)
            {
                var key = context.ResourceKeys[index];
                parameters.Add($"SpanResourceName{index}", key.Name);
                var source = $"r.resource_name = @SpanResourceName{index} COLLATE NOCASE";
                var peer = $"pr.resource_name = @SpanResourceName{index} COLLATE NOCASE";
                if (key.InstanceId is not null)
                {
                    parameters.Add($"SpanInstanceId{index}", key.InstanceId);
                    source += $" AND r.instance_id = @SpanInstanceId{index} COLLATE NOCASE";
                    peer += $" AND pr.instance_id = @SpanInstanceId{index} COLLATE NOCASE";
                }
                predicates.Add($"(({source}) OR ({peer}))");
            }
            sql.Append(" AND (");
            sql.AppendJoin(" OR ", predicates);
            sql.Append(')');
        }
        if (!string.IsNullOrEmpty(context.TraceId))
        {
            parameters.Add(
                "SpanTraceId",
                context.TraceId.Length >= OtlpHelpers.ShortenedIdLength
                    ? CreateStartsWithLikePattern(context.TraceId)
                    : context.TraceId);
            sql.Append(context.TraceId.Length >= OtlpHelpers.ShortenedIdLength
                ? " AND s.trace_id LIKE @SpanTraceId ESCAPE '!'"
                : " AND s.trace_id = @SpanTraceId COLLATE NOCASE");
        }
        if (context.HasError is not null)
        {
            parameters.Add("SpanErrorStatus", (int)OtlpSpanStatusCode.Error);
            sql.Append(context.HasError.Value ? " AND s.status = @SpanErrorStatus" : " AND s.status <> @SpanErrorStatus");
        }

        var filterIndex = 0;
        foreach (var filter in context.Filters.Where(filter => filter.Enabled))
        {
            if (filter is not FieldTelemetryFilter fieldFilter)
            {
                continue;
            }
            if (fieldFilter.Field == KnownTraceFields.DurationField)
            {
                var parameterName = $"SpanDuration{filterIndex++}";
                if (!double.TryParse(fieldFilter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) || !double.IsFinite(milliseconds))
                {
                    sql.Append(" AND 0 = 1");
                    continue;
                }
                parameters.Add(parameterName, milliseconds);
                sql.Append(" AND ");
                sql.Append(BuildNumericPredicate($"(CAST(s.end_time_ticks - s.start_time_ticks AS REAL) / {TimeSpan.TicksPerMillisecond})", fieldFilter.Condition, parameterName));
                continue;
            }

            if (fieldFilter.Condition is FilterCondition.NotEqual or FilterCondition.NotContains &&
                fieldFilter.Field is not (KnownResourceFields.ServiceNameField or KnownTraceFields.TraceIdField or KnownTraceFields.SpanIdField or KnownTraceFields.NameField or KnownTraceFields.KindField or KnownTraceFields.StatusField or KnownSourceFields.NameField or KnownTraceFields.TimestampField))
            {
                var violationFilter = new FieldTelemetryFilter
                {
                    Field = fieldFilter.Field,
                    Condition = fieldFilter.Condition == FilterCondition.NotEqual ? FilterCondition.Equals : FilterCondition.Contains,
                    Value = fieldFilter.Value
                };
                sql.Append(" AND NOT ");
                sql.Append(BuildSpanFieldPredicate(violationFilter, parameters, filterIndex++, invertNegative: false));
            }
            else
            {
                sql.Append(" AND ");
                sql.Append(BuildSpanFieldPredicate(fieldFilter, parameters, filterIndex++, invertNegative: false));
            }
        }

        if (context.TextFragments is { Length: > 0 })
        {
            for (var index = 0; index < context.TextFragments.Length; index++)
            {
                var parameterName = $"SpanTextFragment{index}";
                parameters.Add(parameterName, CreateContainsLikePattern(context.TextFragments[index]));
                 sql.Append(CultureInfo.InvariantCulture, $"""
                     AND (
                        s.name LIKE @{parameterName} ESCAPE '!' OR
                        s.span_id LIKE @{parameterName} ESCAPE '!' OR
                        s.trace_id LIKE @{parameterName} ESCAPE '!' OR
                        sc.scope_name LIKE @{parameterName} ESCAPE '!' OR
                        r.resource_name LIKE @{parameterName} ESCAPE '!' OR
                        CASE s.status WHEN 0 THEN 'Unset' WHEN 1 THEN 'Ok' WHEN 2 THEN 'Error' END LIKE @{parameterName} ESCAPE '!' OR
                        CASE s.kind WHEN 0 THEN 'Unspecified' WHEN 1 THEN 'Internal' WHEN 2 THEN 'Server' WHEN 3 THEN 'Client' WHEN 4 THEN 'Producer' WHEN 5 THEN 'Consumer' END LIKE @{parameterName} ESCAPE '!' OR
                        COALESCE(s.status_message, '') LIKE @{parameterName} ESCAPE '!' OR
                        EXISTS (SELECT 1 FROM telemetry_span_attributes a WHERE a.trace_id = s.trace_id AND a.span_id = s.span_id AND (a.attribute_key LIKE @{parameterName} ESCAPE '!' OR a.attribute_value LIKE @{parameterName} ESCAPE '!')) OR
                        EXISTS (SELECT 1 FROM telemetry_span_events e WHERE e.trace_id = s.trace_id AND e.span_id = s.span_id AND e.event_name LIKE @{parameterName} ESCAPE '!')
                    )
                    """);
            }
        }
        return new TraceQuery(sql.ToString(), parameters);
    }

    private List<string> GetTracePropertyKeysFromDatabase(ResourceKey? resourceKey)
    {
        using var connection = _database.OpenConnection();
        var parameters = new DynamicParameters();
        var resourceWhere = string.Empty;
        if (resourceKey is not null)
        {
            resourceWhere = " AND r.resource_name = @ResourceName COLLATE NOCASE";
            parameters.Add("ResourceName", resourceKey.Value.Name);
            if (resourceKey.Value.InstanceId is not null)
            {
                resourceWhere += " AND r.instance_id = @InstanceId COLLATE NOCASE";
                parameters.Add("InstanceId", resourceKey.Value.InstanceId);
            }
        }
        return connection.Query<string>($"""
            SELECT DISTINCT a.attribute_key
            FROM telemetry_span_attributes a
            JOIN telemetry_spans s ON s.trace_id = a.trace_id AND s.span_id = a.span_id
            JOIN telemetry_resources r ON r.resource_id = s.resource_id
            WHERE 1 = 1{resourceWhere}
            ORDER BY a.attribute_key;
            """, parameters).AsList();
    }

    private Dictionary<string, int> GetTraceFieldValuesFromDatabase(string attributeName)
    {
        if (attributeName is KnownTraceFields.DurationField or KnownTraceFields.TimestampField)
        {
            return new Dictionary<string, int>(StringComparers.OtlpAttribute);
        }

        using var connection = _database.OpenConnection();
        IEnumerable<FieldValueRecord> values = attributeName switch
        {
            KnownResourceFields.ServiceNameField => connection.Query<FieldValueRecord>("""
                SELECT resource_name AS FieldValue, COUNT(*) AS ValueCount
                FROM (
                    SELECT r.resource_name
                    FROM telemetry_spans s
                    JOIN telemetry_resources r ON r.resource_id = s.resource_id
                    UNION ALL
                    SELECT r.resource_name
                    FROM telemetry_spans s
                    JOIN telemetry_resources r ON r.resource_id = s.uninstrumented_peer_resource_id
                )
                GROUP BY resource_name;
                """),
            KnownTraceFields.TraceIdField => QueryFieldValues("trace_id", "telemetry_spans"),
            KnownTraceFields.SpanIdField => QueryFieldValues("span_id", "telemetry_spans"),
            KnownTraceFields.KindField => connection.Query<FieldValueRecord>("""
                SELECT
                    CASE kind
                        WHEN 0 THEN 'Unspecified'
                        WHEN 1 THEN 'Internal'
                        WHEN 2 THEN 'Server'
                        WHEN 3 THEN 'Client'
                        WHEN 4 THEN 'Producer'
                        WHEN 5 THEN 'Consumer'
                        ELSE CAST(kind AS TEXT)
                    END AS FieldValue,
                    COUNT(*) AS ValueCount
                FROM telemetry_spans
                GROUP BY kind;
                """),
            KnownTraceFields.StatusField => connection.Query<FieldValueRecord>("""
                SELECT
                    CASE status
                        WHEN 0 THEN 'Unset'
                        WHEN 1 THEN 'Ok'
                        WHEN 2 THEN 'Error'
                        ELSE CAST(status AS TEXT)
                    END AS FieldValue,
                    COUNT(*) AS ValueCount
                FROM telemetry_spans
                GROUP BY status;
                """),
            KnownSourceFields.NameField => connection.Query<FieldValueRecord>("""
                SELECT sc.scope_name AS FieldValue, COUNT(*) AS ValueCount
                FROM telemetry_spans s
                JOIN telemetry_scopes sc ON sc.scope_id = s.scope_id
                GROUP BY sc.scope_name;
                """),
            KnownTraceFields.NameField => QueryFieldValues("name", "telemetry_spans"),
            _ => connection.Query<FieldValueRecord>("""
                SELECT attribute_value AS FieldValue, COUNT(*) AS ValueCount
                FROM telemetry_span_attributes
                WHERE attribute_key = @AttributeName COLLATE NOCASE
                GROUP BY attribute_value;
                """, new { AttributeName = attributeName })
        };

        return values.ToDictionary(record => record.FieldValue!, record => record.ValueCount, StringComparers.OtlpAttribute);

        IEnumerable<FieldValueRecord> QueryFieldValues(string expression, string table)
        {
            return connection.Query<FieldValueRecord>($"""
                SELECT {expression} AS FieldValue, COUNT(*) AS ValueCount
                FROM {table}
                GROUP BY {expression};
                """);
        }
    }

    private bool HasUpdatedTraceInDatabase(OtlpTrace trace)
    {
        using var connection = _database.OpenConnection();
        var lastUpdatedTicks = connection.QuerySingleOrDefault<long?>("""
            SELECT last_updated_timestamp_ticks
            FROM telemetry_traces
            WHERE trace_id = @TraceId;
            """, new { trace.TraceId });
        return lastUpdatedTicks is null || lastUpdatedTicks.Value > trace.LastUpdatedDate.Ticks;
    }

    private OtlpTrace? GetTraceFromDatabase(string traceId)
    {
        using var connection = _database.OpenConnection();
        var usePrefix = traceId.Length >= OtlpHelpers.ShortenedIdLength;
        var storedTraceId = connection.QueryFirstOrDefault<string>(usePrefix
            ? "SELECT trace_id FROM telemetry_traces WHERE trace_id LIKE @TraceId ESCAPE '!' ORDER BY first_span_timestamp_ticks, trace_id LIMIT 1;"
            : "SELECT trace_id FROM telemetry_traces WHERE trace_id = @TraceId COLLATE NOCASE ORDER BY first_span_timestamp_ticks, trace_id LIMIT 1;", new { TraceId = usePrefix ? CreateStartsWithLikePattern(traceId) : traceId });
        return storedTraceId is null ? null : MaterializeTrace(connection, storedTraceId);
    }

    private OtlpSpan? GetSpanFromDatabase(string traceId, string spanId)
    {
        var trace = GetTraceFromDatabase(traceId);
        return trace?.Spans.FirstOrDefault(span => span.SpanId == spanId);
    }

    private OtlpTrace? MaterializeTrace(SqliteConnection connection, string traceId, IDbTransaction? transaction = null)
    {
        var records = connection.Query<SpanRecord>("""
            SELECT
                s.trace_id AS TraceId,
                s.span_id AS SpanId,
                s.parent_span_id AS ParentSpanId,
                s.resource_id AS ResourceId,
                s.resource_view_id AS ResourceViewId,
                s.scope_id AS ScopeId,
                s.name AS Name,
                s.kind AS Kind,
                s.start_time_ticks AS StartTimeTicks,
                s.end_time_ticks AS EndTimeTicks,
                s.status AS Status,
                s.status_message AS StatusMessage,
                s.trace_state AS State,
                s.uninstrumented_peer_resource_id AS PeerResourceId,
                t.last_updated_timestamp_ticks AS LastUpdatedTimestampTicks,
                r.resource_name AS ResourceName,
                r.instance_id AS InstanceId,
                r.uninstrumented_peer AS UninstrumentedPeer,
                r.has_logs AS HasLogs,
                r.has_traces AS HasTraces,
                r.has_metrics AS HasMetrics,
                sc.scope_name AS ScopeName,
                sc.scope_version AS ScopeVersion,
                pr.resource_name AS PeerResourceName,
                pr.instance_id AS PeerInstanceId
            FROM telemetry_spans s
            JOIN telemetry_traces t ON t.trace_id = s.trace_id
            JOIN telemetry_resources r ON r.resource_id = s.resource_id
            JOIN telemetry_scopes sc ON sc.scope_id = s.scope_id
            LEFT JOIN telemetry_resources pr ON pr.resource_id = s.uninstrumented_peer_resource_id
            WHERE s.trace_id = @TraceId
            ORDER BY s.start_time_ticks, s.span_id;
            """, new { TraceId = traceId }, transaction).AsList();
        if (records.Count == 0)
        {
            return null;
        }

        var spanIds = records.Select(record => record.SpanId).ToArray();
        var spanAttributes = connection.Query<TraceOwnedAttributeRecord>("""
            SELECT span_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_span_attributes
            WHERE trace_id = @TraceId AND span_id IN @SpanIds
            ORDER BY span_id, ordinal;
            """, new { TraceId = traceId, SpanIds = spanIds }, transaction).ToLookup(record => record.OwnerId);
        var eventRecords = connection.Query<SpanEventRecord>("""
            SELECT event_id AS EventId, span_id AS SpanId, event_name AS EventName, event_time_ticks AS EventTimeTicks
            FROM telemetry_span_events
            WHERE trace_id = @TraceId
            ORDER BY span_id, ordinal;
            """, new { TraceId = traceId }, transaction).AsList();
        var eventAttributes = connection.Query<TextOwnedAttributeRecord>("""
            SELECT event_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_span_event_attributes
            WHERE event_id IN @Ids
            ORDER BY event_id, ordinal;
            """, new { Ids = eventRecords.Select(record => record.EventId).ToArray() }, transaction).ToLookup(record => record.OwnerId);
        var events = eventRecords.ToLookup(record => record.SpanId);
        var linkRecords = connection.Query<SpanLinkRecord>("""
            SELECT
                link_id AS LinkId,
                source_trace_id AS SourceTraceId,
                source_span_id AS SourceSpanId,
                target_trace_id AS TraceId,
                target_span_id AS SpanId,
                trace_state AS TraceState
            FROM telemetry_span_links
            WHERE source_trace_id = @TraceId OR target_trace_id = @TraceId
            ORDER BY link_id;
            """, new { TraceId = traceId }, transaction).AsList();
        var linkAttributes = connection.Query<LongOwnedAttributeRecord>("""
            SELECT link_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_span_link_attributes
            WHERE link_id IN @Ids
            ORDER BY link_id, ordinal;
            """, new { Ids = linkRecords.Select(record => record.LinkId).ToArray() }, transaction).ToLookup(record => record.OwnerId);
        var outgoingLinks = linkRecords.Where(record => record.SourceTraceId == traceId).ToLookup(record => record.SourceSpanId);
        var incomingLinks = linkRecords.Where(record => record.TraceId == traceId).ToLookup(record => record.SpanId);

        var trace = new OtlpTrace(Convert.FromHexString(traceId), new DateTime(records[0].LastUpdatedTimestampTicks, DateTimeKind.Utc));
        foreach (var record in records)
        {
            var (_, view, scope) = GetCachedTelemetryMetadata(record.ResourceId, record.ResourceViewId, record.ScopeId, CachedTelemetryType.Traces);

            var modelSpan = new OtlpSpan(view, trace, scope)
            {
                SpanId = record.SpanId,
                ParentSpanId = record.ParentSpanId,
                Name = record.Name,
                Kind = (OtlpSpanKind)record.Kind,
                StartTime = new DateTime(record.StartTimeTicks, DateTimeKind.Utc),
                EndTime = new DateTime(record.EndTimeTicks, DateTimeKind.Utc),
                Status = (OtlpSpanStatusCode)record.Status,
                StatusMessage = record.StatusMessage,
                State = record.State,
                Attributes = ToPairs(spanAttributes[record.SpanId]),
                Events = [],
                Links = outgoingLinks[record.SpanId].Select(CreateLink).ToList(),
                BackLinks = incomingLinks[record.SpanId].Select(CreateLink).ToList()
            };
            if (record.PeerResourceId is not null)
            {
                modelSpan.SetUninstrumentedPeer(GetCachedResource(record.PeerResourceId.Value));
            }
            modelSpan.Events.AddRange(events[record.SpanId].Select(spanEvent => new OtlpSpanEvent(modelSpan)
            {
                InternalId = Guid.Parse(spanEvent.EventId),
                Name = spanEvent.EventName,
                Time = new DateTime(spanEvent.EventTimeTicks, DateTimeKind.Utc),
                Attributes = ToPairs(eventAttributes[spanEvent.EventId])
            }));
            trace.AddSpan(modelSpan, skipLastUpdatedDate: true);
        }
        return trace;

        OtlpSpanLink CreateLink(SpanLinkRecord link)
        {
            return new OtlpSpanLink
            {
                SourceTraceId = link.SourceTraceId,
                SourceSpanId = link.SourceSpanId,
                TraceId = link.TraceId,
                SpanId = link.SpanId,
                TraceState = link.TraceState,
                Attributes = ToPairs(linkAttributes[link.LinkId])
            };
        }

        static KeyValuePair<string, string>[] ToPairs(IEnumerable<AttributeRecord> attributes)
        {
            return attributes.Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray();
        }
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

    private sealed record TraceQuery(string FromAndWhere, DynamicParameters Parameters);

    private sealed record PendingSpan(long ResourceId, long ResourceViewId, long ScopeId, OtlpSpan Span);

    private sealed record PendingSpanEvent(string EventId, string TraceId, string SpanId, int Ordinal, OtlpSpanEvent Event);

    private sealed class IngestionSpanAttributeRecord : AttributeRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public required int Ordinal { get; init; }
    }

    private sealed class IngestionSpanRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public string? ParentSpanId { get; init; }
        public required long ResourceId { get; init; }
        public required long ResourceViewId { get; init; }
        public required long ScopeId { get; init; }
        public required string Name { get; init; }
        public required int Kind { get; init; }
        public required long StartTimeTicks { get; init; }
        public required long EndTimeTicks { get; init; }
        public required int Status { get; init; }
        public string? StatusMessage { get; init; }
        public string? State { get; init; }
        public required long LastUpdatedTimestampTicks { get; init; }
    }

    private sealed class PendingSpanLink(OtlpSpanLink link)
    {
        public OtlpSpanLink Link { get; } = link;
        public long LinkId { get; set; }
    }

    private sealed class TraceAggregateRecord
    {
        public required int TotalItemCount { get; init; }
        public required long MaxDurationTicks { get; init; }
    }

    private sealed class TraceSummaryRecord
    {
        public required string TraceId { get; init; }
        public required long LastUpdatedTimestampTicks { get; init; }
    }

    private sealed class TracePageSummaryRecord
    {
        public required int TotalItemCount { get; init; }
        public required long MaxDurationTicks { get; init; }
        public required bool IsFull { get; init; }
        public string? TraceId { get; init; }
        public string? FullName { get; init; }
        public long? StartTimeTicks { get; init; }
        public long? DurationTicks { get; init; }
        public string? RootResourceName { get; init; }
        public string? RootInstanceId { get; init; }
        public bool? RootUninstrumentedPeer { get; init; }
        public bool? HasError { get; init; }
        public bool? HasGenAI { get; init; }
        public string? ResourceName { get; init; }
        public string? InstanceId { get; init; }
        public bool? UninstrumentedPeer { get; init; }
        public int? TotalSpans { get; init; }
        public int? ErroredSpans { get; init; }
    }

    private sealed class SpanIdentityRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
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

    private sealed class TraceOwnedAttributeRecord : AttributeRecord
    {
        public required string OwnerId { get; init; }
    }

    private sealed class TextOwnedAttributeRecord : AttributeRecord
    {
        public required string OwnerId { get; init; }
    }

    private sealed class LongOwnedAttributeRecord : AttributeRecord
    {
        public required long OwnerId { get; init; }
    }

    private sealed class SpanEventRecord
    {
        public required string EventId { get; init; }
        public required string SpanId { get; init; }
        public required string EventName { get; init; }
        public required long EventTimeTicks { get; init; }
    }

    private sealed class SpanLinkRecord
    {
        public required long LinkId { get; init; }
        public required string SourceTraceId { get; init; }
        public required string SourceSpanId { get; init; }
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public required string TraceState { get; init; }
    }

    private sealed class SpanRecord
    {
        public required string TraceId { get; init; }
        public required string SpanId { get; init; }
        public string? ParentSpanId { get; init; }
        public required long ResourceId { get; init; }
        public required long ResourceViewId { get; init; }
        public required long ScopeId { get; init; }
        public required string Name { get; init; }
        public required int Kind { get; init; }
        public required long StartTimeTicks { get; init; }
        public required long EndTimeTicks { get; init; }
        public required int Status { get; init; }
        public string? StatusMessage { get; init; }
        public string? State { get; init; }
        public long? PeerResourceId { get; init; }
        public required long LastUpdatedTimestampTicks { get; init; }
        public required string ResourceName { get; init; }
        public string? InstanceId { get; init; }
        public required bool UninstrumentedPeer { get; init; }
        public required bool HasLogs { get; init; }
        public required bool HasTraces { get; init; }
        public required bool HasMetrics { get; init; }
        public required string ScopeName { get; init; }
        public required string ScopeVersion { get; init; }
        public string? PeerResourceName { get; init; }
        public string? PeerInstanceId { get; init; }
    }
}