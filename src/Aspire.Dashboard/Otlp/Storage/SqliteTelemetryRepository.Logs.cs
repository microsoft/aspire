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
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private List<OtlpLogEntry> AddLogsToDatabase(AddContext context, RepeatedField<ResourceLogs> resourceLogs)
    {
        var addedLogs = new List<OtlpLogEntry>();
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            foreach (var resourceLogsItem in resourceLogs)
            {
                OtlpResourceView resourceView;
                long resourceId;
                long resourceViewId;
                try
                {
                    var resourceKey = resourceLogsItem.Resource.GetResourceKey();
                    resourceId = GetOrAddTelemetryResource(connection, transaction, resourceKey);
                    var resource = new OtlpResource(resourceKey.Name, resourceKey.InstanceId, uninstrumentedPeer: false, _otlpContext)
                    {
                        HasLogs = true
                    };
                    resourceView = new OtlpResourceView(resource, resourceLogsItem.Resource.Attributes);
                    resourceViewId = InsertResourceView(connection, transaction, resourceId, resourceView.Properties);
                }
                catch (Exception exception)
                {
                    context.FailureCount += resourceLogsItem.ScopeLogs.Sum(scope => scope.LogRecords.Count);
                    _otlpContext.Logger.LogInformation(exception, "Error adding resource.");
                    continue;
                }

                foreach (var scopeLogs in resourceLogsItem.ScopeLogs)
                {
                    OtlpScope scope;
                    long scopeId;
                    try
                    {
                        (scopeId, scope) = GetOrAddScope(connection, transaction, scopeLogs.Scope);
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
                            var log = new OtlpLogEntry(record, resourceView, scope, _otlpContext);
                            var logId = connection.QuerySingle<long>("""
                                INSERT INTO telemetry_logs (
                                    resource_id, resource_view_id, scope_id, timestamp_ticks, flags, severity,
                                    severity_name, severity_number, message, span_id, trace_id, parent_id,
                                    original_format, event_name)
                                VALUES (
                                    @ResourceId, @ResourceViewId, @ScopeId, @TimestampTicks, @Flags, @Severity,
                                    @SeverityName, @SeverityNumber, @Message, @SpanId, @TraceId, @ParentId,
                                    @OriginalFormat, @EventName)
                                RETURNING log_id;
                                """, new
                            {
                                ResourceId = resourceId,
                                ResourceViewId = resourceViewId,
                                ScopeId = scopeId,
                                TimestampTicks = log.TimeStamp.Ticks,
                                Flags = (long)log.Flags,
                                Severity = (int)log.Severity,
                                SeverityName = log.Severity.ToString(),
                                log.SeverityNumber,
                                log.Message,
                                log.SpanId,
                                log.TraceId,
                                log.ParentId,
                                log.OriginalFormat,
                                log.EventName
                            }, transaction);

                            connection.Execute("""
                                INSERT INTO telemetry_log_attributes (log_id, ordinal, attribute_key, attribute_value)
                                VALUES (@LogId, @Ordinal, @Key, @Value);
                                """, log.Attributes.Select((attribute, ordinal) => new
                            {
                                LogId = logId,
                                Ordinal = ordinal,
                                attribute.Key,
                                attribute.Value
                            }), transaction);
                            addedLogs.Add(new OtlpLogEntry(
                                logId,
                                log.TimeStamp,
                                log.Flags,
                                log.Severity,
                                log.SeverityNumber,
                                log.Message,
                                log.SpanId,
                                log.TraceId,
                                log.ParentId,
                                log.OriginalFormat,
                                resourceView,
                                scope,
                                log.Attributes,
                                log.EventName));
                            context.SuccessCount++;
                        }
                        catch (Exception exception)
                        {
                            context.FailureCount++;
                            _otlpContext.Logger.LogInformation(exception, "Error adding log entry.");
                        }
                    }
                }

                connection.Execute(
                    "UPDATE telemetry_resources SET has_logs = 1 WHERE resource_id = @ResourceId;",
                    new { ResourceId = resourceId },
                    transaction);
            }

            TrimLogsToCapacity(connection, transaction);
            transaction.Commit();
        }

        return addedLogs;
    }

    private long GetOrAddTelemetryResource(SqliteConnection connection, IDbTransaction transaction, ResourceKey resourceKey)
    {
        var instanceIdIsNull = resourceKey.InstanceId is null;
        var instanceId = resourceKey.InstanceId ?? string.Empty;
        var resourceId = connection.QuerySingleOrDefault<long?>("""
            SELECT resource_id
            FROM telemetry_resources
            WHERE resource_name = @ResourceName
              AND instance_id_is_null = @InstanceIdIsNull
              AND instance_id = @InstanceId;
            """, new { ResourceName = resourceKey.Name, InstanceIdIsNull = instanceIdIsNull, InstanceId = instanceId }, transaction);
        if (resourceId is not null)
        {
            return resourceId.Value;
        }

        var resourceCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_resources;", transaction: transaction);
        if (resourceCount >= _otlpContext.Options.MaxResourceCount)
        {
            throw new InvalidOperationException($"Resource limit of {_otlpContext.Options.MaxResourceCount} reached. Resource '{resourceKey}' will not be added.");
        }

        return connection.QuerySingle<long>("""
            INSERT INTO telemetry_resources (resource_name, instance_id, instance_id_is_null)
            VALUES (@ResourceName, @InstanceId, @InstanceIdIsNull)
            RETURNING resource_id;
            """, new { ResourceName = resourceKey.Name, InstanceId = instanceId, InstanceIdIsNull = instanceIdIsNull }, transaction);
    }

    private static long InsertResourceView(
        SqliteConnection connection,
        IDbTransaction transaction,
        long resourceId,
        KeyValuePair<string, string>[] properties)
    {
        var resourceViewId = connection.QuerySingle<long>("""
            INSERT INTO telemetry_resource_views (resource_id)
            VALUES (@ResourceId)
            RETURNING resource_view_id;
            """, new { ResourceId = resourceId }, transaction);
        connection.Execute("""
            INSERT INTO telemetry_resource_view_attributes (resource_view_id, ordinal, attribute_key, attribute_value)
            VALUES (@ResourceViewId, @Ordinal, @Key, @Value);
            """, properties.Select((property, ordinal) => new
        {
            ResourceViewId = resourceViewId,
            Ordinal = ordinal,
            property.Key,
            property.Value
        }), transaction);
        return resourceViewId;
    }

    private (long ScopeId, OtlpScope Scope) GetOrAddScope(
        SqliteConnection connection,
        IDbTransaction transaction,
        InstrumentationScope? instrumentationScope)
    {
        var incomingScope = instrumentationScope is null
            ? OtlpScope.Empty
            : new OtlpScope(
                instrumentationScope.Name,
                instrumentationScope.Version,
                instrumentationScope.Attributes.ToKeyValuePairs(_otlpContext));
        var existing = connection.QuerySingleOrDefault<ScopeRecord>("""
            SELECT scope_id AS ScopeId, scope_name AS ScopeName, scope_version AS ScopeVersion
            FROM telemetry_scopes
            WHERE scope_name = @ScopeName;
            """, new { ScopeName = incomingScope.Name }, transaction);
        if (existing is not null)
        {
            var attributes = connection.Query<AttributeRecord>("""
                SELECT attribute_key AS AttributeKey, attribute_value AS AttributeValue
                FROM telemetry_scope_attributes
                WHERE scope_id = @ScopeId
                ORDER BY ordinal;
                """, new { existing.ScopeId }, transaction)
                .Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue))
                .ToArray();
            return (existing.ScopeId, new OtlpScope(existing.ScopeName, existing.ScopeVersion, attributes));
        }

        var scopeCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_scopes;", transaction: transaction);
        if (scopeCount >= TelemetryRepositoryLimits.MaxScopeCount)
        {
            throw new InvalidOperationException($"Scope limit of {TelemetryRepositoryLimits.MaxScopeCount} reached. Scope '{incomingScope.Name}' will not be added.");
        }

        var scopeId = connection.QuerySingle<long>("""
            INSERT INTO telemetry_scopes (scope_name, scope_version)
            VALUES (@ScopeName, @ScopeVersion)
            RETURNING scope_id;
            """, new { ScopeName = incomingScope.Name, ScopeVersion = incomingScope.Version }, transaction);
        connection.Execute("""
            INSERT INTO telemetry_scope_attributes (scope_id, ordinal, attribute_key, attribute_value)
            VALUES (@ScopeId, @Ordinal, @Key, @Value);
            """, incomingScope.Attributes.Select((attribute, ordinal) => new
        {
            ScopeId = scopeId,
            Ordinal = ordinal,
            attribute.Key,
            attribute.Value
        }), transaction);
        return (scopeId, incomingScope);
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
                r.instance_id_is_null AS InstanceIdIsNull,
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
                r.instance_id_is_null AS InstanceIdIsNull,
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
            sql.Append(" WHERE r.resource_name = @ResourceName COLLATE ORDINAL_IGNORE_CASE");
            parameters.Add("ResourceName", resourceKey.Value.Name);
            if (resourceKey.Value.InstanceId is not null)
            {
                sql.Append(" AND r.instance_id_is_null = 0 AND r.instance_id = @InstanceId COLLATE ORDINAL_IGNORE_CASE");
                parameters.Add("InstanceId", resourceKey.Value.InstanceId);
            }
        }
        sql.Append(" ORDER BY a.attribute_key;");
        return connection.Query<string>(sql.ToString(), parameters).AsList();
    }

    private Dictionary<string, int> GetLogsFieldValuesFromDatabase(string attributeName)
    {
        using var connection = _database.OpenConnection();
        var parameters = new DynamicParameters();
        var expression = GetLogFieldExpression(attributeName, parameters, "FieldValue");
        return connection.Query<FieldValueRecord>($"""
            SELECT {expression} AS FieldValue, COUNT(*) AS ValueCount
            FROM telemetry_logs l
            JOIN telemetry_resources r ON r.resource_id = l.resource_id
            JOIN telemetry_scopes s ON s.scope_id = l.scope_id
            GROUP BY {expression};
            """, parameters)
            .Where(record => record.FieldValue is not null)
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
                var predicate = $"r.resource_name = @ResourceName{i} COLLATE ORDINAL_IGNORE_CASE";
                parameters.Add($"ResourceName{i}", key.Name);
                if (key.InstanceId is not null)
                {
                    predicate += $" AND r.instance_id_is_null = 0 AND r.instance_id = @InstanceId{i} COLLATE ORDINAL_IGNORE_CASE";
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
                parameters.Add(parameterName, context.TextFragments[i]);
                predicates.Add($$"""
                    (
                        ordinal_contains(l.message, @{{parameterName}})
                        OR ordinal_contains(s.scope_name, @{{parameterName}})
                        OR ordinal_contains(l.trace_id, @{{parameterName}})
                        OR ordinal_contains(l.span_id, @{{parameterName}})
                        OR ordinal_contains(l.severity_name, @{{parameterName}})
                        OR ordinal_contains(r.resource_name, @{{parameterName}})
                        OR ordinal_contains(COALESCE(l.event_name, ''), @{{parameterName}})
                        OR EXISTS (
                            SELECT 1
                            FROM telemetry_log_attributes text_attribute
                            WHERE text_attribute.log_id = l.log_id
                              AND (
                                  ordinal_contains(text_attribute.attribute_key, @{{parameterName}})
                                  OR ordinal_contains(text_attribute.attribute_value, @{{parameterName}})
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
        parameters.Add(parameterName, filter.Value);
        return BuildStringPredicate(expression, filter.Condition, parameterName);
    }

    private static string GetLogFieldExpression(string field, DynamicParameters parameters, string attributeParameterName)
    {
        return field switch
        {
            nameof(OtlpLogEntry.Message) or KnownStructuredLogFields.MessageField => "l.message",
            KnownStructuredLogFields.TraceIdField => "l.trace_id",
            KnownStructuredLogFields.SpanIdField => "l.span_id",
            KnownStructuredLogFields.OriginalFormatField => "COALESCE(l.original_format, '')",
            KnownStructuredLogFields.CategoryField => "s.scope_name",
            KnownStructuredLogFields.EventNameField => "COALESCE(l.event_name, '')",
            KnownStructuredLogFields.LevelField => "l.severity_name",
            KnownStructuredLogFields.TimestampField => $"CAST(l.timestamp_ticks / {TimeSpan.TicksPerMillisecond} AS TEXT)",
            KnownResourceFields.ServiceNameField => "r.resource_name",
            _ => GetAttributeExpression(field, parameters, attributeParameterName)
        };

        static string GetAttributeExpression(string field, DynamicParameters parameters, string parameterName)
        {
            parameters.Add(parameterName, field);
            return $"""
                COALESCE((
                    SELECT attribute.attribute_value
                    FROM telemetry_log_attributes attribute
                    WHERE attribute.log_id = l.log_id
                      AND attribute.attribute_key = @{parameterName}
                    LIMIT 1
                ), '')
                """;
        }
    }

    private static string BuildStringPredicate(string expression, FilterCondition condition, string parameterName)
    {
        return condition switch
        {
            FilterCondition.Equals => $"{expression} = @{parameterName} COLLATE ORDINAL_IGNORE_CASE",
            FilterCondition.Contains => $"ordinal_contains({expression}, @{parameterName})",
            FilterCondition.GreaterThan or FilterCondition.LessThan or FilterCondition.GreaterThanOrEqual or FilterCondition.LessThanOrEqual => "0 = 1",
            FilterCondition.NotEqual => $"{expression} <> @{parameterName} COLLATE ORDINAL_IGNORE_CASE",
            FilterCondition.NotContains => $"NOT ordinal_contains({expression}, @{parameterName})",
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
        var viewIds = records.Select(record => record.ResourceViewId).Distinct().ToArray();
        var scopeIds = records.Select(record => record.ScopeId).Distinct().ToArray();
        var logAttributes = connection.Query<OwnedAttributeRecord>("""
            SELECT log_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_log_attributes
            WHERE log_id IN @Ids
            ORDER BY log_id, ordinal;
            """, new { Ids = logIds }).ToLookup(record => record.OwnerId);
        var viewAttributes = connection.Query<OwnedAttributeRecord>("""
            SELECT resource_view_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_resource_view_attributes
            WHERE resource_view_id IN @Ids
            ORDER BY resource_view_id, ordinal;
            """, new { Ids = viewIds }).ToLookup(record => record.OwnerId);
        var scopeAttributes = connection.Query<OwnedAttributeRecord>("""
            SELECT scope_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_scope_attributes
            WHERE scope_id IN @Ids
            ORDER BY scope_id, ordinal;
            """, new { Ids = scopeIds }).ToLookup(record => record.OwnerId);

        var resources = new Dictionary<long, OtlpResource>();
        var views = new Dictionary<long, OtlpResourceView>();
        var scopes = new Dictionary<long, OtlpScope>();
        var results = new List<OtlpLogEntry>(records.Count);
        foreach (var record in records)
        {
            if (!resources.TryGetValue(record.ResourceId, out var resource))
            {
                resource = new OtlpResource(
                    record.ResourceName,
                    record.InstanceIdIsNull ? null : record.InstanceId,
                    record.UninstrumentedPeer,
                    _otlpContext)
                {
                    HasLogs = record.HasLogs,
                    HasTraces = record.HasTraces,
                    HasMetrics = record.HasMetrics
                };
                resources.Add(record.ResourceId, resource);
            }
            if (!views.TryGetValue(record.ResourceViewId, out var view))
            {
                view = new OtlpResourceView(resource, ToPairs(viewAttributes[record.ResourceViewId]));
                views.Add(record.ResourceViewId, view);
            }
            if (!scopes.TryGetValue(record.ScopeId, out var scope))
            {
                var attributes = ToPairs(scopeAttributes[record.ScopeId]);
                scope = record.ScopeName == OtlpScope.Empty.Name && record.ScopeVersion.Length == 0 && attributes.Length == 0
                    ? OtlpScope.Empty
                    : new OtlpScope(record.ScopeName, record.ScopeVersion, attributes);
                scopes.Add(record.ScopeId, scope);
            }

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
            SELECT resource_name AS ResourceName, instance_id AS InstanceId, instance_id_is_null AS InstanceIdIsNull
            FROM telemetry_resources;
            """);
        foreach (var resource in resources)
        {
            var key = new ResourceKey(resource.ResourceName, resource.InstanceIdIsNull ? null : resource.InstanceId);
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

    private void DeleteTelemetryResourceFromDatabase(ResourceKey resourceKey)
    {
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var sql = new StringBuilder("""
                DELETE FROM telemetry_resources
                WHERE resource_name = @ResourceName COLLATE ORDINAL_IGNORE_CASE
                """);
            var parameters = new DynamicParameters();
            parameters.Add("ResourceName", resourceKey.Name);
            if (resourceKey.InstanceId is not null)
            {
                sql.Append(" AND instance_id_is_null = 0 AND instance_id = @InstanceId COLLATE ORDINAL_IGNORE_CASE");
                parameters.Add("InstanceId", resourceKey.InstanceId);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
            connection.Execute("""
                DELETE FROM telemetry_traces
                WHERE NOT EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.trace_id = telemetry_traces.trace_id);
                """, transaction: transaction);
            DeleteOrphanedScopes(connection, transaction);
            transaction.Commit();
        }

        _resourceCache.TryRemove(resourceKey, out _);
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
                where = " WHERE resource_name = @ResourceName COLLATE ORDINAL_IGNORE_CASE";
                parameters.Add("ResourceName", resourceKey.Value.Name);
                if (resourceKey.Value.InstanceId is not null)
                {
                    where += " AND instance_id_is_null = 0 AND instance_id = @InstanceId COLLATE ORDINAL_IGNORE_CASE";
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
        }
    }

    private static void DeleteOrphanedScopes(SqliteConnection connection, IDbTransaction transaction)
    {
        connection.Execute("""
            DELETE FROM telemetry_scopes
            WHERE NOT EXISTS (SELECT 1 FROM telemetry_logs WHERE telemetry_logs.scope_id = telemetry_scopes.scope_id)
              AND NOT EXISTS (SELECT 1 FROM telemetry_spans WHERE telemetry_spans.scope_id = telemetry_scopes.scope_id)
              AND NOT EXISTS (SELECT 1 FROM telemetry_metric_instruments WHERE telemetry_metric_instruments.scope_id = telemetry_scopes.scope_id);
            """, transaction: transaction);
    }

    private List<OtlpResource> GetTelemetryResources(bool includeUninstrumentedPeers, string? name)
    {
        var resources = new Dictionary<ResourceKey, OtlpResource>();
        using var connection = _database.OpenConnection();
        foreach (var record in connection.Query<TelemetryResourceStateRecord>("""
            SELECT
                resource_name AS ResourceName,
                instance_id AS InstanceId,
                instance_id_is_null AS InstanceIdIsNull,
                uninstrumented_peer AS UninstrumentedPeer,
                has_logs AS HasLogs,
                has_traces AS HasTraces,
                has_metrics AS HasMetrics
            FROM telemetry_resources;
            """))
        {
            var key = new ResourceKey(record.ResourceName, record.InstanceIdIsNull ? null : record.InstanceId);
            var resource = _resourceCache.GetOrAdd(key, resourceKey =>
            {
                var newResource = new OtlpResource(resourceKey.Name, resourceKey.InstanceId, record.UninstrumentedPeer, _otlpContext);
                newResource.ConfigureDataProviders(
                    (meterName, instrumentName, startTime, endTime) => GetResourceInstrumentFromDatabase(resourceKey, meterName, instrumentName, startTime, endTime),
                    () => GetInstrumentsSummariesFromDatabase(resourceKey),
                    () => GetResourceViewsFromDatabase(resourceKey, newResource));
                return newResource;
            });
            resource.SetUninstrumentedPeer(record.UninstrumentedPeer);
            resource.HasLogs = record.HasLogs;
            resource.HasTraces = record.HasTraces;
            resource.HasMetrics = record.HasMetrics;
            resources.Add(key, resource);
        }

        return resources.Values
            .Where(resource => includeUninstrumentedPeers || !resource.UninstrumentedPeer)
            .Where(resource => name is null || string.Equals(resource.ResourceName, name, StringComparisons.ResourceName))
            .OrderBy(resource => resource.ResourceKey)
            .ToList();
    }

    private List<OtlpResourceView> GetResourceViewsFromDatabase(ResourceKey resourceKey, OtlpResource resource)
    {
        using var connection = _database.OpenConnection();
        var viewIds = connection.Query<long>("""
            SELECT v.resource_view_id
            FROM telemetry_resource_views v
            JOIN telemetry_resources r ON r.resource_id = v.resource_id
            WHERE r.resource_name = @ResourceName COLLATE ORDINAL_IGNORE_CASE
              AND (@InstanceId IS NULL OR (r.instance_id_is_null = 0 AND r.instance_id = @InstanceId COLLATE ORDINAL_IGNORE_CASE))
            ORDER BY v.resource_view_id;
            """, new { ResourceName = resourceKey.Name, resourceKey.InstanceId }).AsList();
        var attributes = connection.Query<OwnedAttributeRecord>("""
            SELECT resource_view_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_resource_view_attributes
            WHERE resource_view_id IN @Ids
            ORDER BY resource_view_id, ordinal;
            """, new { Ids = viewIds }).ToLookup(attribute => attribute.OwnerId);
        return viewIds
            .Select(viewId => new OtlpResourceView(resource, attributes[viewId]
                .Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue))
                .ToArray()))
            .DistinctBy(view => view.Properties, ResourceViewPropertiesComparer.Instance)
            .ToList();
    }

    private sealed class ResourceViewPropertiesComparer : IEqualityComparer<KeyValuePair<string, string>[]>
    {
        public static readonly ResourceViewPropertiesComparer Instance = new();

        public bool Equals(KeyValuePair<string, string>[]? left, KeyValuePair<string, string>[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.SequenceEqual(right);

        public int GetHashCode(KeyValuePair<string, string>[] properties)
        {
            var hash = new HashCode();
            foreach (var property in properties)
            {
                hash.Add(property);
            }
            return hash.ToHashCode();
        }
    }

    private sealed record LogQuery(string FromAndWhere, DynamicParameters Parameters);

    private sealed class ScopeRecord
    {
        public required long ScopeId { get; init; }
        public required string ScopeName { get; init; }
        public required string ScopeVersion { get; init; }
    }

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
        public required string InstanceId { get; init; }
        public required bool InstanceIdIsNull { get; init; }
        public required bool UninstrumentedPeer { get; init; }
        public required bool HasLogs { get; init; }
        public required bool HasTraces { get; init; }
        public required bool HasMetrics { get; init; }
        public required string ScopeName { get; init; }
        public required string ScopeVersion { get; init; }
    }

    private sealed class FieldValueRecord
    {
        public string? FieldValue { get; init; }
        public required int ValueCount { get; init; }
    }

    private class TelemetryResourceRecord
    {
        public required string ResourceName { get; init; }
        public required string InstanceId { get; init; }
        public required bool InstanceIdIsNull { get; init; }
    }

    private sealed class TelemetryResourceStateRecord : TelemetryResourceRecord
    {
        public required bool UninstrumentedPeer { get; init; }
        public required bool HasLogs { get; init; }
        public required bool HasTraces { get; init; }
        public required bool HasMetrics { get; init; }
    }
}