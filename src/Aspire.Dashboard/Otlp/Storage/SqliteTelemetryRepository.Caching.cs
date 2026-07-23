// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Globalization;
using System.Text;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Dapper;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int MaxInstrumentBatchSize = 100;
    private const int MaxMetadataAttributeBatchSize = 200;

    private readonly object _cacheLock = new();
    private readonly Dictionary<ResourceKey, CachedResource> _cachedResourcesByKey = [];
    private readonly Dictionary<long, CachedResource> _cachedResourcesById = [];
    private readonly Dictionary<string, CachedScope> _cachedScopesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<long, CachedScope> _cachedScopesById = [];
    private readonly Dictionary<long, CachedInstrument> _cachedInstrumentsById = [];
    private bool _cachePopulated;

    private CachedResource GetOrAddCachedResource(
        SqliteConnection connection,
        IDbTransaction transaction,
        ResourceKey resourceKey,
        bool uninstrumentedPeer = false)
    {
        lock (_cacheLock)
        {
            if (_cachedResourcesByKey.TryGetValue(resourceKey, out var cachedResource))
            {
                if (cachedResource.Resource.UninstrumentedPeer && !uninstrumentedPeer)
                {
                    connection.Execute(
                        "UPDATE telemetry_resources SET uninstrumented_peer = 0 WHERE resource_id = @ResourceId;",
                        new { cachedResource.ResourceId },
                        transaction);
                }
                cachedResource.Resource.SetUninstrumentedPeer(uninstrumentedPeer);
                return cachedResource;
            }

            var record = connection.QuerySingleOrDefault<CachedResourceRecord>("""
                SELECT
                    resource_id AS ResourceId,
                    resource_name AS ResourceName,
                    instance_id AS InstanceId,
                    uninstrumented_peer AS UninstrumentedPeer,
                    has_logs AS HasLogs,
                    has_traces AS HasTraces,
                    has_metrics AS HasMetrics
                FROM telemetry_resources
                WHERE resource_name = @ResourceName
                  AND instance_id IS @InstanceId;
                """, new { ResourceName = resourceKey.Name, resourceKey.InstanceId }, transaction);
            if (record is not null)
            {
                cachedResource = GetOrAddCachedResource(record);
                if (cachedResource.Resource.UninstrumentedPeer && !uninstrumentedPeer)
                {
                    connection.Execute(
                        "UPDATE telemetry_resources SET uninstrumented_peer = 0 WHERE resource_id = @ResourceId;",
                        new { cachedResource.ResourceId },
                        transaction);
                }
                cachedResource.Resource.SetUninstrumentedPeer(uninstrumentedPeer);
                return cachedResource;
            }

            var resourceCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_resources;", transaction: transaction);
            if (resourceCount >= _otlpContext.Options.MaxResourceCount)
            {
                throw new InvalidOperationException($"Resource limit of {_otlpContext.Options.MaxResourceCount} reached. Resource '{resourceKey}' will not be added.");
            }

            var resourceId = connection.QuerySingle<long>("""
                INSERT INTO telemetry_resources (resource_name, instance_id)
                VALUES (@ResourceName, @InstanceId)
                RETURNING resource_id;
                """, new { ResourceName = resourceKey.Name, resourceKey.InstanceId }, transaction);
            return CreateCachedResource(resourceId, resourceKey, uninstrumentedPeer);
        }
    }

    private CachedResourceView GetOrAddCachedResourceView(
        SqliteConnection connection,
        IDbTransaction transaction,
        CachedResource resource,
        RepeatedField<KeyValue> attributes)
    {
        var incomingView = new OtlpResourceView(resource.Resource, attributes);
        lock (_cacheLock)
        {
            if (resource.ViewsByProperties.TryGetValue(incomingView.Properties, out var cachedView))
            {
                return cachedView;
            }

            var parameters = new DynamicParameters();
            parameters.Add("ResourceId", resource.ResourceId);
            parameters.Add("PropertyCount", incomingView.Properties.Length);
            var sql = new StringBuilder("""
                SELECT v.resource_view_id
                FROM telemetry_resource_views v
                WHERE v.resource_id = @ResourceId
                  AND (SELECT COUNT(*) FROM telemetry_resource_view_attributes a WHERE a.resource_view_id = v.resource_view_id) = @PropertyCount
                """);
            for (var index = 0; index < incomingView.Properties.Length; index++)
            {
                parameters.Add($"PropertyKey{index}", incomingView.Properties[index].Key);
                parameters.Add($"PropertyValue{index}", incomingView.Properties[index].Value);
                sql.Append(CultureInfo.InvariantCulture, $"""

                      AND EXISTS (
                          SELECT 1
                          FROM telemetry_resource_view_attributes a
                          WHERE a.resource_view_id = v.resource_view_id
                            AND a.ordinal = {index}
                            AND a.attribute_key = @PropertyKey{index}
                            AND a.attribute_value = @PropertyValue{index}
                      )
                    """);
            }
            sql.Append(" LIMIT 1;");

            var resourceViewId = connection.QuerySingleOrDefault<long?>(sql.ToString(), parameters, transaction);
            if (resourceViewId is null)
            {
                var resourceViewCount = connection.QuerySingle<int>(
                    "SELECT COUNT(*) FROM telemetry_resource_views WHERE resource_id = @ResourceId;",
                    new { resource.ResourceId },
                    transaction);
                if (resourceViewCount >= TelemetryRepositoryLimits.MaxResourceViewCount)
                {
                    throw new InvalidOperationException($"Resource view limit of {TelemetryRepositoryLimits.MaxResourceViewCount} reached.");
                }

                resourceViewId = connection.QuerySingle<long>("""
                    INSERT INTO telemetry_resource_views (resource_id)
                    VALUES (@ResourceId)
                    RETURNING resource_view_id;
                    """, new { resource.ResourceId }, transaction);
                var properties = incomingView.Properties
                    .Select((property, ordinal) => (Ordinal: ordinal, property.Key, property.Value))
                    .ToArray();
                SqliteBatchInsert.BatchInsertRows(
                    connection,
                    transaction,
                    properties,
                    MaxMetadataAttributeBatchSize,
                    "telemetry_resource_view_attributes",
                    ["resource_view_id", "ordinal", "attribute_key", "attribute_value"],
                    (property, parameters) =>
                    {
                        parameters[0].Value = resourceViewId.Value;
                        parameters[1].Value = property.Ordinal;
                        parameters[2].Value = property.Key;
                        parameters[3].Value = property.Value;
                    });
            }

            return AddCachedResourceView(resource, resourceViewId.Value, incomingView.Properties);
        }
    }

    private CachedResourceScope GetOrAddCachedScope(
        SqliteConnection connection,
        IDbTransaction transaction,
        CachedResource resource,
        InstrumentationScope? instrumentationScope,
        CachedTelemetryType telemetryType)
    {
        var incomingScope = instrumentationScope is null
            ? OtlpScope.Empty
            : new OtlpScope(
                instrumentationScope.Name,
                instrumentationScope.Version,
                instrumentationScope.Attributes.ToKeyValuePairs(_otlpContext));
        lock (_cacheLock)
        {
            if (resource.Scopes.TryGetValue(incomingScope.Name, out var resourceScope))
            {
                resourceScope.TelemetryTypes |= telemetryType;
                return resourceScope;
            }

            if (!_cachedScopesByName.TryGetValue(incomingScope.Name, out var cachedScope))
            {
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
                    cachedScope = GetOrAddCachedScope(existing.ScopeId, existing.ScopeName, existing.ScopeVersion, attributes);
                }
                else
                {
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
                    var attributes = incomingScope.Attributes
                        .Select((attribute, ordinal) => (Ordinal: ordinal, attribute.Key, attribute.Value))
                        .ToArray();
                    SqliteBatchInsert.BatchInsertRows(
                        connection,
                        transaction,
                        attributes,
                        MaxMetadataAttributeBatchSize,
                        "telemetry_scope_attributes",
                        ["scope_id", "ordinal", "attribute_key", "attribute_value"],
                        (attribute, parameters) =>
                        {
                            parameters[0].Value = scopeId;
                            parameters[1].Value = attribute.Ordinal;
                            parameters[2].Value = attribute.Key;
                            parameters[3].Value = attribute.Value;
                        });
                    cachedScope = GetOrAddCachedScope(scopeId, incomingScope.Name, incomingScope.Version, incomingScope.Attributes);
                }
            }

            return AddCachedScope(resource, cachedScope, telemetryType);
        }
    }

    private CachedInstrument GetOrAddCachedInstrument(
        SqliteConnection connection,
        IDbTransaction transaction,
        CachedResource resource,
        CachedResourceScope resourceScope,
        Metric metric)
    {
        if (string.IsNullOrEmpty(metric.Name))
        {
            throw new InvalidOperationException("Instrument name is required.");
        }

        lock (_cacheLock)
        {
            if (resourceScope.Instruments.TryGetValue(metric.Name, out var instrument))
            {
                return instrument;
            }

            EnsureCachedInstrumentsLoaded(connection, transaction, resource, resourceScope);
            if (resourceScope.Instruments.TryGetValue(metric.Name, out instrument))
            {
                return instrument;
            }

            var instrumentCount = resource.InstrumentCount ??= connection.QuerySingle<int>(
                    "SELECT COUNT(*) FROM telemetry_metric_instruments WHERE resource_id = @ResourceId;",
                    new { resource.ResourceId },
                    transaction);
            if (instrumentCount >= TelemetryRepositoryLimits.MaxInstrumentCount)
            {
                throw new InvalidOperationException($"Instrument limit of {TelemetryRepositoryLimits.MaxInstrumentCount} reached. Instrument '{metric.Name}' will not be added.");
            }

            var instrumentId = connection.QuerySingle<long>("""
                INSERT INTO telemetry_metric_instruments (
                    resource_id, scope_id, instrument_name, description, unit, instrument_type,
                    aggregation_temporality, is_monotonic)
                VALUES (
                    @ResourceId, @ScopeId, @InstrumentName, @Description, @Unit, @InstrumentType,
                    @AggregationTemporality, @IsMonotonic)
                RETURNING instrument_id;
                """, new
            {
                resource.ResourceId,
                ScopeId = resourceScope.Scope.ScopeId,
                InstrumentName = metric.Name,
                metric.Description,
                metric.Unit,
                InstrumentType = (int)MapMetricType(metric.DataCase),
                AggregationTemporality = (int)MapAggregationTemporality(metric),
                IsMonotonic = metric.DataCase == Metric.DataOneofCase.Sum && metric.Sum.IsMonotonic
            }, transaction);
            var record = new CachedInstrumentRecord
            {
                InstrumentId = instrumentId,
                ResourceId = resource.ResourceId,
                ScopeId = resourceScope.Scope.ScopeId,
                InstrumentName = metric.Name,
                Description = metric.Description,
                Unit = metric.Unit,
                InstrumentType = (int)MapMetricType(metric.DataCase),
                AggregationTemporality = (int)MapAggregationTemporality(metric),
                HasOverflow = false
            };
            resource.InstrumentCount++;
            _metricIngestionState.LoadedDimensionInstruments.Add(instrumentId);
            _metricIngestionState.DimensionCounts[instrumentId] = 0;
            return AddCachedInstrument(resourceScope, record);
        }
    }

    private void EnsureCachedInstruments(
        SqliteConnection connection,
        IDbTransaction transaction,
        CachedResource resource,
        CachedResourceScope resourceScope,
        RepeatedField<Metric> metrics)
    {
        lock (_cacheLock)
        {
            EnsureCachedInstrumentsLoaded(connection, transaction, resource, resourceScope);

            var pendingMetrics = metrics
                .Where(metric => !string.IsNullOrEmpty(metric.Name) && metric.DataCase is
                    Metric.DataOneofCase.Gauge or Metric.DataOneofCase.Sum or Metric.DataOneofCase.Histogram)
                .DistinctBy(metric => metric.Name, StringComparer.Ordinal)
                .Where(metric => !resourceScope.Instruments.ContainsKey(metric.Name))
                .ToList();
            if (pendingMetrics.Count == 0)
            {
                return;
            }

            var instrumentCount = resource.InstrumentCount ??= connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM telemetry_metric_instruments WHERE resource_id = @ResourceId;",
                new { resource.ResourceId },
                transaction);
            var availableCount = Math.Max(0, TelemetryRepositoryLimits.MaxInstrumentCount - instrumentCount);
            foreach (var batch in pendingMetrics.Take(availableCount).Chunk(MaxInstrumentBatchSize))
            {
                var sql = new StringBuilder("""
                    INSERT INTO telemetry_metric_instruments (
                        resource_id, scope_id, instrument_name, description, unit, instrument_type,
                        aggregation_temporality, is_monotonic)
                    VALUES
                    """);
                var parameters = new DynamicParameters();
                parameters.Add("ResourceId", resource.ResourceId);
                parameters.Add("ScopeId", resourceScope.Scope.ScopeId);
                for (var index = 0; index < batch.Length; index++)
                {
                    if (index > 0)
                    {
                        sql.AppendLine(",");
                    }
                    sql.Append(CultureInfo.InvariantCulture, $"    (@ResourceId, @ScopeId, @InstrumentName{index}, @Description{index}, @Unit{index}, @InstrumentType{index}, @AggregationTemporality{index}, @IsMonotonic{index})");
                    parameters.Add($"InstrumentName{index}", batch[index].Name);
                    parameters.Add($"Description{index}", batch[index].Description);
                    parameters.Add($"Unit{index}", batch[index].Unit);
                    parameters.Add($"InstrumentType{index}", (int)MapMetricType(batch[index].DataCase));
                    parameters.Add($"AggregationTemporality{index}", (int)MapAggregationTemporality(batch[index]));
                    parameters.Add($"IsMonotonic{index}", batch[index].DataCase == Metric.DataOneofCase.Sum && batch[index].Sum.IsMonotonic);
                }
                sql.Append("""

                    RETURNING instrument_id AS InstrumentId, instrument_name AS InstrumentName;
                    """);

                var metricsByName = batch.ToDictionary(metric => metric.Name, StringComparer.Ordinal);
                var insertedRecords = connection.Query<InsertedInstrumentRecord>(sql.ToString(), parameters, transaction).ToList();

                foreach (var insertedRecord in insertedRecords)
                {
                    var metric = metricsByName[insertedRecord.InstrumentName];
                    AddCachedInstrument(resourceScope, new CachedInstrumentRecord
                    {
                        InstrumentId = insertedRecord.InstrumentId,
                        ResourceId = resource.ResourceId,
                        ScopeId = resourceScope.Scope.ScopeId,
                        InstrumentName = metric.Name,
                        Description = metric.Description,
                        Unit = metric.Unit,
                        InstrumentType = (int)MapMetricType(metric.DataCase),
                        AggregationTemporality = (int)MapAggregationTemporality(metric),
                        HasOverflow = false
                    });
                    _metricIngestionState.LoadedDimensionInstruments.Add(insertedRecord.InstrumentId);
                    _metricIngestionState.DimensionCounts[insertedRecord.InstrumentId] = 0;
                }
                resource.InstrumentCount += insertedRecords.Count;
            }
        }
    }

    private void EnsureCachedInstrumentsLoaded(
        SqliteConnection connection,
        IDbTransaction transaction,
        CachedResource resource,
        CachedResourceScope resourceScope)
    {
        if (resourceScope.InstrumentsLoaded)
        {
            return;
        }

        var records = connection.Query<CachedInstrumentRecord>("""
            SELECT instrument_id AS InstrumentId,
                resource_id AS ResourceId,
                scope_id AS ScopeId,
                instrument_name AS InstrumentName,
                description AS Description,
                unit AS Unit,
                instrument_type AS InstrumentType,
                aggregation_temporality AS AggregationTemporality,
                has_overflow AS HasOverflow
            FROM telemetry_metric_instruments
            WHERE resource_id = @ResourceId AND scope_id = @ScopeId;
            """, new
        {
            resource.ResourceId,
            ScopeId = resourceScope.Scope.ScopeId
        }, transaction);
        foreach (var loadedRecord in records)
        {
            AddCachedInstrument(resourceScope, loadedRecord);
        }
        resourceScope.InstrumentsLoaded = true;
    }

    private void ClearIngestionCaches()
    {
        ClearMetadataCache();
        _metricIngestionState.Clear();
    }

    private void EnsureCachePopulated()
    {
        lock (_writeLock)
        {
            lock (_cacheLock)
            {
                if (_cachePopulated)
                {
                    return;
                }

                using var connection = _database.OpenConnection();
                using var reader = connection.QueryMultiple("""
                SELECT
                    resource_id AS ResourceId,
                    resource_name AS ResourceName,
                    instance_id AS InstanceId,
                    uninstrumented_peer AS UninstrumentedPeer,
                    has_logs AS HasLogs,
                    has_traces AS HasTraces,
                    has_metrics AS HasMetrics
                FROM telemetry_resources;

                SELECT
                    v.resource_view_id AS ResourceViewId,
                    v.resource_id AS ResourceId,
                    a.attribute_key AS AttributeKey,
                    a.attribute_value AS AttributeValue
                FROM telemetry_resource_views v
                LEFT JOIN telemetry_resource_view_attributes a ON a.resource_view_id = v.resource_view_id
                ORDER BY v.resource_view_id, a.ordinal;

                SELECT DISTINCT
                    u.resource_id AS ResourceId,
                    u.scope_id AS ScopeId,
                    u.telemetry_type AS TelemetryType,
                    s.scope_name AS ScopeName,
                    s.scope_version AS ScopeVersion
                FROM (
                    SELECT resource_id, scope_id, 1 AS telemetry_type FROM telemetry_logs
                    UNION ALL
                    SELECT resource_id, scope_id, 2 AS telemetry_type FROM telemetry_spans
                    UNION ALL
                    SELECT resource_id, scope_id, 4 AS telemetry_type FROM telemetry_metric_instruments
                ) u
                JOIN telemetry_scopes s ON s.scope_id = u.scope_id;

                SELECT
                    scope_id AS ScopeId,
                    attribute_key AS AttributeKey,
                    attribute_value AS AttributeValue
                FROM telemetry_scope_attributes
                ORDER BY scope_id, ordinal;

                SELECT
                    instrument_id AS InstrumentId,
                    resource_id AS ResourceId,
                    scope_id AS ScopeId,
                    instrument_name AS InstrumentName,
                    description AS Description,
                    unit AS Unit,
                    instrument_type AS InstrumentType,
                    aggregation_temporality AS AggregationTemporality,
                    has_overflow AS HasOverflow
                FROM telemetry_metric_instruments
                ORDER BY instrument_id;
                """);

                var resources = reader.Read<CachedResourceRecord>().AsList();
                var resourceViews = reader.Read<CachedResourceViewRecord>().AsList();
                var scopeUsages = reader.Read<CachedScopeUsageRecord>().AsList();
                var scopeAttributes = reader.Read<CachedScopeAttributeRecord>().ToLookup(record => record.ScopeId);
                var instruments = reader.Read<CachedInstrumentRecord>().AsList();

                foreach (var record in resources)
                {
                    GetOrAddCachedResource(record);
                }

                foreach (var group in resourceViews.GroupBy(record => (record.ResourceId, record.ResourceViewId)))
                {
                    if (_cachedResourcesById.TryGetValue(group.Key.ResourceId, out var resource))
                    {
                        var properties = group
                            .Where(record => record.AttributeKey is not null)
                            .Select(record => KeyValuePair.Create(record.AttributeKey!, record.AttributeValue!))
                            .ToArray();
                        AddCachedResourceView(resource, group.Key.ResourceViewId, properties);
                    }
                }

                foreach (var record in scopeUsages)
                {
                    if (_cachedResourcesById.TryGetValue(record.ResourceId, out var resource))
                    {
                        var scope = GetOrAddCachedScope(
                            record.ScopeId,
                            record.ScopeName,
                            record.ScopeVersion,
                            scopeAttributes[record.ScopeId]
                                .Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue))
                                .ToArray());
                        AddCachedScope(resource, scope, (CachedTelemetryType)record.TelemetryType);
                    }
                }

                foreach (var record in instruments)
                {
                    if (_cachedResourcesById.TryGetValue(record.ResourceId, out var resource) &&
                        _cachedScopesById.TryGetValue(record.ScopeId, out var scope))
                    {
                        var resourceScope = AddCachedScope(resource, scope, CachedTelemetryType.Metrics);
                        AddCachedInstrument(resourceScope, record);
                    }
                }

                foreach (var resource in _cachedResourcesById.Values)
                {
                    resource.InstrumentCount = resource.Scopes.Values.Sum(scope => scope.Instruments.Count);
                    foreach (var resourceScope in resource.Scopes.Values.Where(scope => scope.TelemetryTypes.HasFlag(CachedTelemetryType.Metrics)))
                    {
                        resourceScope.InstrumentsLoaded = true;
                    }
                }

                _cachePopulated = true;
            }
        }
    }

    private CachedResource GetOrAddCachedResource(CachedResourceRecord record)
    {
        var key = new ResourceKey(record.ResourceName, record.InstanceId);
        if (!_cachedResourcesByKey.TryGetValue(key, out var cachedResource))
        {
            cachedResource = CreateCachedResource(record.ResourceId, key, record.UninstrumentedPeer);
        }

        cachedResource.Resource.SetUninstrumentedPeer(record.UninstrumentedPeer);
        cachedResource.Resource.HasLogs = record.HasLogs;
        cachedResource.Resource.HasTraces = record.HasTraces;
        cachedResource.Resource.HasMetrics = record.HasMetrics;
        return cachedResource;
    }

    private CachedResource CreateCachedResource(long resourceId, ResourceKey key, bool uninstrumentedPeer)
    {
        var resource = new OtlpResource(key.Name, key.InstanceId, uninstrumentedPeer, _otlpContext);
        var cachedResource = new CachedResource(resourceId, resource);
        _cachedResourcesByKey.Add(key, cachedResource);
        _cachedResourcesById.Add(resourceId, cachedResource);
        return cachedResource;
    }

    private static CachedResourceView AddCachedResourceView(CachedResource resource, long resourceViewId, KeyValuePair<string, string>[] properties)
    {
        if (resource.ViewsById.TryGetValue(resourceViewId, out var cachedView))
        {
            return cachedView;
        }

        var view = resource.Resource.GetViewFromProperties(properties);
        cachedView = new CachedResourceView(resourceViewId, view);
        resource.ViewsById.Add(resourceViewId, cachedView);
        resource.ViewsByProperties[view.Properties] = cachedView;
        return cachedView;
    }

    private CachedScope GetOrAddCachedScope(long scopeId, string scopeName, string scopeVersion, KeyValuePair<string, string>[] attributes)
    {
        if (_cachedScopesById.TryGetValue(scopeId, out var cachedScope))
        {
            return cachedScope;
        }

        var scope = CreateScope(scopeName, scopeVersion, attributes);
        cachedScope = new CachedScope(scopeId, scope);
        _cachedScopesById.Add(scopeId, cachedScope);
        _cachedScopesByName[scope.Name] = cachedScope;
        return cachedScope;
    }

    private static CachedResourceScope AddCachedScope(CachedResource resource, CachedScope scope, CachedTelemetryType telemetryType)
    {
        if (!resource.Scopes.TryGetValue(scope.Scope.Name, out var resourceScope))
        {
            resourceScope = new CachedResourceScope(scope);
            resource.Scopes.Add(scope.Scope.Name, resourceScope);
        }
        resourceScope.TelemetryTypes |= telemetryType;
        return resourceScope;
    }

    private CachedInstrument AddCachedInstrument(CachedResourceScope resourceScope, CachedInstrumentRecord record)
    {
        if (!resourceScope.Instruments.TryGetValue(record.InstrumentName, out var instrument))
        {
            instrument = new CachedInstrument(
                record.InstrumentId,
                new OtlpInstrumentSummary
                {
                    Name = record.InstrumentName,
                    Description = record.Description,
                    Unit = record.Unit,
                    Type = (OtlpInstrumentType)record.InstrumentType,
                    AggregationTemporality = (OtlpAggregationTemporality)record.AggregationTemporality,
                    Parent = resourceScope.Scope.Scope
                },
                record.HasOverflow);
            resourceScope.Instruments.Add(record.InstrumentName, instrument);
            _cachedInstrumentsById.Add(record.InstrumentId, instrument);
        }
        return instrument;
    }

    private List<OtlpInstrumentSummary> GetCachedInstrumentSummaries(ResourceKey key)
    {
        EnsureCachePopulated();
        lock (_cacheLock)
        {
            return GetCachedResources(key)
                .SelectMany(resource => resource.Scopes.Values)
                .SelectMany(scope => scope.Instruments.Values)
                .Select(instrument => instrument.Summary)
                .DistinctBy(summary => summary.GetKey())
                .ToList();
        }
    }

    private List<CachedInstrument> GetCachedInstruments(ResourceKey resourceKey, string meterName, string instrumentName)
    {
        EnsureCachePopulated();
        lock (_cacheLock)
        {
            return GetCachedResources(resourceKey)
                .SelectMany(resource => resource.Scopes.Values)
                .Where(scope => string.Equals(scope.Scope.Scope.Name, meterName, StringComparison.Ordinal))
                .SelectMany(scope => scope.Instruments.Values)
                .Where(instrument => string.Equals(instrument.Summary.Name, instrumentName, StringComparison.Ordinal))
                .ToList();
        }
    }

    private void MarkCachedInstrumentHasOverflow(long instrumentId)
    {
        lock (_cacheLock)
        {
            if (_cachedInstrumentsById.TryGetValue(instrumentId, out var instrument))
            {
                instrument.HasOverflow = true;
            }
        }
    }

    private (OtlpResource Resource, OtlpResourceView View, OtlpScope Scope) GetCachedTelemetryMetadata(
        long resourceId,
        long resourceViewId,
        long scopeId,
        CachedTelemetryType telemetryType)
    {
        EnsureCachePopulated();
        lock (_cacheLock)
        {
            var resource = _cachedResourcesById[resourceId];
            var view = resource.ViewsById[resourceViewId];
            var scope = _cachedScopesById[scopeId];
            AddCachedScope(resource, scope, telemetryType);
            return (resource.Resource, view.View, scope.Scope);
        }
    }

    private OtlpResource? GetCachedResource(long resourceId)
    {
        EnsureCachePopulated();
        lock (_cacheLock)
        {
            return _cachedResourcesById.TryGetValue(resourceId, out var resource) ? resource.Resource : null;
        }
    }

    private IEnumerable<CachedResource> GetCachedResources(ResourceKey key)
    {
        return key.InstanceId is null
            ? _cachedResourcesByKey.Values.Where(resource => string.Equals(resource.Resource.ResourceName, key.Name, StringComparisons.ResourceName))
            : _cachedResourcesByKey.TryGetValue(key, out var resource) ? [resource] : [];
    }

    private void ClearMetadataCache()
    {
        lock (_cacheLock)
        {
            _cachedResourcesByKey.Clear();
            _cachedResourcesById.Clear();
            _cachedScopesByName.Clear();
            _cachedScopesById.Clear();
            _cachedInstrumentsById.Clear();
            _cachePopulated = false;
        }
    }

    private sealed class CachedResource(long resourceId, OtlpResource resource)
    {
        public long ResourceId { get; } = resourceId;
        public OtlpResource Resource { get; } = resource;
        public Dictionary<long, CachedResourceView> ViewsById { get; } = [];
        public Dictionary<KeyValuePair<string, string>[], CachedResourceView> ViewsByProperties { get; } = new(ResourceViewPropertiesComparer.Instance);
        public Dictionary<string, CachedResourceScope> Scopes { get; } = new(StringComparer.Ordinal);
        public int? InstrumentCount { get; set; }
    }

    private sealed record CachedResourceView(long ResourceViewId, OtlpResourceView View);
    private sealed record CachedScope(long ScopeId, OtlpScope Scope);

    private sealed class CachedResourceScope(CachedScope scope)
    {
        public CachedScope Scope { get; } = scope;
        public CachedTelemetryType TelemetryTypes { get; set; }
        public Dictionary<string, CachedInstrument> Instruments { get; } = new(StringComparer.Ordinal);
        public bool InstrumentsLoaded { get; set; }
    }

    private sealed class CachedInstrument(long instrumentId, OtlpInstrumentSummary summary, bool hasOverflow)
    {
        public long InstrumentId { get; } = instrumentId;
        public OtlpInstrumentSummary Summary { get; } = summary;
        public bool HasOverflow { get; set; } = hasOverflow;
    }

    [Flags]
    private enum CachedTelemetryType
    {
        Logs = 1,
        Traces = 2,
        Metrics = 4
    }

    private sealed class CachedResourceRecord
    {
        public required long ResourceId { get; init; }
        public required string ResourceName { get; init; }
        public string? InstanceId { get; init; }
        public required bool UninstrumentedPeer { get; init; }
        public required bool HasLogs { get; init; }
        public required bool HasTraces { get; init; }
        public required bool HasMetrics { get; init; }
    }

    private sealed class CachedResourceViewRecord
    {
        public required long ResourceViewId { get; init; }
        public required long ResourceId { get; init; }
        public string? AttributeKey { get; init; }
        public string? AttributeValue { get; init; }
    }

    private sealed class CachedScopeUsageRecord
    {
        public required long ResourceId { get; init; }
        public required long ScopeId { get; init; }
        public required int TelemetryType { get; init; }
        public required string ScopeName { get; init; }
        public required string ScopeVersion { get; init; }
    }

    private sealed class CachedScopeAttributeRecord
    {
        public required long ScopeId { get; init; }
        public required string AttributeKey { get; init; }
        public required string AttributeValue { get; init; }
    }

    private sealed class CachedInstrumentRecord
    {
        public required long InstrumentId { get; init; }
        public required long ResourceId { get; init; }
        public required long ScopeId { get; init; }
        public required string InstrumentName { get; init; }
        public required string Description { get; init; }
        public required string Unit { get; init; }
        public required int InstrumentType { get; init; }
        public required int AggregationTemporality { get; init; }
        public required bool HasOverflow { get; init; }
    }

    private sealed class InsertedInstrumentRecord
    {
        public required long InstrumentId { get; init; }
        public required string InstrumentName { get; init; }
    }
}
