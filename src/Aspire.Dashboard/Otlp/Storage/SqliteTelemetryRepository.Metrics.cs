// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Dapper;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int LongPointType = 1;
    private const int DoublePointType = 2;
    private const int HistogramPointType = 3;

    private void AddMetricsToDatabase(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        lock (_writeLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var resourceMetricsItem in resourceMetrics)
            {
                long resourceId;
                try
                {
                    resourceId = GetOrAddTelemetryResource(connection, transaction, resourceMetricsItem.Resource.GetResourceKey());
                }
                catch (Exception exception)
                {
                    context.FailureCount += resourceMetricsItem.ScopeMetrics.Sum(scope => scope.Metrics.Sum(OtlpResource.GetMetricDataPointCount));
                    _otlpContext.Logger.LogInformation(exception, "Error adding resource.");
                    continue;
                }

                foreach (var scopeMetrics in resourceMetricsItem.ScopeMetrics)
                {
                    OtlpScope scope;
                    long scopeId;
                    try
                    {
                        (scopeId, scope) = GetOrAddScope(connection, transaction, scopeMetrics.Scope);
                    }
                    catch (Exception exception)
                    {
                        context.FailureCount += scopeMetrics.Metrics.Sum(OtlpResource.GetMetricDataPointCount);
                        _otlpContext.Logger.LogInformation(exception, "Error adding metric scope.");
                        continue;
                    }

                    foreach (var metric in scopeMetrics.Metrics)
                    {
                        AddMetricToDatabase(connection, transaction, context, resourceId, scopeId, scope, metric);
                    }
                }

                connection.Execute(
                    "UPDATE telemetry_resources SET has_metrics = 1 WHERE resource_id = @ResourceId;",
                    new { ResourceId = resourceId },
                    transaction);
            }
            transaction.Commit();
        }
    }

    private void AddMetricToDatabase(
        SqliteConnection connection,
        IDbTransaction transaction,
        AddContext context,
        long resourceId,
        long scopeId,
        OtlpScope scope,
        Metric metric)
    {
        var pointCount = OtlpResource.GetMetricDataPointCount(metric);
        if (metric.DataCase is Metric.DataOneofCase.Summary or Metric.DataOneofCase.ExponentialHistogram)
        {
            context.FailureCount += pointCount;
            _otlpContext.Logger.LogInformation("Error adding {MetricType} metrics. {MetricType} is not supported.", metric.DataCase, metric.DataCase);
            return;
        }
        if (metric.DataCase is Metric.DataOneofCase.None)
        {
            return;
        }

        long instrumentId;
        try
        {
            instrumentId = GetOrAddMetricInstrument(connection, transaction, resourceId, scopeId, metric);
        }
        catch (Exception exception)
        {
            context.FailureCount += pointCount;
            _otlpContext.Logger.LogInformation(exception, "Error adding metric instrument {MetricName}.", metric.Name);
            return;
        }

        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge:
                foreach (var point in metric.Gauge.DataPoints)
                {
                    AddNumberMetricPoint(connection, transaction, context, instrumentId, scope, point);
                }
                break;
            case Metric.DataOneofCase.Sum:
                foreach (var point in metric.Sum.DataPoints)
                {
                    AddNumberMetricPoint(connection, transaction, context, instrumentId, scope, point);
                }
                break;
            case Metric.DataOneofCase.Histogram:
                foreach (var point in metric.Histogram.DataPoints)
                {
                    AddHistogramMetricPoint(connection, transaction, context, instrumentId, scope, point);
                }
                break;
        }
    }

    private static long GetOrAddMetricInstrument(
        SqliteConnection connection,
        IDbTransaction transaction,
        long resourceId,
        long scopeId,
        Metric metric)
    {
        if (string.IsNullOrEmpty(metric.Name))
        {
            throw new InvalidOperationException("Instrument name is required.");
        }
        var existingId = connection.QuerySingleOrDefault<long?>("""
            SELECT instrument_id
            FROM telemetry_metric_instruments
            WHERE resource_id = @ResourceId AND scope_id = @ScopeId AND instrument_name = @InstrumentName;
            """, new { ResourceId = resourceId, ScopeId = scopeId, InstrumentName = metric.Name }, transaction);
        if (existingId is not null)
        {
            return existingId.Value;
        }

        var instrumentCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_metric_instruments WHERE resource_id = @ResourceId;", new { ResourceId = resourceId }, transaction);
        if (instrumentCount >= TelemetryRepositoryLimits.MaxInstrumentCount)
        {
            throw new InvalidOperationException($"Instrument limit of {TelemetryRepositoryLimits.MaxInstrumentCount} reached. Instrument '{metric.Name}' will not be added.");
        }

        return connection.QuerySingle<long>("""
            INSERT INTO telemetry_metric_instruments (
                resource_id, scope_id, instrument_name, description, unit, instrument_type,
                aggregation_temporality, is_monotonic)
            VALUES (
                @ResourceId, @ScopeId, @InstrumentName, @Description, @Unit, @InstrumentType,
                @AggregationTemporality, @IsMonotonic)
            RETURNING instrument_id;
            """, new
        {
            ResourceId = resourceId,
            ScopeId = scopeId,
            InstrumentName = metric.Name,
            metric.Description,
            metric.Unit,
            InstrumentType = (int)MapMetricType(metric.DataCase),
            AggregationTemporality = (int)MapAggregationTemporality(metric),
            IsMonotonic = metric.DataCase == Metric.DataOneofCase.Sum && metric.Sum.IsMonotonic
        }, transaction);
    }

    private void AddNumberMetricPoint(
        SqliteConnection connection,
        IDbTransaction transaction,
        AddContext context,
        long instrumentId,
        OtlpScope scope,
        NumberDataPoint point)
    {
        try
        {
            var dimensionId = GetOrAddMetricDimension(connection, transaction, instrumentId, scope, point.Attributes);
            var pointType = point.ValueCase switch
            {
                NumberDataPoint.ValueOneofCase.AsInt => LongPointType,
                NumberDataPoint.ValueOneofCase.AsDouble => DoublePointType,
                _ => throw new InvalidOperationException("Metric data point has no value.")
            };
            var latest = GetLatestMetricPoint(connection, transaction, dimensionId);
            var sameValue = latest is not null && latest.PointType == pointType &&
                (pointType == LongPointType ? latest.IntegerValue == point.AsInt : latest.DoubleValue == point.AsDouble);
            long pointId;
            if (sameValue)
            {
                pointId = latest!.PointId;
                connection.Execute("""
                    UPDATE telemetry_metric_points
                    SET end_time_ticks = @EndTimeTicks, repeat_count = repeat_count + 1
                    WHERE point_id = @PointId;
                    """, new { PointId = pointId, EndTimeTicks = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano).Ticks }, transaction);
            }
            else
            {
                var start = OtlpHelpers.UnixNanoSecondsToDateTime(point.StartTimeUnixNano);
                if (latest?.PointType == pointType)
                {
                    start = new DateTime(latest.EndTimeTicks, DateTimeKind.Utc);
                }
                pointId = connection.QuerySingle<long>("""
                    INSERT INTO telemetry_metric_points (
                        dimension_id, point_type, start_time_ticks, end_time_ticks, repeat_count,
                        integer_value, double_value, flags)
                    VALUES (
                        @DimensionId, @PointType, @StartTimeTicks, @EndTimeTicks, 1,
                        @IntegerValue, @DoubleValue, @Flags)
                    RETURNING point_id;
                    """, new
                {
                    DimensionId = dimensionId,
                    PointType = pointType,
                    StartTimeTicks = start.Ticks,
                    EndTimeTicks = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano).Ticks,
                    IntegerValue = pointType == LongPointType ? point.AsInt : (long?)null,
                    DoubleValue = pointType == DoublePointType ? point.AsDouble : (double?)null,
                    Flags = (long)point.Flags
                }, transaction);
            }
            AddMetricExemplars(connection, transaction, pointId, point.Exemplars);
            TrimMetricDimension(connection, transaction, dimensionId);
            context.SuccessCount++;
        }
        catch (Exception exception)
        {
            context.FailureCount++;
            _otlpContext.Logger.LogInformation(exception, "Error adding metric.");
        }
    }

    private void AddHistogramMetricPoint(
        SqliteConnection connection,
        IDbTransaction transaction,
        AddContext context,
        long instrumentId,
        OtlpScope scope,
        HistogramDataPoint point)
    {
        try
        {
            if (point.BucketCounts.Count > 0 && point.ExplicitBounds.Count == 0)
            {
                throw new InvalidOperationException("Histogram data point has bucket counts without any explicit bounds.");
            }
            var dimensionId = GetOrAddMetricDimension(connection, transaction, instrumentId, scope, point.Attributes);
            var latest = GetLatestMetricPoint(connection, transaction, dimensionId);
            var sameCount = latest is not null && latest.PointType == HistogramPointType && latest.HistogramCount == point.Count.ToString(CultureInfo.InvariantCulture);
            long pointId;
            if (sameCount)
            {
                pointId = latest!.PointId;
                connection.Execute(
                    "UPDATE telemetry_metric_points SET end_time_ticks = @EndTimeTicks WHERE point_id = @PointId;",
                    new { PointId = pointId, EndTimeTicks = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano).Ticks },
                    transaction);
            }
            else
            {
                var start = OtlpHelpers.UnixNanoSecondsToDateTime(point.StartTimeUnixNano);
                if (latest?.PointType == HistogramPointType)
                {
                    start = new DateTime(latest.EndTimeTicks, DateTimeKind.Utc);
                }
                pointId = connection.QuerySingle<long>("""
                    INSERT INTO telemetry_metric_points (
                        dimension_id, point_type, start_time_ticks, end_time_ticks, repeat_count,
                        histogram_sum, histogram_count, flags)
                    VALUES (
                        @DimensionId, @PointType, @StartTimeTicks, @EndTimeTicks, 1,
                        @HistogramSum, @HistogramCount, @Flags)
                    RETURNING point_id;
                    """, new
                {
                    DimensionId = dimensionId,
                    PointType = HistogramPointType,
                    StartTimeTicks = start.Ticks,
                    EndTimeTicks = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano).Ticks,
                    HistogramSum = point.Sum,
                    HistogramCount = point.Count.ToString(CultureInfo.InvariantCulture),
                    Flags = (long)point.Flags
                }, transaction);
                connection.Execute("""
                    INSERT INTO telemetry_metric_histogram_bucket_counts (point_id, ordinal, bucket_count)
                    VALUES (@PointId, @Ordinal, @BucketCount);
                    """, point.BucketCounts.Select((count, ordinal) => new { PointId = pointId, Ordinal = ordinal, BucketCount = count.ToString(CultureInfo.InvariantCulture) }), transaction);
                connection.Execute("""
                    INSERT INTO telemetry_metric_histogram_explicit_bounds (point_id, ordinal, explicit_bound)
                    VALUES (@PointId, @Ordinal, @ExplicitBound);
                    """, point.ExplicitBounds.Select((bound, ordinal) => new { PointId = pointId, Ordinal = ordinal, ExplicitBound = bound }), transaction);
            }
            AddMetricExemplars(connection, transaction, pointId, point.Exemplars);
            TrimMetricDimension(connection, transaction, dimensionId);
            context.SuccessCount++;
        }
        catch (Exception exception)
        {
            context.FailureCount++;
            _otlpContext.Logger.LogInformation(exception, "Error adding metric.");
        }
    }

    private long GetOrAddMetricDimension(
        SqliteConnection connection,
        IDbTransaction transaction,
        long instrumentId,
        OtlpScope scope,
        RepeatedField<KeyValue> pointAttributes)
    {
        KeyValuePair<string, string>[]? temporaryAttributes = null;
        OtlpHelpers.CopyKeyValuePairs(pointAttributes, scope.Attributes, _otlpContext, out var copyCount, ref temporaryAttributes);
        Array.Sort(temporaryAttributes, 0, copyCount, MetricAttributeComparer.Instance);
        var attributes = temporaryAttributes.AsSpan(0, copyCount).ToArray();
        var existingDimensionIds = connection.Query<long>("SELECT dimension_id FROM telemetry_metric_dimensions WHERE instrument_id = @InstrumentId ORDER BY dimension_id;", new { InstrumentId = instrumentId }, transaction).AsList();
        var existingAttributesByDimension = connection.Query<OwnedAttributeRecord>("""
            SELECT a.dimension_id AS OwnerId, a.attribute_key AS AttributeKey, a.attribute_value AS AttributeValue
            FROM telemetry_metric_dimension_attributes a
            JOIN telemetry_metric_dimensions d ON d.dimension_id = a.dimension_id
            WHERE d.instrument_id = @InstrumentId
            ORDER BY a.dimension_id, a.ordinal;
            """, new { InstrumentId = instrumentId }, transaction).ToLookup(attribute => attribute.OwnerId);
        foreach (var existingDimensionId in existingDimensionIds)
        {
            var existingAttributes = existingAttributesByDimension[existingDimensionId]
                .Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue));
            if (existingAttributes.SequenceEqual(attributes))
            {
                return existingDimensionId;
            }
        }

        if (existingDimensionIds.Count >= TelemetryRepositoryLimits.MaxDimensionCount)
        {
            throw new InvalidOperationException($"Dimension limit of {TelemetryRepositoryLimits.MaxDimensionCount} reached.");
        }
        var dimensionId = connection.QuerySingle<long>("""
            INSERT INTO telemetry_metric_dimensions (instrument_id)
            VALUES (@InstrumentId)
            RETURNING dimension_id;
            """, new { InstrumentId = instrumentId }, transaction);
        connection.Execute("""
            INSERT INTO telemetry_metric_dimension_attributes (dimension_id, ordinal, attribute_key, attribute_value)
            VALUES (@DimensionId, @Ordinal, @Key, @Value);
            """, attributes.Select((attribute, ordinal) => new { DimensionId = dimensionId, Ordinal = ordinal, attribute.Key, attribute.Value }), transaction);

        if (pointAttributes.Count == 1 && pointAttributes[0].Key == "otel.metric.overflow" && pointAttributes[0].Value.GetString() == "true")
        {
            connection.Execute("UPDATE telemetry_metric_instruments SET has_overflow = 1 WHERE instrument_id = @InstrumentId;", new { InstrumentId = instrumentId }, transaction);
        }
        return dimensionId;
    }

    private static MetricPointRecord? GetLatestMetricPoint(SqliteConnection connection, IDbTransaction transaction, long dimensionId)
    {
        return connection.QuerySingleOrDefault<MetricPointRecord>("""
            SELECT
                point_id AS PointId,
                point_type AS PointType,
                end_time_ticks AS EndTimeTicks,
                integer_value AS IntegerValue,
                double_value AS DoubleValue,
                histogram_count AS HistogramCount
            FROM telemetry_metric_points
            WHERE dimension_id = @DimensionId
            ORDER BY point_id DESC
            LIMIT 1;
            """, new { DimensionId = dimensionId }, transaction);
    }

    private void AddMetricExemplars(SqliteConnection connection, IDbTransaction transaction, long pointId, RepeatedField<Exemplar> exemplars)
    {
        foreach (var exemplar in exemplars)
        {
            if (exemplar.TraceId is null || exemplar.SpanId is null)
            {
                continue;
            }
            var startTicks = OtlpHelpers.UnixNanoSecondsToDateTime(exemplar.TimeUnixNano).Ticks;
            var value = exemplar.HasAsDouble ? exemplar.AsDouble : exemplar.AsInt;
            var exists = connection.QuerySingle<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM telemetry_metric_exemplars
                    WHERE point_id = @PointId AND start_time_ticks = @StartTimeTicks AND exemplar_value = @Value
                );
                """, new { PointId = pointId, StartTimeTicks = startTicks, Value = value }, transaction);
            if (exists)
            {
                continue;
            }
            var exemplarId = connection.QuerySingle<long>("""
                INSERT INTO telemetry_metric_exemplars (
                    point_id, start_time_ticks, exemplar_value, span_id, trace_id)
                VALUES (@PointId, @StartTimeTicks, @Value, @SpanId, @TraceId)
                RETURNING exemplar_id;
                """, new
            {
                PointId = pointId,
                StartTimeTicks = startTicks,
                Value = value,
                SpanId = exemplar.SpanId.ToHexString(),
                TraceId = exemplar.TraceId.ToHexString()
            }, transaction);
            var attributes = exemplar.FilteredAttributes.ToKeyValuePairs(_otlpContext);
            connection.Execute("""
                INSERT INTO telemetry_metric_exemplar_attributes (exemplar_id, ordinal, attribute_key, attribute_value)
                VALUES (@ExemplarId, @Ordinal, @Key, @Value);
                """, attributes.Select((attribute, ordinal) => new { ExemplarId = exemplarId, Ordinal = ordinal, attribute.Key, attribute.Value }), transaction);
        }
    }

    private void TrimMetricDimension(SqliteConnection connection, IDbTransaction transaction, long dimensionId)
    {
        connection.Execute("""
            DELETE FROM telemetry_metric_points
            WHERE point_id IN (
                SELECT point_id
                FROM telemetry_metric_points
                WHERE dimension_id = @DimensionId
                ORDER BY point_id
                LIMIT MAX((SELECT COUNT(*) FROM telemetry_metric_points WHERE dimension_id = @DimensionId) - @MaxMetricsCount, 0)
            );
            """, new { DimensionId = dimensionId, _otlpContext.Options.MaxMetricsCount }, transaction);
    }

    private List<OtlpInstrumentSummary> GetInstrumentsSummariesFromDatabase(ResourceKey key)
    {
        using var connection = _database.OpenConnection();
        var records = QueryMetricInstruments(connection, key, meterName: null, instrumentName: null);
        var scopes = MaterializeMetricScopes(connection, records);
        return records
            .Select(record => CreateMetricSummary(record, scopes[record.ScopeId]))
            .DistinctBy(summary => summary.GetKey())
            .ToList();
    }

    private OtlpInstrumentData? GetInstrumentFromDatabase(GetInstrumentRequest request)
    {
        using var connection = _database.OpenConnection();
        var records = QueryMetricInstruments(connection, request.ResourceKey, request.MeterName, request.InstrumentName);
        if (records.Count == 0)
        {
            return null;
        }
        var scopes = MaterializeMetricScopes(connection, records);
        var dimensions = new List<DimensionScope>();
        var knownAttributeValues = new Dictionary<string, List<string?>>();
        foreach (var record in records)
        {
            foreach (var dimension in MaterializeMetricDimensions(connection, record.InstrumentId, request.StartTime, request.EndTime))
            {
                var isFirst = dimensions.Count == 0;
                foreach (var key in knownAttributeValues.Keys.Union(dimension.Attributes.Select(attribute => attribute.Key)).Distinct().ToList())
                {
                    if (!knownAttributeValues.TryGetValue(key, out var values))
                    {
                        values = [];
                        knownAttributeValues.Add(key, values);
                        if (!isFirst)
                        {
                            values.Add(null);
                        }
                    }
                    var value = OtlpHelpers.GetValue(dimension.Attributes, key);
                    if (!values.Contains(value))
                    {
                        values.Add(value);
                    }
                }
                dimensions.Add(dimension);
            }
        }
        return new OtlpInstrumentData
        {
            Summary = CreateMetricSummary(records[0], scopes[records[0].ScopeId]),
            Dimensions = dimensions,
            KnownAttributeValues = knownAttributeValues,
            HasOverflow = records.Any(record => record.HasOverflow)
        };
    }

    private DateTime? GetInstrumentLatestEndTimeFromDatabase(ResourceKey resourceKey, string meterName, string instrumentName)
    {
        using var connection = _database.OpenConnection();
        var endTimeTicks = connection.QuerySingleOrDefault<long?>("""
            SELECT MAX(p.end_time_ticks)
            FROM telemetry_metric_points p
            JOIN telemetry_metric_dimensions d ON d.dimension_id = p.dimension_id
            JOIN telemetry_metric_instruments i ON i.instrument_id = d.instrument_id
            JOIN telemetry_resources r ON r.resource_id = i.resource_id
            JOIN telemetry_scopes s ON s.scope_id = i.scope_id
            WHERE r.resource_name = @ResourceName COLLATE ORDINAL_IGNORE_CASE
              AND (@InstanceId IS NULL OR (r.instance_id_is_null = 0 AND r.instance_id = @InstanceId COLLATE ORDINAL_IGNORE_CASE))
              AND s.scope_name = @MeterName
              AND i.instrument_name = @InstrumentName;
            """, new { ResourceName = resourceKey.Name, resourceKey.InstanceId, MeterName = meterName, InstrumentName = instrumentName });
        return endTimeTicks is not null ? new DateTime(endTimeTicks.Value, DateTimeKind.Utc) : null;
    }

    private OtlpInstrument? GetResourceInstrumentFromDatabase(
        ResourceKey resourceKey,
        string meterName,
        string instrumentName,
        DateTime? startTime,
        DateTime? endTime)
    {
        var data = GetInstrumentFromDatabase(new GetInstrumentRequest
        {
            ResourceKey = resourceKey,
            MeterName = meterName,
            InstrumentName = instrumentName,
            StartTime = startTime,
            EndTime = endTime
        });
        if (data is null)
        {
            return null;
        }

        var instrument = new OtlpInstrument
        {
            Summary = data.Summary,
            Context = _otlpContext,
            HasOverflow = data.HasOverflow
        };
        foreach (var (key, values) in data.KnownAttributeValues)
        {
            instrument.KnownAttributeValues.Add(key, values);
        }
        foreach (var dimension in data.Dimensions)
        {
            instrument.Dimensions.Add(dimension.Attributes, dimension);
        }
        return instrument;
    }

    private static List<MetricInstrumentRecord> QueryMetricInstruments(
        SqliteConnection connection,
        ResourceKey key,
        string? meterName,
        string? instrumentName)
    {
        return connection.Query<MetricInstrumentRecord>("""
            SELECT
                i.instrument_id AS InstrumentId,
                i.scope_id AS ScopeId,
                i.instrument_name AS InstrumentName,
                i.description AS Description,
                i.unit AS Unit,
                i.instrument_type AS InstrumentType,
                i.aggregation_temporality AS AggregationTemporality,
                i.has_overflow AS HasOverflow
            FROM telemetry_metric_instruments i
            JOIN telemetry_resources r ON r.resource_id = i.resource_id
            JOIN telemetry_scopes s ON s.scope_id = i.scope_id
            WHERE r.resource_name = @ResourceName COLLATE ORDINAL_IGNORE_CASE
              AND (@InstanceId IS NULL OR (r.instance_id_is_null = 0 AND r.instance_id = @InstanceId COLLATE ORDINAL_IGNORE_CASE))
              AND (@MeterName IS NULL OR s.scope_name = @MeterName)
              AND (@InstrumentName IS NULL OR i.instrument_name = @InstrumentName)
            ORDER BY i.instrument_id;
            """, new { ResourceName = key.Name, key.InstanceId, MeterName = meterName, InstrumentName = instrumentName }).AsList();
    }

    private static Dictionary<long, OtlpScope> MaterializeMetricScopes(SqliteConnection connection, List<MetricInstrumentRecord> records)
    {
        var scopeIds = records.Select(record => record.ScopeId).Distinct().ToArray();
        var scopeRecords = connection.Query<MetricScopeRecord>("""
            SELECT scope_id AS ScopeId, scope_name AS ScopeName, scope_version AS ScopeVersion
            FROM telemetry_scopes
            WHERE scope_id IN @Ids;
            """, new { Ids = scopeIds }).ToDictionary(record => record.ScopeId);
        var attributes = connection.Query<OwnedAttributeRecord>("""
            SELECT scope_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_scope_attributes
            WHERE scope_id IN @Ids
            ORDER BY scope_id, ordinal;
            """, new { Ids = scopeIds }).ToLookup(record => record.OwnerId);
        return scopeRecords.ToDictionary(
            pair => pair.Key,
            pair => CreateScope(pair.Value.ScopeName, pair.Value.ScopeVersion, attributes[pair.Key].Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray()));
    }

    private List<DimensionScope> MaterializeMetricDimensions(
        SqliteConnection connection,
        long instrumentId,
        DateTime? startTime,
        DateTime? endTime)
    {
        var dimensionIds = connection.Query<long>("SELECT dimension_id FROM telemetry_metric_dimensions WHERE instrument_id = @InstrumentId ORDER BY dimension_id;", new { InstrumentId = instrumentId }).AsList();
        var attributes = connection.Query<OwnedAttributeRecord>("""
            SELECT dimension_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_metric_dimension_attributes
            WHERE dimension_id IN @Ids
            ORDER BY dimension_id, ordinal;
            """, new { Ids = dimensionIds }).ToLookup(record => record.OwnerId);
        var points = connection.Query<MetricPointDataRecord>("""
            SELECT
                point_id AS PointId,
                dimension_id AS DimensionId,
                point_type AS PointType,
                start_time_ticks AS StartTimeTicks,
                end_time_ticks AS EndTimeTicks,
                repeat_count AS RepeatCount,
                integer_value AS IntegerValue,
                double_value AS DoubleValue,
                histogram_sum AS HistogramSum,
                histogram_count AS HistogramCount
            FROM telemetry_metric_points
            WHERE dimension_id IN @Ids
              AND (@ApplyRange = 0 OR (start_time_ticks <= @EndTicks AND end_time_ticks >= @StartTicks) OR (start_time_ticks >= @StartTicks AND end_time_ticks <= @EndTicks))
            ORDER BY dimension_id, point_id;
            """, new
        {
            Ids = dimensionIds,
            ApplyRange = startTime is not null && endTime is not null,
            StartTicks = startTime?.Ticks ?? 0,
            EndTicks = endTime?.Ticks ?? 0
        }).ToLookup(record => record.DimensionId);
        var allPointIds = points.SelectMany(group => group).Select(point => point.PointId).ToArray();
        var bucketCounts = connection.Query<MetricHistogramBucketRecord>("""
            SELECT point_id AS PointId, bucket_count AS BucketCount
            FROM telemetry_metric_histogram_bucket_counts
            WHERE point_id IN @Ids
            ORDER BY point_id, ordinal;
            """, new { Ids = allPointIds }).ToLookup(record => record.PointId);
        var explicitBounds = connection.Query<MetricHistogramBoundRecord>("""
            SELECT point_id AS PointId, explicit_bound AS ExplicitBound
            FROM telemetry_metric_histogram_explicit_bounds
            WHERE point_id IN @Ids
            ORDER BY point_id, ordinal;
            """, new { Ids = allPointIds }).ToLookup(record => record.PointId);
        var exemplars = MaterializeMetricExemplars(connection, allPointIds);

        var results = new List<DimensionScope>(dimensionIds.Count);
        foreach (var dimensionId in dimensionIds)
        {
            var dimension = new DimensionScope(
                _otlpContext.Options.MaxMetricsCount,
                attributes[dimensionId].Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray());
            if (startTime is not null && endTime is not null)
            {
                foreach (var point in points[dimensionId])
                {
                    MetricValueBase value = point.PointType switch
                    {
                        LongPointType => new MetricValue<long>(point.IntegerValue!.Value, new DateTime(point.StartTimeTicks, DateTimeKind.Utc), new DateTime(point.EndTimeTicks, DateTimeKind.Utc)),
                        DoublePointType => new MetricValue<double>(point.DoubleValue!.Value, new DateTime(point.StartTimeTicks, DateTimeKind.Utc), new DateTime(point.EndTimeTicks, DateTimeKind.Utc)),
                        HistogramPointType => new HistogramValue(
                            bucketCounts[point.PointId].Select(bucket => ulong.Parse(bucket.BucketCount, CultureInfo.InvariantCulture)).ToArray(),
                            point.HistogramSum!.Value,
                            ulong.Parse(point.HistogramCount!, CultureInfo.InvariantCulture),
                            new DateTime(point.StartTimeTicks, DateTimeKind.Utc),
                            new DateTime(point.EndTimeTicks, DateTimeKind.Utc),
                            explicitBounds[point.PointId].Select(bound => bound.ExplicitBound).ToArray()),
                        _ => throw new InvalidOperationException($"Unknown metric point type '{point.PointType}'.")
                    };
                    if (point.PointType != HistogramPointType)
                    {
                        value.Count = checked((ulong)point.RepeatCount);
                    }
                    value.Exemplars.AddRange(exemplars[point.PointId]);
                    dimension.Values.Add(value);
                }
            }
            results.Add(dimension);
        }
        return results;
    }

    private static ILookup<long, MetricsExemplar> MaterializeMetricExemplars(SqliteConnection connection, long[] pointIds)
    {
        var records = connection.Query<MetricExemplarRecord>("""
            SELECT
                exemplar_id AS ExemplarId,
                point_id AS PointId,
                start_time_ticks AS StartTimeTicks,
                exemplar_value AS ExemplarValue,
                span_id AS SpanId,
                trace_id AS TraceId
            FROM telemetry_metric_exemplars
            WHERE point_id IN @Ids
            ORDER BY point_id, exemplar_id;
            """, new { Ids = pointIds }).AsList();
        var attributes = connection.Query<OwnedAttributeRecord>("""
            SELECT exemplar_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_metric_exemplar_attributes
            WHERE exemplar_id IN @Ids
            ORDER BY exemplar_id, ordinal;
            """, new { Ids = records.Select(record => record.ExemplarId).ToArray() }).ToLookup(record => record.OwnerId);
        return records.Select(record => new KeyValuePair<long, MetricsExemplar>(record.PointId, new MetricsExemplar
        {
            Start = new DateTime(record.StartTimeTicks, DateTimeKind.Utc),
            Value = record.ExemplarValue,
            SpanId = record.SpanId,
            TraceId = record.TraceId,
            Attributes = attributes[record.ExemplarId].Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray()
        })).ToLookup(pair => pair.Key, pair => pair.Value);
    }

    private void ClearSelectedMetricsFromDatabase(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        using var connection = _database.OpenConnection();
        foreach (var resource in connection.Query<TelemetryResourceRecord>("SELECT resource_name AS ResourceName, instance_id AS InstanceId, instance_id_is_null AS InstanceIdIsNull FROM telemetry_resources;"))
        {
            var key = new ResourceKey(resource.ResourceName, resource.InstanceIdIsNull ? null : resource.InstanceId);
            if (selectedResources.TryGetValue(key.GetCompositeName(), out var dataTypes) && dataTypes.Contains(AspireDataType.Metrics) && !dataTypes.Contains(AspireDataType.Resource))
            {
                ClearMetricsFromDatabase(key);
            }
        }
    }

    private void ClearMetricsFromDatabase(ResourceKey? resourceKey)
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
                DELETE FROM telemetry_metric_instruments
                WHERE resource_id IN (SELECT resource_id FROM telemetry_resources{where});

                UPDATE telemetry_resources
                SET has_metrics = EXISTS (SELECT 1 FROM telemetry_metric_instruments WHERE telemetry_metric_instruments.resource_id = telemetry_resources.resource_id);
                """, parameters, transaction);
            DeleteOrphanedScopes(connection, transaction);
            transaction.Commit();
        }
    }

    private static OtlpInstrumentSummary CreateMetricSummary(MetricInstrumentRecord record, OtlpScope scope)
    {
        return new OtlpInstrumentSummary
        {
            Name = record.InstrumentName,
            Description = record.Description,
            Unit = record.Unit,
            Type = (OtlpInstrumentType)record.InstrumentType,
            AggregationTemporality = (OtlpAggregationTemporality)record.AggregationTemporality,
            Parent = scope
        };
    }

    private static OtlpScope CreateScope(string name, string version, KeyValuePair<string, string>[] attributes)
    {
        return name == OtlpScope.Empty.Name && version.Length == 0 && attributes.Length == 0
            ? OtlpScope.Empty
            : new OtlpScope(name, version, attributes);
    }

    private static OtlpInstrumentType MapMetricType(Metric.DataOneofCase dataCase)
    {
        return dataCase switch
        {
            Metric.DataOneofCase.Gauge => OtlpInstrumentType.Gauge,
            Metric.DataOneofCase.Sum => OtlpInstrumentType.Sum,
            Metric.DataOneofCase.Histogram => OtlpInstrumentType.Histogram,
            _ => OtlpInstrumentType.Unsupported
        };
    }

    private static OtlpAggregationTemporality MapAggregationTemporality(Metric metric)
    {
        return metric.DataCase switch
        {
            Metric.DataOneofCase.Sum => (OtlpAggregationTemporality)metric.Sum.AggregationTemporality,
            Metric.DataOneofCase.Histogram => (OtlpAggregationTemporality)metric.Histogram.AggregationTemporality,
            Metric.DataOneofCase.ExponentialHistogram => (OtlpAggregationTemporality)metric.ExponentialHistogram.AggregationTemporality,
            _ => OtlpAggregationTemporality.Unspecified
        };
    }

    private sealed class MetricAttributeComparer : IComparer<KeyValuePair<string, string>>
    {
        public static readonly MetricAttributeComparer Instance = new();

        public int Compare(KeyValuePair<string, string> x, KeyValuePair<string, string> y) => string.Compare(x.Key, y.Key, StringComparison.Ordinal);
    }

    private class MetricPointRecord
    {
        public required long PointId { get; init; }
        public required int PointType { get; init; }
        public required long EndTimeTicks { get; init; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public string? HistogramCount { get; init; }
    }

    private sealed class MetricInstrumentRecord
    {
        public required long InstrumentId { get; init; }
        public required long ScopeId { get; init; }
        public required string InstrumentName { get; init; }
        public required string Description { get; init; }
        public required string Unit { get; init; }
        public required int InstrumentType { get; init; }
        public required int AggregationTemporality { get; init; }
        public required bool HasOverflow { get; init; }
    }

    private sealed class MetricScopeRecord
    {
        public required long ScopeId { get; init; }
        public required string ScopeName { get; init; }
        public required string ScopeVersion { get; init; }
    }

    private sealed class MetricPointDataRecord : MetricPointRecord
    {
        public required long DimensionId { get; init; }
        public required long StartTimeTicks { get; init; }
        public required long RepeatCount { get; init; }
        public double? HistogramSum { get; init; }
    }

    private sealed class MetricHistogramBucketRecord
    {
        public required long PointId { get; init; }
        public required string BucketCount { get; init; }
    }

    private sealed class MetricHistogramBoundRecord
    {
        public required long PointId { get; init; }
        public required double ExplicitBound { get; init; }
    }

    private sealed class MetricExemplarRecord
    {
        public required long ExemplarId { get; init; }
        public required long PointId { get; init; }
        public required long StartTimeTicks { get; init; }
        public required double ExemplarValue { get; init; }
        public required string SpanId { get; init; }
        public required string TraceId { get; init; }
    }
}