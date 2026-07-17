// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
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
    private const int MaxMetricPointBatchSize = 100;

    private readonly MetricIngestionState _metricIngestionState = new();

    private void AddMetricsToDatabase(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        lock (_writeLock)
        {
            _metricIngestionState.DimensionsToTrim.Clear();
            try
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var pointBatch = new MetricPointBatch();
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
                            AddMetricToDatabase(connection, transaction, context, resourceId, scopeId, scope, metric, _metricIngestionState, pointBatch);
                        }
                    }

                    connection.Execute(
                        "UPDATE telemetry_resources SET has_metrics = 1 WHERE resource_id = @ResourceId;",
                        new { ResourceId = resourceId },
                        transaction);
                }

                ExecuteMetricPointBatch(connection, transaction, pointBatch);

                TrimMetricDimensions(connection, transaction, _metricIngestionState.DimensionsToTrim);

                transaction.Commit();
                _metricIngestionState.DimensionsToTrim.Clear();
            }
            catch
            {
                // Cache entries can refer to changes that were rolled back with the transaction.
                ClearIngestionCaches();
                throw;
            }
        }
    }

    private void AddMetricToDatabase(
        SqliteConnection connection,
        IDbTransaction transaction,
        AddContext context,
        long resourceId,
        long scopeId,
        OtlpScope scope,
        Metric metric,
        MetricIngestionState ingestionState,
        MetricPointBatch pointBatch)
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
            instrumentId = GetOrAddMetricInstrument(connection, transaction, resourceId, scopeId, metric, ingestionState);
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
                    AddNumberMetricPoint(connection, transaction, context, instrumentId, scope, point, ingestionState, pointBatch);
                }
                break;
            case Metric.DataOneofCase.Sum:
                foreach (var point in metric.Sum.DataPoints)
                {
                    AddNumberMetricPoint(connection, transaction, context, instrumentId, scope, point, ingestionState, pointBatch);
                }
                break;
            case Metric.DataOneofCase.Histogram:
                foreach (var point in metric.Histogram.DataPoints)
                {
                    AddHistogramMetricPoint(connection, transaction, context, instrumentId, scope, point, ingestionState, pointBatch);
                }
                break;
        }
    }

    private static long GetOrAddMetricInstrument(
        SqliteConnection connection,
        IDbTransaction transaction,
        long resourceId,
        long scopeId,
        Metric metric,
        MetricIngestionState ingestionState)
    {
        if (string.IsNullOrEmpty(metric.Name))
        {
            throw new InvalidOperationException("Instrument name is required.");
        }

        var instrumentKey = (resourceId, scopeId, metric.Name);
        if (ingestionState.InstrumentIds.TryGetValue(instrumentKey, out var instrumentId))
        {
            return instrumentId;
        }

        var existingId = connection.QuerySingleOrDefault<long?>("""
            SELECT instrument_id
            FROM telemetry_metric_instruments
            WHERE resource_id = @ResourceId AND scope_id = @ScopeId AND instrument_name = @InstrumentName;
            """, new { ResourceId = resourceId, ScopeId = scopeId, InstrumentName = metric.Name }, transaction);
        if (existingId is not null)
        {
            ingestionState.InstrumentIds.Add(instrumentKey, existingId.Value);
            return existingId.Value;
        }

        var instrumentCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM telemetry_metric_instruments WHERE resource_id = @ResourceId;", new { ResourceId = resourceId }, transaction);
        if (instrumentCount >= TelemetryRepositoryLimits.MaxInstrumentCount)
        {
            throw new InvalidOperationException($"Instrument limit of {TelemetryRepositoryLimits.MaxInstrumentCount} reached. Instrument '{metric.Name}' will not be added.");
        }

        instrumentId = connection.QuerySingle<long>("""
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
        ingestionState.InstrumentIds.Add(instrumentKey, instrumentId);
        return instrumentId;
    }

    private void AddNumberMetricPoint(
        SqliteConnection connection,
        IDbTransaction transaction,
        AddContext context,
        long instrumentId,
        OtlpScope scope,
        NumberDataPoint point,
        MetricIngestionState ingestionState,
        MetricPointBatch pointBatch)
    {
        try
        {
            var dimension = GetOrAddMetricDimension(connection, transaction, instrumentId, scope, point.Attributes, ingestionState);
            var pointType = point.ValueCase switch
            {
                NumberDataPoint.ValueOneofCase.AsInt => LongPointType,
                NumberDataPoint.ValueOneofCase.AsDouble => DoublePointType,
                _ => throw new InvalidOperationException("Metric data point has no value.")
            };
            var pendingLatest = dimension.PendingPoint;
            var latest = dimension.LatestPoint;
            var latestPointType = pendingLatest?.PointType ?? latest?.PointType;
            var latestEndTimeTicks = pendingLatest?.EndTimeTicks ?? latest?.EndTimeTicks;
            var sameValue = latestPointType == pointType && (pendingLatest is not null
                ? pointType == LongPointType ? pendingLatest.IntegerValue == point.AsInt : pendingLatest.DoubleValue == point.AsDouble
                : pointType == LongPointType ? latest?.IntegerValue == point.AsInt : latest?.DoubleValue == point.AsDouble);
            var endTimeTicks = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano).Ticks;
            if (sameValue)
            {
                if (pendingLatest is not null)
                {
                    pendingLatest.EndTimeTicks = endTimeTicks;
                    pendingLatest.RepeatCount++;
                    pendingLatest.SourcePointCount++;
                    pendingLatest.Exemplars.AddRange(point.Exemplars);
                }
                else
                {
                    pointBatch.AddUpdate(latest!.PointId, endTimeTicks, incrementRepeatCount: true);
                    latest.EndTimeTicks = endTimeTicks;
                    AddMetricExemplars(connection, transaction, latest.PointId, point.Exemplars);
                    context.SuccessCount++;
                }
            }
            else
            {
                var start = OtlpHelpers.UnixNanoSecondsToDateTime(point.StartTimeUnixNano);
                if (latestPointType == pointType)
                {
                    start = new DateTime(latestEndTimeTicks!.Value, DateTimeKind.Utc);
                }
                var pendingPoint = new PendingMetricPoint
                {
                    Context = context,
                    Dimension = dimension,
                    PointType = pointType,
                    StartTimeTicks = start.Ticks,
                    EndTimeTicks = endTimeTicks,
                    RepeatCount = 1,
                    IntegerValue = pointType == LongPointType ? point.AsInt : (long?)null,
                    DoubleValue = pointType == DoublePointType ? point.AsDouble : (double?)null,
                    Flags = (long)point.Flags
                };
                pendingPoint.Exemplars.AddRange(point.Exemplars);
                pointBatch.Inserts.Add(pendingPoint);
                dimension.PendingPoint = pendingPoint;
                ingestionState.DimensionsToTrim.Add(dimension.DimensionId);
            }
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
        HistogramDataPoint point,
        MetricIngestionState ingestionState,
        MetricPointBatch pointBatch)
    {
        try
        {
            if (point.BucketCounts.Count > 0 && point.ExplicitBounds.Count == 0)
            {
                throw new InvalidOperationException("Histogram data point has bucket counts without any explicit bounds.");
            }
            var dimension = GetOrAddMetricDimension(connection, transaction, instrumentId, scope, point.Attributes, ingestionState);
            var pendingLatest = dimension.PendingPoint;
            var latest = dimension.LatestPoint;
            var latestPointType = pendingLatest?.PointType ?? latest?.PointType;
            var latestEndTimeTicks = pendingLatest?.EndTimeTicks ?? latest?.EndTimeTicks;
            var histogramCount = point.Count.ToString(CultureInfo.InvariantCulture);
            var sameCount = latestPointType == HistogramPointType &&
                (pendingLatest?.HistogramCount ?? latest?.HistogramCount) == histogramCount;
            var endTimeTicks = OtlpHelpers.UnixNanoSecondsToDateTime(point.TimeUnixNano).Ticks;
            if (sameCount)
            {
                if (pendingLatest is not null)
                {
                    pendingLatest.EndTimeTicks = endTimeTicks;
                    pendingLatest.SourcePointCount++;
                    pendingLatest.Exemplars.AddRange(point.Exemplars);
                }
                else
                {
                    pointBatch.AddUpdate(latest!.PointId, endTimeTicks, incrementRepeatCount: false);
                    latest.EndTimeTicks = endTimeTicks;
                    AddMetricExemplars(connection, transaction, latest.PointId, point.Exemplars);
                    context.SuccessCount++;
                }
            }
            else
            {
                var start = OtlpHelpers.UnixNanoSecondsToDateTime(point.StartTimeUnixNano);
                if (latestPointType == HistogramPointType)
                {
                    start = new DateTime(latestEndTimeTicks!.Value, DateTimeKind.Utc);
                }
                var pendingPoint = new PendingMetricPoint
                {
                    Context = context,
                    Dimension = dimension,
                    PointType = HistogramPointType,
                    StartTimeTicks = start.Ticks,
                    EndTimeTicks = endTimeTicks,
                    RepeatCount = 1,
                    HistogramSum = point.Sum,
                    HistogramCount = histogramCount,
                    Flags = (long)point.Flags,
                    HistogramBucketCounts = point.BucketCounts.Select(count => count.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    HistogramExplicitBounds = point.ExplicitBounds.ToArray()
                };
                pendingPoint.Exemplars.AddRange(point.Exemplars);
                pointBatch.Inserts.Add(pendingPoint);
                dimension.PendingPoint = pendingPoint;
                ingestionState.DimensionsToTrim.Add(dimension.DimensionId);
            }
        }
        catch (Exception exception)
        {
            context.FailureCount++;
            _otlpContext.Logger.LogInformation(exception, "Error adding metric.");
        }
    }

    private void ExecuteMetricPointBatch(SqliteConnection connection, IDbTransaction transaction, MetricPointBatch pointBatch)
    {
        foreach (var updates in pointBatch.Updates.Values.Chunk(MaxMetricPointBatchSize))
        {
            var sql = new StringBuilder("""
                WITH updates(point_id, end_time_ticks, repeat_delta) AS (
                    VALUES
                """);
            var parameters = new DynamicParameters();
            var index = 0;
            foreach (var update in updates)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"        (@PointId{index}, @EndTimeTicks{index}, @RepeatDelta{index})");
                parameters.Add($"PointId{index}", update.PointId);
                parameters.Add($"EndTimeTicks{index}", update.EndTimeTicks);
                parameters.Add($"RepeatDelta{index}", update.RepeatDelta);
                index++;
            }
            sql.AppendLine();
            sql.Append("""
                )
                UPDATE telemetry_metric_points
                SET end_time_ticks = (SELECT end_time_ticks FROM updates WHERE updates.point_id = telemetry_metric_points.point_id),
                    repeat_count = repeat_count + (SELECT repeat_delta FROM updates WHERE updates.point_id = telemetry_metric_points.point_id)
                WHERE point_id IN (SELECT point_id FROM updates);
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }

        // Keep one INSERT statement and RETURNING result set per point inside a single command. This preserves
        // deterministic point-to-ID mapping for histogram and exemplar rows without a round trip per point.
        foreach (var points in pointBatch.Inserts.Chunk(MaxMetricPointBatchSize))
        {
            var insertSql = new StringBuilder();
            var insertParameters = new DynamicParameters();
            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];
                insertSql.Append(CultureInfo.InvariantCulture, $$"""
                    INSERT INTO telemetry_metric_points (
                        dimension_id, point_type, start_time_ticks, end_time_ticks, repeat_count,
                        integer_value, double_value, histogram_sum, histogram_count, flags)
                    VALUES (
                        @DimensionId{{i}}, @PointType{{i}}, @StartTimeTicks{{i}}, @EndTimeTicks{{i}}, @RepeatCount{{i}},
                        @IntegerValue{{i}}, @DoubleValue{{i}}, @HistogramSum{{i}}, @HistogramCount{{i}}, @Flags{{i}})
                    RETURNING point_id;
                    """);
                insertParameters.Add($"DimensionId{i}", point.Dimension.DimensionId);
                insertParameters.Add($"PointType{i}", point.PointType);
                insertParameters.Add($"StartTimeTicks{i}", point.StartTimeTicks);
                insertParameters.Add($"EndTimeTicks{i}", point.EndTimeTicks);
                insertParameters.Add($"RepeatCount{i}", point.RepeatCount);
                insertParameters.Add($"IntegerValue{i}", point.IntegerValue);
                insertParameters.Add($"DoubleValue{i}", point.DoubleValue);
                insertParameters.Add($"HistogramSum{i}", point.HistogramSum);
                insertParameters.Add($"HistogramCount{i}", point.HistogramCount);
                insertParameters.Add($"Flags{i}", point.Flags);
            }

            using var reader = connection.QueryMultiple(insertSql.ToString(), insertParameters, transaction);
            foreach (var point in points)
            {
                point.PointId = reader.ReadSingle<long>();
            }
        }

        foreach (var point in pointBatch.Inserts)
        {
            try
            {
                InsertHistogramBucketCounts(connection, transaction, point);
                InsertHistogramExplicitBounds(connection, transaction, point);
                AddMetricExemplars(connection, transaction, point.PointId, point.Exemplars);
                point.Context.SuccessCount += point.SourcePointCount;
            }
            catch (Exception exception)
            {
                point.Context.FailureCount += point.SourcePointCount;
                _otlpContext.Logger.LogInformation(exception, "Error adding metric.");
            }

            if (ReferenceEquals(point.Dimension.PendingPoint, point))
            {
                point.Dimension.LatestPoint = new MetricPointRecord
                {
                    PointId = point.PointId,
                    PointType = point.PointType,
                    EndTimeTicks = point.EndTimeTicks,
                    IntegerValue = point.IntegerValue,
                    DoubleValue = point.DoubleValue,
                    HistogramCount = point.HistogramCount
                };
                point.Dimension.PendingPoint = null;
            }
        }
    }

    private static void InsertHistogramBucketCounts(SqliteConnection connection, IDbTransaction transaction, PendingMetricPoint point)
    {
        if (point.HistogramBucketCounts is not { Length: > 0 } bucketCounts)
        {
            return;
        }

        for (var offset = 0; offset < bucketCounts.Length; offset += MaxMetricPointBatchSize)
        {
            var count = Math.Min(MaxMetricPointBatchSize, bucketCounts.Length - offset);
            var sql = new StringBuilder("""
                INSERT INTO telemetry_metric_histogram_bucket_counts (point_id, ordinal, bucket_count)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@PointId{index}, @Ordinal{index}, @BucketCount{index})");
                parameters.Add($"PointId{index}", point.PointId);
                parameters.Add($"Ordinal{index}", offset + index);
                parameters.Add($"BucketCount{index}", bucketCounts[offset + index]);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private static void InsertHistogramExplicitBounds(SqliteConnection connection, IDbTransaction transaction, PendingMetricPoint point)
    {
        if (point.HistogramExplicitBounds is not { Length: > 0 } explicitBounds)
        {
            return;
        }

        for (var offset = 0; offset < explicitBounds.Length; offset += MaxMetricPointBatchSize)
        {
            var count = Math.Min(MaxMetricPointBatchSize, explicitBounds.Length - offset);
            var sql = new StringBuilder("""
                INSERT INTO telemetry_metric_histogram_explicit_bounds (point_id, ordinal, explicit_bound)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@PointId{index}, @Ordinal{index}, @ExplicitBound{index})");
                parameters.Add($"PointId{index}", point.PointId);
                parameters.Add($"Ordinal{index}", offset + index);
                parameters.Add($"ExplicitBound{index}", explicitBounds[offset + index]);
            }
            sql.Append(';');
            connection.Execute(sql.ToString(), parameters, transaction);
        }
    }

    private MetricDimensionState GetOrAddMetricDimension(
        SqliteConnection connection,
        IDbTransaction transaction,
        long instrumentId,
        OtlpScope scope,
        RepeatedField<KeyValue> pointAttributes,
        MetricIngestionState ingestionState)
    {
        KeyValuePair<string, string>[]? temporaryAttributes = null;
        OtlpHelpers.CopyKeyValuePairs(pointAttributes, scope.Attributes, _otlpContext, out var copyCount, ref temporaryAttributes);
        Array.Sort(temporaryAttributes, 0, copyCount, MetricAttributeComparer.Instance);
        var attributes = temporaryAttributes.AsSpan(0, copyCount).ToArray();
        var attributeHash = GetMetricDimensionAttributeHash(attributes);
        var cacheKey = (instrumentId, attributeHash);
        if (!ingestionState.Dimensions.TryGetValue(cacheKey, out var candidates))
        {
            candidates = connection.Query<MetricDimensionStateRecord>("""
                SELECT
                    d.dimension_id AS DimensionId,
                    a.attribute_key AS AttributeKey,
                    a.attribute_value AS AttributeValue,
                    p.point_id AS PointId,
                    p.point_type AS PointType,
                    p.end_time_ticks AS EndTimeTicks,
                    p.integer_value AS IntegerValue,
                    p.double_value AS DoubleValue,
                    p.histogram_count AS HistogramCount
                FROM telemetry_metric_dimensions d
                LEFT JOIN telemetry_metric_dimension_attributes a ON a.dimension_id = d.dimension_id
                LEFT JOIN telemetry_metric_points p ON p.point_id = (
                    SELECT point_id
                    FROM telemetry_metric_points
                    WHERE dimension_id = d.dimension_id
                    ORDER BY point_id DESC
                    LIMIT 1
                )
                WHERE d.instrument_id = @InstrumentId
                  AND d.attribute_hash = @AttributeHash
                ORDER BY d.dimension_id, a.ordinal;
                """, new { InstrumentId = instrumentId, AttributeHash = attributeHash }, transaction)
                .GroupBy(record => record.DimensionId)
                .Select(group =>
                {
                    var first = group.First();
                    return new MetricDimensionState
                    {
                        DimensionId = group.Key,
                        Attributes = group
                            .Where(record => record.AttributeKey is not null)
                            .Select(record => KeyValuePair.Create(record.AttributeKey!, record.AttributeValue!))
                            .ToArray(),
                        LatestPoint = first.PointId is not null
                            ? new MetricPointRecord
                            {
                                PointId = first.PointId.Value,
                                PointType = first.PointType!.Value,
                                EndTimeTicks = first.EndTimeTicks!.Value,
                                IntegerValue = first.IntegerValue,
                                DoubleValue = first.DoubleValue,
                                HistogramCount = first.HistogramCount
                            }
                            : null
                    };
                })
                .ToList();
            ingestionState.Dimensions.Add(cacheKey, candidates);
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Attributes.SequenceEqual(attributes))
            {
                return candidate;
            }
        }

        if (!ingestionState.DimensionCounts.TryGetValue(instrumentId, out var dimensionCount))
        {
            dimensionCount = connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM telemetry_metric_dimensions WHERE instrument_id = @InstrumentId;",
                new { InstrumentId = instrumentId },
                transaction);
        }
        if (dimensionCount >= TelemetryRepositoryLimits.MaxDimensionCount)
        {
            throw new InvalidOperationException($"Dimension limit of {TelemetryRepositoryLimits.MaxDimensionCount} reached.");
        }
        var dimensionId = connection.QuerySingle<long>("""
            INSERT INTO telemetry_metric_dimensions (instrument_id, attribute_hash)
            VALUES (@InstrumentId, @AttributeHash)
            RETURNING dimension_id;
            """, new { InstrumentId = instrumentId, AttributeHash = attributeHash }, transaction);
        connection.Execute("""
            INSERT INTO telemetry_metric_dimension_attributes (dimension_id, ordinal, attribute_key, attribute_value)
            VALUES (@DimensionId, @Ordinal, @Key, @Value);
            """, attributes.Select((attribute, ordinal) => new { DimensionId = dimensionId, Ordinal = ordinal, attribute.Key, attribute.Value }), transaction);

        if (pointAttributes.Count == 1 && pointAttributes[0].Key == "otel.metric.overflow" && pointAttributes[0].Value.GetString() == "true")
        {
            connection.Execute("UPDATE telemetry_metric_instruments SET has_overflow = 1 WHERE instrument_id = @InstrumentId;", new { InstrumentId = instrumentId }, transaction);
        }
        var dimension = new MetricDimensionState { DimensionId = dimensionId, Attributes = attributes };
        candidates.Add(dimension);
        ingestionState.DimensionCounts[instrumentId] = dimensionCount + 1;
        return dimension;
    }

    private static long GetMetricDimensionAttributeHash(ReadOnlySpan<KeyValuePair<string, string>> attributes)
    {
        var hash = new XxHash3();
        foreach (var attribute in attributes)
        {
            AppendHashValue(hash, attribute.Key);
            AppendHashValue(hash, attribute.Value);
        }

        return BinaryPrimitives.ReadInt64LittleEndian(hash.GetCurrentHash());

        static void AppendHashValue(XxHash3 hash, string value)
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, valueBytes.Length);
            hash.Append(lengthBytes);
            hash.Append(valueBytes);
        }
    }

    private void AddMetricExemplars(SqliteConnection connection, IDbTransaction transaction, long pointId, IEnumerable<Exemplar> exemplars)
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

    private void TrimMetricDimensions(SqliteConnection connection, IDbTransaction transaction, IEnumerable<long> dimensionIds)
    {
        foreach (var ids in dimensionIds.Chunk(MaxMetricPointBatchSize))
        {
            connection.Execute("""
                DELETE FROM telemetry_metric_points
                WHERE point_id IN (
                    SELECT point_id
                    FROM (
                        SELECT
                            point_id,
                            ROW_NUMBER() OVER (PARTITION BY dimension_id ORDER BY point_id DESC) AS point_rank
                        FROM telemetry_metric_points
                        WHERE dimension_id IN @DimensionIds
                    )
                    WHERE point_rank > @MaxMetricsCount
                );
                """, new { DimensionIds = ids, _otlpContext.Options.MaxMetricsCount }, transaction);
        }
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
            WHERE r.resource_name = @ResourceName COLLATE NOCASE
                            AND (@InstanceId IS NULL OR r.instance_id = @InstanceId COLLATE NOCASE)
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
            WHERE r.resource_name = @ResourceName COLLATE NOCASE
                            AND (@InstanceId IS NULL OR r.instance_id = @InstanceId COLLATE NOCASE)
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
        foreach (var resource in connection.Query<TelemetryResourceRecord>("SELECT resource_name AS ResourceName, instance_id AS InstanceId FROM telemetry_resources;"))
        {
            var key = new ResourceKey(resource.ResourceName, resource.InstanceId);
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
                where = " WHERE resource_name = @ResourceName COLLATE NOCASE";
                parameters.Add("ResourceName", resourceKey.Value.Name);
                if (resourceKey.Value.InstanceId is not null)
                {
                    where += " AND instance_id = @InstanceId COLLATE NOCASE";
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
            _metricIngestionState.Clear();
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

    private sealed class MetricIngestionState
    {
        public Dictionary<(long ResourceId, long ScopeId, string InstrumentName), long> InstrumentIds { get; } = [];
        public Dictionary<(long InstrumentId, long AttributeHash), List<MetricDimensionState>> Dimensions { get; } = [];
        public Dictionary<long, int> DimensionCounts { get; } = [];
        public HashSet<long> DimensionsToTrim { get; } = [];

        public void Clear()
        {
            InstrumentIds.Clear();
            Dimensions.Clear();
            DimensionCounts.Clear();
            DimensionsToTrim.Clear();
        }
    }

    private sealed class MetricDimensionState
    {
        public required long DimensionId { get; init; }
        public required KeyValuePair<string, string>[] Attributes { get; init; }
        public MetricPointRecord? LatestPoint { get; set; }
        public PendingMetricPoint? PendingPoint { get; set; }
    }

    private sealed class MetricPointBatch
    {
        public Dictionary<long, MetricPointUpdate> Updates { get; } = [];
        public List<PendingMetricPoint> Inserts { get; } = [];

        public void AddUpdate(long pointId, long endTimeTicks, bool incrementRepeatCount)
        {
            if (!Updates.TryGetValue(pointId, out var update))
            {
                update = new MetricPointUpdate { PointId = pointId };
                Updates.Add(pointId, update);
            }
            update.EndTimeTicks = endTimeTicks;
            if (incrementRepeatCount)
            {
                update.RepeatDelta++;
            }
        }
    }

    private sealed class MetricPointUpdate
    {
        public required long PointId { get; init; }
        public long EndTimeTicks { get; set; }
        public long RepeatDelta { get; set; }
    }

    private sealed class PendingMetricPoint
    {
        public required AddContext Context { get; init; }
        public required MetricDimensionState Dimension { get; init; }
        public required int PointType { get; init; }
        public required long StartTimeTicks { get; init; }
        public required long EndTimeTicks { get; set; }
        public required long RepeatCount { get; set; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public double? HistogramSum { get; init; }
        public string? HistogramCount { get; init; }
        public required long Flags { get; init; }
        public long PointId { get; set; }
        public int SourcePointCount { get; set; } = 1;
        public string[]? HistogramBucketCounts { get; init; }
        public double[]? HistogramExplicitBounds { get; init; }
        public List<Exemplar> Exemplars { get; } = [];
    }

    private sealed class MetricDimensionStateRecord
    {
        public required long DimensionId { get; init; }
        public string? AttributeKey { get; init; }
        public string? AttributeValue { get; init; }
        public long? PointId { get; init; }
        public int? PointType { get; init; }
        public long? EndTimeTicks { get; init; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public string? HistogramCount { get; init; }
    }

    private class MetricPointRecord
    {
        public required long PointId { get; init; }
        public required int PointType { get; init; }
        public required long EndTimeTicks { get; set; }
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