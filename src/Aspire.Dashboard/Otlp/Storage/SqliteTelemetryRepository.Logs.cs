// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Globalization;
using System.Text;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Dapper;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Logs.V1;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int MaxLogBatchSize = 50;
    private const int MaxLogAttributeBatchSize = 200;

    private List<OtlpLogEntry> AddLogsToDatabase(AddContext context, RepeatedField<ResourceLogs> resourceLogs)
    {
        var addedLogs = new List<OtlpLogEntry>();
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var pendingLogs = new List<PendingLog>();
            var resourcesWithLogs = new HashSet<CachedResource>();

            foreach (var resourceLogsItem in resourceLogs)
            {
                CachedResource cachedResource;
                OtlpResourceView resourceView;
                long resourceViewId;
                try
                {
                    var resourceKey = resourceLogsItem.Resource.GetResourceKey();
                    cachedResource = GetOrAddCachedResource(connection, transaction, resourceKey);
                    var cachedView = GetOrAddCachedResourceView(connection, transaction, cachedResource, resourceLogsItem.Resource.Attributes);
                    resourceView = cachedView.View;
                    resourceViewId = cachedView.ResourceViewId;
                }
                catch (Exception exception)
                {
                    context.FailureCount += resourceLogsItem.ScopeLogs.Sum(scope => scope.LogRecords.Count);
                    _otlpContext.Logger.LogInformation(exception, "Error adding resource.");
                    continue;
                }
                resourcesWithLogs.Add(cachedResource);

                foreach (var scopeLogs in resourceLogsItem.ScopeLogs)
                {
                    OtlpScope scope;
                    long scopeId;
                    try
                    {
                        var cachedScope = GetOrAddCachedScope(connection, transaction, cachedResource, scopeLogs.Scope, CachedTelemetryType.Logs);
                        scopeId = cachedScope.Scope.ScopeId;
                        scope = cachedScope.Scope.Scope;
                    }
                    catch (Exception exception)
                    {
                        context.FailureCount += scopeLogs.LogRecords.Count;
                        _otlpContext.Logger.LogInformation(exception, "Error adding log scope.");
                        continue;
                    }

                    foreach (var record in scopeLogs.LogRecords)
                    {
                        try
                        {
                            pendingLogs.Add(new PendingLog(
                                cachedResource.ResourceId,
                                resourceViewId,
                                scopeId,
                                new OtlpLogEntry(record, resourceView, scope, _otlpContext)));
                        }
                        catch (Exception exception)
                        {
                            context.FailureCount++;
                            _otlpContext.Logger.LogInformation(exception, "Error adding log entry.");
                        }
                    }
                }
            }

            InsertLogs(connection, transaction, pendingLogs, addedLogs);
            context.SuccessCount += pendingLogs.Count;
            MarkResourcesHaveLogs(connection, transaction, resourcesWithLogs);
            TrimLogsToCapacity(connection, transaction);
            transaction.Commit();
        }

        return addedLogs;
    }

    private static void InsertLogs(
        SqliteConnection connection,
        IDbTransaction transaction,
        List<PendingLog> logs,
        List<OtlpLogEntry> addedLogs)
    {
        foreach (var logBatch in logs.Chunk(MaxLogBatchSize))
        {
            var sql = new StringBuilder();
            var parameters = new DynamicParameters();
            for (var index = 0; index < logBatch.Length; index++)
            {
                var pendingLog = logBatch[index];
                var log = pendingLog.Log;
                sql.Append(CultureInfo.InvariantCulture, $$"""
                    INSERT INTO telemetry_logs (
                        resource_id, resource_view_id, scope_id, timestamp_ticks, flags, severity,
                        severity_name, severity_number, message, span_id, trace_id, parent_id,
                        original_format, event_name)
                    VALUES (
                        @ResourceId{{index}}, @ResourceViewId{{index}}, @ScopeId{{index}}, @TimestampTicks{{index}}, @Flags{{index}}, @Severity{{index}},
                        @SeverityName{{index}}, @SeverityNumber{{index}}, @Message{{index}}, @SpanId{{index}}, @TraceId{{index}}, @ParentId{{index}},
                        @OriginalFormat{{index}}, @EventName{{index}})
                    RETURNING log_id;
                    """);
                parameters.Add($"ResourceId{index}", pendingLog.ResourceId);
                parameters.Add($"ResourceViewId{index}", pendingLog.ResourceViewId);
                parameters.Add($"ScopeId{index}", pendingLog.ScopeId);
                parameters.Add($"TimestampTicks{index}", log.TimeStamp.Ticks);
                parameters.Add($"Flags{index}", (long)log.Flags);
                parameters.Add($"Severity{index}", (int)log.Severity);
                parameters.Add($"SeverityName{index}", log.Severity.ToString());
                parameters.Add($"SeverityNumber{index}", log.SeverityNumber);
                parameters.Add($"Message{index}", log.Message);
                parameters.Add($"SpanId{index}", log.SpanId);
                parameters.Add($"TraceId{index}", log.TraceId);
                parameters.Add($"ParentId{index}", log.ParentId);
                parameters.Add($"OriginalFormat{index}", log.OriginalFormat);
                parameters.Add($"EventName{index}", log.EventName);
            }

            var logIds = new long[logBatch.Length];
            // Keep one RETURNING result set per log in a single command so each generated ID maps
            // deterministically to its source log without relying on SQLite RETURNING row order.
            using (var reader = connection.QueryMultiple(sql.ToString(), parameters, transaction))
            {
                for (var index = 0; index < logBatch.Length; index++)
                {
                    logIds[index] = reader.ReadSingle<long>();
                }
            }

            InsertLogAttributes(connection, transaction, logBatch, logIds);
            for (var index = 0; index < logBatch.Length; index++)
            {
                var log = logBatch[index].Log;
                addedLogs.Add(new OtlpLogEntry(
                    logIds[index],
                    log.TimeStamp,
                    log.Flags,
                    log.Severity,
                    log.SeverityNumber,
                    log.Message,
                    log.SpanId,
                    log.TraceId,
                    log.ParentId,
                    log.OriginalFormat,
                    log.ResourceView,
                    log.Scope,
                    log.Attributes,
                    log.EventName));
            }
        }
    }

    private static void InsertLogAttributes(SqliteConnection connection, IDbTransaction transaction, PendingLog[] logs, long[] logIds)
    {
        var attributes = logs
            .SelectMany((pendingLog, logIndex) => pendingLog.Log.Attributes.Select((attribute, ordinal) => (LogId: logIds[logIndex], Ordinal: ordinal, Attribute: attribute)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            attributes,
            MaxLogAttributeBatchSize,
            "telemetry_log_attributes",
            ["log_id", "ordinal", "attribute_key", "attribute_value"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.LogId;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Attribute.Key;
                parameters[3].Value = row.Attribute.Value;
            });
    }

    private static void MarkResourcesHaveLogs(SqliteConnection connection, IDbTransaction transaction, HashSet<CachedResource> resources)
    {
        var resourcesToUpdate = resources.Where(resource => !resource.Resource.HasLogs).ToArray();
        foreach (var batch in resourcesToUpdate.Chunk(MaxLogAttributeBatchSize))
        {
            connection.Execute(
                "UPDATE telemetry_resources SET has_logs = 1 WHERE resource_id IN @ResourceIds;",
                new { ResourceIds = batch.Select(resource => resource.ResourceId).ToArray() },
                transaction);
        }
        foreach (var resource in resourcesToUpdate)
        {
            resource.Resource.HasLogs = true;
        }
    }

    private void TrimLogsToCapacity(SqliteConnection connection, IDbTransaction transaction)
    {
        connection.Execute("""
            DELETE FROM telemetry_logs
            WHERE log_id IN (
                SELECT log_id
                FROM telemetry_logs
                ORDER BY timestamp_ticks, log_id
                LIMIT MAX((SELECT COUNT(*) FROM telemetry_logs) - @MaxLogCount, 0)
            );

            DELETE FROM telemetry_resource_views
            WHERE NOT EXISTS (
                SELECT 1 FROM telemetry_logs WHERE telemetry_logs.resource_view_id = telemetry_resource_views.resource_view_id
                        )
                            AND NOT EXISTS (
                                SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_view_id = telemetry_resource_views.resource_view_id
                            );
            """, new { _otlpContext.Options.MaxLogCount }, transaction);
    }

    private PagedResult<OtlpLogEntry> GetLogsFromDatabase(GetLogsContext context)
    {
        using var connection = _database.OpenConnection();
        var query = BuildLogQuery(context);
        var totalCount = connection.QuerySingle<int>($"SELECT COUNT(*) {query.FromAndWhere}", query.Parameters);
        if (totalCount == 0)
        {
            return PagedResult<OtlpLogEntry>.Empty;
        }

        query.Parameters.Add("StartIndex", context.StartIndex);
        query.Parameters.Add("Count", context.Count);
        var records = connection.Query<LogRecord>(
            $"""
            SELECT
                l.log_id AS LogId,
                l.resource_id AS ResourceId,
                l.resource_view_id AS ResourceViewId,
                l.scope_id AS ScopeId,
                l.timestamp_ticks AS TimestampTicks,
                l.flags AS Flags,
                l.severity AS Severity,
                l.severity_number AS SeverityNumber,
                l.message AS Message,
                l.span_id AS SpanId,
                l.trace_id AS TraceId,
                l.parent_id AS ParentId,
                l.original_format AS OriginalFormat,
                l.event_name AS EventName,
                r.resource_name AS ResourceName,
                r.instance_id AS InstanceId,
                r.uninstrumented_peer AS UninstrumentedPeer,
                r.has_logs AS HasLogs,
                r.has_traces AS HasTraces,
                r.has_metrics AS HasMetrics,
                s.scope_name AS ScopeName,
                s.scope_version AS ScopeVersion
            {query.FromAndWhere}
            ORDER BY l.timestamp_ticks, l.log_id DESC
            LIMIT @Count OFFSET @StartIndex;
            """,
            query.Parameters).AsList();

        return new PagedResult<OtlpLogEntry>
        {
            TotalItemCount = totalCount,
            Items = MaterializeLogs(connection, records),
            IsFull = totalCount >= _otlpContext.Options.MaxLogCount
        };
    }

    private PagedResult<LogSummary> GetLogSummariesFromDatabase(GetLogsContext context)
    {
        using var connection = _database.OpenConnection();
        var query = BuildLogQuery(context);
        query.Parameters.Add("StartIndex", Math.Max(context.StartIndex, 0));
        query.Parameters.Add("Count", Math.Max(context.Count, 0));
        query.Parameters.Add("MaxLogCount", _otlpContext.Options.MaxLogCount);

        // Return the aggregate and page display data together. Only attributes that affect the
        // message column are aggregated, avoiding the extra materialization queries for every page.
        var records = connection.Query<LogSummaryRecord>($"""
            WITH
            filtered_logs AS (
                SELECT
                    l.*,
                    r.resource_name,
                    r.instance_id,
                    r.uninstrumented_peer
                {query.FromAndWhere}
            ),
            log_aggregate AS (
                SELECT COUNT(*) AS TotalItemCount
                FROM filtered_logs
            ),
            paged_logs AS (
                SELECT *
                FROM filtered_logs
                ORDER BY timestamp_ticks, log_id DESC
                LIMIT @Count OFFSET @StartIndex
            ),
            log_attribute_summaries AS (
                SELECT
                    a.log_id,
                    MAX(CASE WHEN a.attribute_key = 'exception.stacktrace' THEN a.attribute_value END) AS exception_stacktrace,
                    MAX(CASE WHEN a.attribute_key = 'exception.message' THEN a.attribute_value END) AS exception_message,
                    MAX(CASE WHEN a.attribute_key = 'exception.type' THEN a.attribute_value END) AS exception_type,
                    MAX(CASE WHEN a.attribute_key = 'gen_ai.system' THEN 1 ELSE 0 END) AS has_gen_ai_system,
                    MAX(CASE WHEN a.attribute_key = 'gen_ai.system' AND LENGTH(a.attribute_value) > 0 THEN 1 ELSE 0 END) AS has_non_empty_gen_ai_system,
                    MAX(CASE WHEN a.attribute_key = 'gen_ai.provider.name' AND LENGTH(a.attribute_value) > 0 THEN 1 ELSE 0 END) AS has_non_empty_gen_ai_provider
                FROM telemetry_log_attributes a
                JOIN paged_logs pl ON pl.log_id = a.log_id
                WHERE a.attribute_key IN (
                    'exception.stacktrace',
                    'exception.message',
                    'exception.type',
                    'gen_ai.system',
                    'gen_ai.provider.name')
                GROUP BY a.log_id
            )
            SELECT
                a.TotalItemCount,
                (SELECT COUNT(*) FROM telemetry_logs) >= @MaxLogCount AS IsFull,
                pl.log_id AS InternalId,
                pl.timestamp_ticks AS TimestampTicks,
                pl.severity AS Severity,
                pl.message AS Message,
                pl.span_id AS SpanId,
                pl.trace_id AS TraceId,
                pl.resource_id AS ResourceId,
                pl.resource_name AS ResourceName,
                pl.instance_id AS InstanceId,
                pl.uninstrumented_peer AS UninstrumentedPeer,
                CASE
                    WHEN LENGTH(las.exception_stacktrace) > 0 THEN las.exception_stacktrace
                    WHEN LENGTH(las.exception_message) > 0 AND LENGTH(las.exception_type) > 0 THEN las.exception_type || ': ' || las.exception_message
                    WHEN LENGTH(las.exception_message) > 0 THEN las.exception_message
                END AS ExceptionText,
                                COALESCE(
                                        CASE WHEN las.has_gen_ai_system = 1
                                                THEN las.has_non_empty_gen_ai_system
                                                ELSE las.has_non_empty_gen_ai_provider
                                        END,
                                        0) = 1 OR
                                CASE WHEN EXISTS (
                                        SELECT 1
                                        FROM telemetry_span_attributes sa
                                        WHERE sa.trace_id = pl.trace_id
                                            AND sa.span_id = pl.span_id
                                            AND sa.attribute_key = 'gen_ai.system'
                                ) THEN EXISTS (
                                        SELECT 1
                                        FROM telemetry_span_attributes sa
                                        WHERE sa.trace_id = pl.trace_id
                                            AND sa.span_id = pl.span_id
                                            AND sa.attribute_key = 'gen_ai.system'
                                            AND LENGTH(sa.attribute_value) > 0
                                ) ELSE EXISTS (
                                        SELECT 1
                                        FROM telemetry_span_attributes sa
                                        WHERE sa.trace_id = pl.trace_id
                                            AND sa.span_id = pl.span_id
                                            AND sa.attribute_key = 'gen_ai.provider.name'
                                            AND LENGTH(sa.attribute_value) > 0
                                ) END AS HasGenAI
            FROM log_aggregate a
            LEFT JOIN paged_logs pl ON 1 = 1
            LEFT JOIN log_attribute_summaries las ON las.log_id = pl.log_id
            ORDER BY pl.timestamp_ticks, pl.log_id DESC;
            """, query.Parameters).AsList();

        var firstRecord = records[0];
        return new PagedResult<LogSummary>
        {
            Items = records
                .Where(record => record.InternalId is not null)
                .Select(record => new LogSummary
                {
                    InternalId = record.InternalId!.Value,
                    TimeStamp = new DateTime(record.TimestampTicks!.Value, DateTimeKind.Utc),
                    Severity = (LogLevel)record.Severity!.Value,
                    Message = record.Message!,
                    SpanId = record.SpanId!,
                    TraceId = record.TraceId!,
                    Resource = GetCachedResource(record.ResourceId!.Value)!,
                    ExceptionText = record.ExceptionText,
                    HasGenAI = record.HasGenAI!.Value
                }).ToList(),
            TotalItemCount = firstRecord.TotalItemCount,
            IsFull = firstRecord.IsFull
        };
    }

    private OtlpLogEntry? GetLogFromDatabase(long logId)
    {
        using var connection = _database.OpenConnection();
        var records = connection.Query<LogRecord>("""
            SELECT
                l.log_id AS LogId,
                l.resource_id AS ResourceId,
                l.resource_view_id AS ResourceViewId,
                l.scope_id AS ScopeId,
                l.timestamp_ticks AS TimestampTicks,
                l.flags AS Flags,
                l.severity AS Severity,
                l.severity_number AS SeverityNumber,
                l.message AS Message,
                l.span_id AS SpanId,
                l.trace_id AS TraceId,
                l.parent_id AS ParentId,
                l.original_format AS OriginalFormat,
                l.event_name AS EventName,
                r.resource_name AS ResourceName,
                r.instance_id AS InstanceId,
                r.uninstrumented_peer AS UninstrumentedPeer,
                r.has_logs AS HasLogs,
                r.has_traces AS HasTraces,
                r.has_metrics AS HasMetrics,
                s.scope_name AS ScopeName,
                s.scope_version AS ScopeVersion
            FROM telemetry_logs l
            JOIN telemetry_resources r ON r.resource_id = l.resource_id
            JOIN telemetry_scopes s ON s.scope_id = l.scope_id
            WHERE l.log_id = @LogId;
            """, new { LogId = logId }).AsList();
        return records.Count == 0 ? null : MaterializeLogs(connection, records)[0];
    }

    private List<OtlpLogEntry> GetLogsForSpanFromDatabase(string traceId, string spanId)
    {
        return GetLogsFromDatabase(new GetLogsContext
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
    }

    private List<OtlpLogEntry> GetLogsForTraceFromDatabase(string traceId)
    {
        return GetLogsFromDatabase(new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter { Field = KnownStructuredLogFields.TraceIdField, Condition = FilterCondition.Equals, Value = traceId }
            ]
        }).Items;
    }

    private List<string> GetLogPropertyKeysFromDatabase(ResourceKey? resourceKey)
    {
        using var connection = _database.OpenConnection();
        var sql = new StringBuilder("""
            SELECT DISTINCT a.attribute_key
            FROM telemetry_log_attributes a
            JOIN telemetry_logs l ON l.log_id = a.log_id
            JOIN telemetry_resources r ON r.resource_id = l.resource_id
            """);
        var parameters = new DynamicParameters();
        if (resourceKey is not null)
        {
            sql.Append(" WHERE r.resource_name = @ResourceName COLLATE NOCASE");
            parameters.Add("ResourceName", resourceKey.Value.Name);
            if (resourceKey.Value.InstanceId is not null)
            {
                sql.Append(" AND r.instance_id = @InstanceId COLLATE NOCASE");
                parameters.Add("InstanceId", resourceKey.Value.InstanceId);
            }
        }
        sql.Append(" ORDER BY a.attribute_key;");
        return connection.Query<string>(sql.ToString(), parameters).AsList();
    }

    private Dictionary<string, int> GetLogsFieldValuesFromDatabase(string attributeName)
    {
        if (attributeName == KnownStructuredLogFields.TimestampField)
        {
            return new Dictionary<string, int>(StringComparers.OtlpAttribute);
        }

        using var connection = _database.OpenConnection();
        var parameters = new DynamicParameters();
        var expression = GetLogFieldExpression(attributeName, parameters, "FieldValue", coalesceMissing: false);
        return connection.Query<FieldValueRecord>($"""
            WITH field_values AS (
                SELECT {expression} AS FieldValue
                FROM telemetry_logs l
                JOIN telemetry_resources r ON r.resource_id = l.resource_id
                JOIN telemetry_scopes s ON s.scope_id = l.scope_id
            )
            SELECT FieldValue, COUNT(*) AS ValueCount
            FROM field_values
            WHERE FieldValue IS NOT NULL
            GROUP BY FieldValue;
            """, parameters)
            .ToDictionary(record => record.FieldValue!, record => record.ValueCount, StringComparers.OtlpAttribute);
    }

    private static LogQuery BuildLogQuery(GetLogsContext context)
    {
        var parameters = new DynamicParameters();
        var predicates = new List<string>();
        if (context.ResourceKeys.Count > 0)
        {
            var resourcePredicates = new List<string>();
            for (var i = 0; i < context.ResourceKeys.Count; i++)
            {
                var key = context.ResourceKeys[i];
                var predicate = $"r.resource_name = @ResourceName{i} COLLATE NOCASE";
                parameters.Add($"ResourceName{i}", key.Name);
                if (key.InstanceId is not null)
                {
                    predicate += $" AND r.instance_id = @InstanceId{i} COLLATE NOCASE";
                    parameters.Add($"InstanceId{i}", key.InstanceId);
                }
                resourcePredicates.Add($"({predicate})");
            }
            predicates.Add($"({string.Join(" OR ", resourcePredicates)})");
        }

        var filterIndex = 0;
        foreach (var filter in context.Filters.Where(filter => filter.Enabled))
        {
            if (filter is not FieldTelemetryFilter fieldFilter)
            {
                throw new NotSupportedException($"Unsupported log filter type '{filter.GetType().Name}'.");
            }
            predicates.Add(BuildLogFilterPredicate(fieldFilter, parameters, filterIndex++));
        }

        if (context.TextFragments is { Length: > 0 })
        {
            for (var i = 0; i < context.TextFragments.Length; i++)
            {
                var parameterName = $"TextFragment{i}";
                parameters.Add(parameterName, CreateContainsLikePattern(context.TextFragments[i]));
                predicates.Add($$"""
                    (
                        l.message LIKE @{{parameterName}} ESCAPE '!'
                        OR s.scope_name LIKE @{{parameterName}} ESCAPE '!'
                        OR l.trace_id LIKE @{{parameterName}} ESCAPE '!'
                        OR l.span_id LIKE @{{parameterName}} ESCAPE '!'
                        OR l.severity_name LIKE @{{parameterName}} ESCAPE '!'
                        OR r.resource_name LIKE @{{parameterName}} ESCAPE '!'
                        OR COALESCE(l.event_name, '') LIKE @{{parameterName}} ESCAPE '!'
                        OR EXISTS (
                            SELECT 1
                            FROM telemetry_log_attributes text_attribute
                            WHERE text_attribute.log_id = l.log_id
                              AND (
                                  text_attribute.attribute_key LIKE @{{parameterName}} ESCAPE '!'
                                  OR text_attribute.attribute_value LIKE @{{parameterName}} ESCAPE '!'
                              )
                        )
                    )
                    """);
            }
        }

        var where = predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
        return new LogQuery($"""
            FROM telemetry_logs l
            JOIN telemetry_resources r ON r.resource_id = l.resource_id
            JOIN telemetry_scopes s ON s.scope_id = l.scope_id
            {where}
            """, parameters);
    }

    private static string BuildLogFilterPredicate(FieldTelemetryFilter filter, DynamicParameters parameters, int index)
    {
        var parameterName = $"FilterValue{index}";
        if (filter.Field == nameof(OtlpLogEntry.Severity))
        {
            if (!Enum.TryParse<LogLevel>(filter.Value, ignoreCase: true, out var severity))
            {
                return "1 = 1";
            }
            parameters.Add(parameterName, (int)severity);
            return BuildNumericPredicate("l.severity", filter.Condition, parameterName);
        }

        if (filter.Field == nameof(OtlpLogEntry.TimeStamp))
        {
            var timestamp = DateTime.Parse(filter.Value, CultureInfo.InvariantCulture);
            parameters.Add(parameterName, timestamp.Ticks);
            return BuildNumericPredicate("l.timestamp_ticks", filter.Condition, parameterName);
        }

        if (filter.Field == KnownStructuredLogFields.TimestampField)
        {
            if (!DateTime.TryParse(filter.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var timestamp))
            {
                return "0 = 1";
            }
            parameters.Add(parameterName, timestamp.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond);
            return BuildNumericPredicate($"l.timestamp_ticks / {TimeSpan.TicksPerMillisecond}", filter.Condition, parameterName);
        }

        var expression = GetLogFieldExpression(filter.Field, parameters, $"AttributeName{index}");
        parameters.Add(
            parameterName,
            filter.Condition is FilterCondition.Contains or FilterCondition.NotContains
                ? CreateContainsLikePattern(filter.Value)
                : filter.Value);
        return BuildStringPredicate(expression, filter.Condition, parameterName);
    }

    private static string GetLogFieldExpression(string field, DynamicParameters parameters, string attributeParameterName, bool coalesceMissing = true)
    {
        return field switch
        {
            nameof(OtlpLogEntry.Message) or KnownStructuredLogFields.MessageField => "l.message",
            KnownStructuredLogFields.TraceIdField => "l.trace_id",
            KnownStructuredLogFields.SpanIdField => "l.span_id",
            KnownStructuredLogFields.OriginalFormatField => coalesceMissing ? "COALESCE(l.original_format, '')" : "l.original_format",
            KnownStructuredLogFields.CategoryField => "s.scope_name",
            KnownStructuredLogFields.EventNameField => coalesceMissing ? "COALESCE(l.event_name, '')" : "l.event_name",
            KnownStructuredLogFields.LevelField => "l.severity_name",
            KnownStructuredLogFields.TimestampField => $"CAST(l.timestamp_ticks / {TimeSpan.TicksPerMillisecond} AS TEXT)",
            KnownResourceFields.ServiceNameField => "r.resource_name",
            _ => GetAttributeExpression(field, parameters, attributeParameterName, coalesceMissing)
        };

        static string GetAttributeExpression(string field, DynamicParameters parameters, string parameterName, bool coalesceMissing)
        {
            parameters.Add(parameterName, field);
            var expression = $"""
                (
                    SELECT attribute.attribute_value
                    FROM telemetry_log_attributes attribute
                    WHERE attribute.log_id = l.log_id
                      AND attribute.attribute_key = @{parameterName}
                    LIMIT 1
                )
                """;
            return coalesceMissing ? $"COALESCE({expression}, '')" : expression;
        }
    }

    private static string BuildStringPredicate(string expression, FilterCondition condition, string parameterName)
    {
        return condition switch
        {
            FilterCondition.Equals => $"{expression} = @{parameterName} COLLATE NOCASE",
            FilterCondition.Contains => $"{expression} LIKE @{parameterName} ESCAPE '!'",
            FilterCondition.GreaterThan or FilterCondition.LessThan or FilterCondition.GreaterThanOrEqual or FilterCondition.LessThanOrEqual => "0 = 1",
            FilterCondition.NotEqual => $"{expression} <> @{parameterName} COLLATE NOCASE",
            FilterCondition.NotContains => $"{expression} NOT LIKE @{parameterName} ESCAPE '!'",
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, null)
        };
    }

    private static string BuildNumericPredicate(string expression, FilterCondition condition, string parameterName)
    {
        var operation = condition switch
        {
            FilterCondition.Equals => "=",
            FilterCondition.GreaterThan => ">",
            FilterCondition.LessThan => "<",
            FilterCondition.GreaterThanOrEqual => ">=",
            FilterCondition.LessThanOrEqual => "<=",
            FilterCondition.NotEqual => "<>",
            FilterCondition.Contains or FilterCondition.NotContains => null,
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition, null)
        };
        return operation is null ? "0 = 1" : $"{expression} {operation} @{parameterName}";
    }

    private List<OtlpLogEntry> MaterializeLogs(SqliteConnection connection, List<LogRecord> records)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var logIds = records.Select(record => record.LogId).Distinct().ToArray();
        var logAttributes = connection.Query<OwnedAttributeRecord>("""
            SELECT log_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_log_attributes
            WHERE log_id IN @Ids
            ORDER BY log_id, ordinal;
            """, new { Ids = logIds }).ToLookup(record => record.OwnerId);
        var results = new List<OtlpLogEntry>(records.Count);
        foreach (var record in records)
        {
            var (_, view, scope) = GetCachedTelemetryMetadata(record.ResourceId, record.ResourceViewId, record.ScopeId, CachedTelemetryType.Logs);

            results.Add(new OtlpLogEntry(
                record.LogId,
                new DateTime(record.TimestampTicks, DateTimeKind.Utc),
                checked((uint)record.Flags),
                (LogLevel)record.Severity,
                record.SeverityNumber,
                record.Message,
                record.SpanId,
                record.TraceId,
                record.ParentId,
                record.OriginalFormat,
                view,
                scope,
                ToPairs(logAttributes[record.LogId]),
                record.EventName));
        }

        return results;

        static KeyValuePair<string, string>[] ToPairs(IEnumerable<OwnedAttributeRecord> attributes)
        {
            return attributes.Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray();
        }
    }

    private void ClearSelectedLogsFromDatabase(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        EnsureWritable();
        using var connection = _database.OpenConnection();
        var resources = connection.Query<TelemetryResourceRecord>("""
            SELECT resource_name AS ResourceName, instance_id AS InstanceId
            FROM telemetry_resources;
            """);
        foreach (var resource in resources)
        {
            var key = new ResourceKey(resource.ResourceName, resource.InstanceId);
            if (!selectedResources.TryGetValue(key.GetCompositeName(), out var dataTypes))
            {
                continue;
            }

            if (dataTypes.Contains(AspireDataType.Resource))
            {
                DeleteTelemetryResourceFromDatabase(key);
            }
            else if (dataTypes.Contains(AspireDataType.StructuredLogs))
            {
                ClearStructuredLogsFromDatabase(key);
            }
        }
    }

    private void ClearStructuredLogsFromDatabase(ResourceKey? resourceKey)
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

            connection.Execute($"""
                DELETE FROM telemetry_logs
                WHERE resource_id IN (SELECT resource_id FROM telemetry_resources{where});

                DELETE FROM telemetry_resource_views
                WHERE NOT EXISTS (
                    SELECT 1 FROM telemetry_logs WHERE telemetry_logs.resource_view_id = telemetry_resource_views.resource_view_id
                                )
                                    AND NOT EXISTS (
                                        SELECT 1 FROM telemetry_spans WHERE telemetry_spans.resource_view_id = telemetry_resource_views.resource_view_id
                                    );

                UPDATE telemetry_resources
                SET has_logs = EXISTS (SELECT 1 FROM telemetry_logs WHERE telemetry_logs.resource_id = telemetry_resources.resource_id);
                """, parameters, transaction);
            DeleteOrphanedScopes(connection, transaction);
            transaction.Commit();
            ClearMetadataCache();
        }
    }

    private sealed record LogQuery(string FromAndWhere, DynamicParameters Parameters);

    private sealed record PendingLog(long ResourceId, long ResourceViewId, long ScopeId, OtlpLogEntry Log);

    private class AttributeRecord
    {
        public required string AttributeKey { get; init; }
        public required string AttributeValue { get; init; }
    }

    private sealed class OwnedAttributeRecord : AttributeRecord
    {
        public required long OwnerId { get; init; }
    }

    private sealed class LogRecord
    {
        public required long LogId { get; init; }
        public required long ResourceId { get; init; }
        public required long ResourceViewId { get; init; }
        public required long ScopeId { get; init; }
        public required long TimestampTicks { get; init; }
        public required long Flags { get; init; }
        public required int Severity { get; init; }
        public required int SeverityNumber { get; init; }
        public required string Message { get; init; }
        public required string SpanId { get; init; }
        public required string TraceId { get; init; }
        public required string ParentId { get; init; }
        public string? OriginalFormat { get; init; }
        public string? EventName { get; init; }
        public required string ResourceName { get; init; }
        public string? InstanceId { get; init; }
        public required bool UninstrumentedPeer { get; init; }
        public required bool HasLogs { get; init; }
        public required bool HasTraces { get; init; }
        public required bool HasMetrics { get; init; }
        public required string ScopeName { get; init; }
        public required string ScopeVersion { get; init; }
    }

    private sealed class LogSummaryRecord
    {
        public required int TotalItemCount { get; init; }
        public required bool IsFull { get; init; }
        public long? InternalId { get; init; }
        public long? TimestampTicks { get; init; }
        public int? Severity { get; init; }
        public string? Message { get; init; }
        public string? SpanId { get; init; }
        public string? TraceId { get; init; }
        public long? ResourceId { get; init; }
        public string? ResourceName { get; init; }
        public string? InstanceId { get; init; }
        public bool? UninstrumentedPeer { get; init; }
        public string? ExceptionText { get; init; }
        public bool? HasGenAI { get; init; }
    }

    private sealed class FieldValueRecord
    {
        public string? FieldValue { get; init; }
        public required int ValueCount { get; init; }
    }

}
