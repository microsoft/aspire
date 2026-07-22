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
using Aspire.Dashboard.Utils;
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
    private const string MetricPointRangeFilterSql = "(@ApplyRange = 0 OR (p.start_time_ticks <= @EndTicks AND p.end_time_ticks >= @StartTicks) OR (p.start_time_ticks >= @StartTicks AND p.end_time_ticks <= @EndTicks))";
    private const string SelectedMetricPointsCteSql = $"""
        selected_metric_points AS (
            SELECT p.*
            FROM telemetry_metric_points p
            WHERE p.dimension_id IN @DimensionIds
              AND {MetricPointRangeFilterSql}
        )
        """;
    private const string EffectiveMetricPointsCteSql = $"""
        {SelectedMetricPointsCteSql},
        ranked_metric_points AS (
            SELECT
                p.*,
                ROW_NUMBER() OVER (
                    PARTITION BY p.start_time_ticks, p.dimension_id
                    ORDER BY p.point_id DESC) AS point_rank
            FROM selected_metric_points p
        ),
        effective_metric_points AS (
            SELECT *
            FROM ranked_metric_points
            WHERE point_rank = 1
        )
        """;

    private readonly MetricIngestionState _metricIngestionState = new();

    private void AddMetricsToDatabase(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        lock (_writeLock)
        {
            _metricIngestionState.DimensionsToTrim.Clear();
            _metricIngestionState.PendingDimensions.Clear();
            _metricIngestionState.PendingDimensionAttributes.Clear();
            try
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction();
                var pointBatch = new MetricPointBatch();
                foreach (var resourceMetricsItem in resourceMetrics)
                {
                    CachedResource cachedResource;
                    try
                    {
                        cachedResource = GetOrAddCachedResource(connection, transaction, resourceMetricsItem.Resource.GetResourceKey());
                    }
                    catch (Exception exception)
                    {
                        context.FailureCount += resourceMetricsItem.ScopeMetrics.Sum(scope => scope.Metrics.Sum(OtlpResource.GetMetricDataPointCount));
                        _otlpContext.Logger.LogInformation(exception, "Error adding resource.");
                        continue;
                    }

                    foreach (var scopeMetrics in resourceMetricsItem.ScopeMetrics)
                    {
                        CachedResourceScope cachedScope;
                        try
                        {
                            cachedScope = GetOrAddCachedScope(connection, transaction, cachedResource, scopeMetrics.Scope, CachedTelemetryType.Metrics);
                        }
                        catch (Exception exception)
                        {
                            context.FailureCount += scopeMetrics.Metrics.Sum(OtlpResource.GetMetricDataPointCount);
                            _otlpContext.Logger.LogInformation(exception, "Error adding metric scope.");
                            continue;
                        }

                        EnsureCachedInstruments(connection, transaction, cachedResource, cachedScope, scopeMetrics.Metrics);
                        foreach (var metric in scopeMetrics.Metrics)
                        {
                            AddMetricToDatabase(connection, transaction, context, cachedResource, cachedScope, metric, _metricIngestionState, pointBatch);
                        }
                    }

                    if (!cachedResource.Resource.HasMetrics)
                    {
                        connection.Execute(
                            "UPDATE telemetry_resources SET has_metrics = 1 WHERE resource_id = @ResourceId;",
                            new { cachedResource.ResourceId },
                            transaction);
                        cachedResource.Resource.HasMetrics = true;
                    }
                }

                InsertMetricDimensions(connection, transaction, _metricIngestionState.PendingDimensions);
                InsertMetricDimensionAttributes(connection, transaction, _metricIngestionState.PendingDimensionAttributes);
                ExecuteMetricPointBatch(connection, transaction, pointBatch);

                TrimMetricDimensions(connection, transaction, _metricIngestionState.DimensionsToTrim);

                transaction.Commit();
                _metricIngestionState.DimensionsToTrim.Clear();
                _metricIngestionState.PendingDimensions.Clear();
                _metricIngestionState.PendingDimensionAttributes.Clear();
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
        CachedResource cachedResource,
        CachedResourceScope cachedScope,
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

        CachedInstrument cachedInstrument;
        try
        {
            cachedInstrument = GetOrAddCachedInstrument(connection, transaction, cachedResource, cachedScope, metric);
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
                    AddNumberMetricPoint(connection, transaction, context, cachedInstrument.InstrumentId, cachedScope.Scope.Scope, point, ingestionState, pointBatch);
                }
                break;
            case Metric.DataOneofCase.Sum:
                foreach (var point in metric.Sum.DataPoints)
                {
                    AddNumberMetricPoint(connection, transaction, context, cachedInstrument.InstrumentId, cachedScope.Scope.Scope, point, ingestionState, pointBatch);
                }
                break;
            case Metric.DataOneofCase.Histogram:
                foreach (var point in metric.Histogram.DataPoints)
                {
                    AddHistogramMetricPoint(connection, transaction, context, cachedInstrument.InstrumentId, cachedScope.Scope.Scope, point, ingestionState, pointBatch);
                }
                break;
        }
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
                    QueueMetricExemplars(pointBatch, latest.PointId, point.Exemplars);
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
                ingestionState.DimensionsToTrim.Add(dimension);
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
            var histogramCount = checked((long)point.Count);
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
                    QueueMetricExemplars(pointBatch, latest.PointId, point.Exemplars);
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
                    HistogramBucketCounts = point.BucketCounts.Select(count => checked((long)count)).ToArray(),
                    HistogramExplicitBounds = point.ExplicitBounds.ToArray()
                };
                pendingPoint.Exemplars.AddRange(point.Exemplars);
                pointBatch.Inserts.Add(pendingPoint);
                dimension.PendingPoint = pendingPoint;
                ingestionState.DimensionsToTrim.Add(dimension);
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
                UPDATE telemetry_metric_points AS points
                SET end_time_ticks = updates.end_time_ticks,
                    repeat_count = points.repeat_count + updates.repeat_delta
                FROM updates
                WHERE points.point_id = updates.point_id;
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

        InsertHistogramBucketCounts(connection, transaction, pointBatch.Inserts);
        InsertHistogramExplicitBounds(connection, transaction, pointBatch.Inserts);
        foreach (var point in pointBatch.Inserts)
        {
            QueueMetricExemplars(pointBatch, point.PointId, point.Exemplars);
        }
        InsertMetricExemplars(connection, transaction, pointBatch.Exemplars);

        foreach (var point in pointBatch.Inserts)
        {
            point.Context.SuccessCount += point.SourcePointCount;

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

    private static void InsertHistogramBucketCounts(
        SqliteConnection connection,
        IDbTransaction transaction,
        List<PendingMetricPoint> points)
    {
        var bucketCounts = points
            .Where(point => point.HistogramBucketCounts is { Length: > 0 })
            .SelectMany(point => point.HistogramBucketCounts!.Select((bucketCount, ordinal) => new PendingHistogramBucketCount(point.PointId, ordinal, bucketCount)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            bucketCounts,
            MaxMetricPointBatchSize,
            "telemetry_metric_histogram_bucket_counts",
            ["point_id", "ordinal", "bucket_count"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.PointId;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.BucketCount;
            });
    }

    private static void InsertHistogramExplicitBounds(
        SqliteConnection connection,
        IDbTransaction transaction,
        List<PendingMetricPoint> points)
    {
        var explicitBounds = points
            .Where(point => point.HistogramExplicitBounds is { Length: > 0 })
            .SelectMany(point => point.HistogramExplicitBounds!.Select((explicitBound, ordinal) => new PendingHistogramExplicitBound(point.PointId, ordinal, explicitBound)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            explicitBounds,
            MaxMetricPointBatchSize,
            "telemetry_metric_histogram_explicit_bounds",
            ["point_id", "ordinal", "explicit_bound"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.PointId;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.ExplicitBound;
            });
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
        if (ingestionState.LoadedDimensionInstruments.Add(instrumentId))
        {
            var dimensions = connection.Query<MetricDimensionStateRecord>("""
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
                ORDER BY d.dimension_id, a.ordinal;
                """, new { InstrumentId = instrumentId }, transaction)
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
            foreach (var loadedDimension in dimensions)
            {
                var dimensionCacheKey = (instrumentId, GetMetricDimensionAttributeHash(loadedDimension.Attributes));
                if (!ingestionState.Dimensions.TryGetValue(dimensionCacheKey, out var dimensionCandidates))
                {
                    dimensionCandidates = [];
                    ingestionState.Dimensions.Add(dimensionCacheKey, dimensionCandidates);
                }
                dimensionCandidates.Add(loadedDimension);
            }
            ingestionState.DimensionCounts[instrumentId] = dimensions.Count;
        }

        if (!ingestionState.Dimensions.TryGetValue(cacheKey, out var candidates))
        {
            candidates = [];
            ingestionState.Dimensions.Add(cacheKey, candidates);
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Attributes.SequenceEqual(attributes))
            {
                return candidate;
            }
        }

        var dimensionCount = ingestionState.DimensionCounts[instrumentId];
        if (dimensionCount >= TelemetryRepositoryLimits.MaxDimensionCount)
        {
            throw new InvalidOperationException($"Dimension limit of {TelemetryRepositoryLimits.MaxDimensionCount} reached.");
        }
        var dimension = new MetricDimensionState { Attributes = attributes };
        ingestionState.PendingDimensions.Add(new PendingMetricDimension(instrumentId, attributeHash, dimension));
        ingestionState.PendingDimensionAttributes.AddRange(attributes.Select((attribute, ordinal) => new PendingMetricDimensionAttribute(
            dimension,
            ordinal,
            attribute.Key,
            attribute.Value)));

        if (pointAttributes.Count == 1 && pointAttributes[0].Key == "otel.metric.overflow" && pointAttributes[0].Value.GetString() == "true")
        {
            connection.Execute("UPDATE telemetry_metric_instruments SET has_overflow = 1 WHERE instrument_id = @InstrumentId;", new { InstrumentId = instrumentId }, transaction);
            MarkCachedInstrumentHasOverflow(instrumentId);
        }
        candidates.Add(dimension);
        ingestionState.DimensionCounts[instrumentId] = dimensionCount + 1;
        return dimension;
    }

    private static void InsertMetricDimensions(
        SqliteConnection connection,
        IDbTransaction transaction,
        List<PendingMetricDimension> dimensions)
    {
        foreach (var batch in dimensions.Chunk(MaxMetricPointBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT INTO telemetry_metric_dimensions (instrument_id, attribute_hash)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@InstrumentId{index}, @AttributeHash{index})");
                parameters.Add($"InstrumentId{index}", batch[index].InstrumentId);
                parameters.Add($"AttributeHash{index}", batch[index].AttributeHash);
            }
            sql.Append("""

                RETURNING dimension_id AS DimensionId, instrument_id AS InstrumentId, attribute_hash AS AttributeHash;
                """);

            // Hash collisions can produce indistinguishable inserted rows. Their IDs are interchangeable here
            // because attributes and points are associated only after an ID is assigned to each pending dimension.
            var pendingDimensions = batch
                .GroupBy(dimension => (dimension.InstrumentId, dimension.AttributeHash))
                .ToDictionary(group => group.Key, group => new Queue<PendingMetricDimension>(group));
            foreach (var insertedDimension in connection.Query<InsertedMetricDimensionRecord>(sql.ToString(), parameters, transaction))
            {
                pendingDimensions[(insertedDimension.InstrumentId, insertedDimension.AttributeHash)]
                    .Dequeue()
                    .Dimension.DimensionId = insertedDimension.DimensionId;
            }
        }
    }

    private static void InsertMetricDimensionAttributes(
        SqliteConnection connection,
        IDbTransaction transaction,
        List<PendingMetricDimensionAttribute> attributes)
    {
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            attributes,
            MaxMetricPointBatchSize,
            "telemetry_metric_dimension_attributes",
            ["dimension_id", "ordinal", "attribute_key", "attribute_value"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.Dimension.DimensionId;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Key;
                parameters[3].Value = row.Value;
            });
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

    private void QueueMetricExemplars(MetricPointBatch pointBatch, long pointId, IEnumerable<Exemplar> exemplars)
    {
        foreach (var exemplar in exemplars)
        {
            if (exemplar.TraceId is null || exemplar.SpanId is null)
            {
                continue;
            }
            var startTicks = OtlpHelpers.UnixNanoSecondsToDateTime(exemplar.TimeUnixNano).Ticks;
            var value = exemplar.HasAsDouble ? exemplar.AsDouble : exemplar.AsInt;
            pointBatch.Exemplars.TryAdd(
                new MetricExemplarKey(pointId, startTicks, value),
                new PendingMetricExemplar
                {
                    PointId = pointId,
                    StartTimeTicks = startTicks,
                    Value = value,
                    SpanId = exemplar.SpanId.ToHexString(),
                    TraceId = exemplar.TraceId.ToHexString(),
                    Attributes = exemplar.FilteredAttributes.ToKeyValuePairs(_otlpContext)
                });
        }
    }

    private static void InsertMetricExemplars(
        SqliteConnection connection,
        IDbTransaction transaction,
        Dictionary<MetricExemplarKey, PendingMetricExemplar> exemplars)
    {
        foreach (var batch in exemplars.Values.Chunk(MaxMetricPointBatchSize))
        {
            var sql = new StringBuilder("""
                INSERT OR IGNORE INTO telemetry_metric_exemplars (
                    point_id, start_time_ticks, exemplar_value, span_id, trace_id)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@PointId{index}, @StartTimeTicks{index}, @Value{index}, @SpanId{index}, @TraceId{index})");
                parameters.Add($"PointId{index}", batch[index].PointId);
                parameters.Add($"StartTimeTicks{index}", batch[index].StartTimeTicks);
                parameters.Add($"Value{index}", batch[index].Value);
                parameters.Add($"SpanId{index}", batch[index].SpanId);
                parameters.Add($"TraceId{index}", batch[index].TraceId);
            }
            sql.Append("""
                RETURNING
                    exemplar_id AS ExemplarId,
                    point_id AS PointId,
                    start_time_ticks AS StartTimeTicks,
                    exemplar_value AS ExemplarValue;
                """);
            foreach (var inserted in connection.Query<InsertedMetricExemplarRecord>(sql.ToString(), parameters, transaction))
            {
                exemplars[new MetricExemplarKey(inserted.PointId, inserted.StartTimeTicks, inserted.ExemplarValue)].ExemplarId = inserted.ExemplarId;
            }
        }

        var attributes = exemplars.Values
            .Where(exemplar => exemplar.ExemplarId is not null)
            .SelectMany(exemplar => exemplar.Attributes.Select((attribute, ordinal) => new PendingMetricExemplarAttribute(
                exemplar.ExemplarId!.Value,
                ordinal,
                attribute.Key,
                attribute.Value)))
            .ToArray();
        SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            attributes,
            MaxMetricPointBatchSize,
            "telemetry_metric_exemplar_attributes",
            ["exemplar_id", "ordinal", "attribute_key", "attribute_value"],
            static (row, parameters) =>
            {
                parameters[0].Value = row.ExemplarId;
                parameters[1].Value = row.Ordinal;
                parameters[2].Value = row.Key;
                parameters[3].Value = row.Value;
            });
    }

    private void TrimMetricDimensions(SqliteConnection connection, IDbTransaction transaction, IEnumerable<MetricDimensionState> dimensions)
    {
        foreach (var batch in dimensions.Chunk(MaxMetricPointBatchSize))
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
                """, new { DimensionIds = batch.Select(dimension => dimension.DimensionId).ToArray(), _otlpContext.Options.MaxMetricsCount }, transaction);
        }
    }

    private OtlpInstrumentData? GetInstrumentFromDatabase(GetInstrumentRequest request)
    {
        var instruments = GetCachedInstruments(request.ResourceKey, request.MeterName, request.InstrumentName);
        if (instruments.Count == 0)
        {
            return null;
        }

        using var connection = _database.OpenConnection();
        var knownAttributeValues = new Dictionary<string, List<string?>>();
        var dimension = MaterializeMetricDimension(
            connection,
            instruments.Select(instrument => instrument.InstrumentId).ToArray(),
            instruments[0].Summary.Type == OtlpInstrumentType.Histogram,
            request.StartTime,
            request.EndTime,
            request.DimensionFilters,
            knownAttributeValues);
        return new OtlpInstrumentData
        {
            Summary = instruments[0].Summary,
            Dimensions = [dimension],
            KnownAttributeValues = knownAttributeValues,
            HasOverflow = instruments.Any(instrument => instrument.HasOverflow)
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

    private DimensionScope MaterializeMetricDimension(
        SqliteConnection connection,
        IReadOnlyList<long> instrumentIds,
        bool isHistogram,
        DateTime? startTime,
        DateTime? endTime,
        IReadOnlyDictionary<string, IReadOnlyList<string?>> dimensionFilters,
        Dictionary<string, List<string?>> knownAttributeValues)
    {
        var dimensionIds = connection.Query<long>("SELECT dimension_id FROM telemetry_metric_dimensions WHERE instrument_id IN @InstrumentIds ORDER BY dimension_id;", new { InstrumentIds = instrumentIds }).AsList();
        var attributes = connection.Query<OwnedAttributeRecord>("""
            SELECT dimension_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
            FROM telemetry_metric_dimension_attributes
            WHERE dimension_id IN @DimensionIds
            ORDER BY dimension_id, ordinal;
            """, new { DimensionIds = dimensionIds }).ToLookup(record => record.OwnerId);
        PopulateKnownAttributeValues(dimensionIds, attributes, knownAttributeValues);

        var selectedDimensionIds = dimensionIds
            .Where(dimensionId => MatchesDimensionFilters(attributes[dimensionId], dimensionFilters))
            .ToArray();
        var queryParameters = new DynamicParameters();
        queryParameters.Add("DimensionIds", selectedDimensionIds);
        queryParameters.Add("ApplyRange", startTime is not null && endTime is not null);
        queryParameters.Add("StartTicks", startTime?.Ticks ?? 0);
        queryParameters.Add("EndTicks", endTime?.Ticks ?? 0);
        var points = connection.Query<MetricPointDataRecord>($"""
            WITH {EffectiveMetricPointsCteSql},
            aggregated_metric_points AS (
                SELECT
                    p.start_time_ticks AS PointId,
                    p.point_type AS PointType,
                    p.start_time_ticks AS StartTimeTicks,
                    MAX(p.end_time_ticks) AS EndTimeTicks,
                    MAX(p.repeat_count) AS RepeatCount,
                    SUM(p.integer_value) AS IntegerValue,
                    SUM(p.double_value) AS DoubleValue,
                    SUM(p.histogram_sum) AS HistogramSum,
                    SUM(p.histogram_count) AS HistogramCount
                FROM effective_metric_points p
                GROUP BY p.point_type, p.start_time_ticks
            )
            SELECT
                p.PointId,
                0 AS DimensionId,
                p.PointType,
                p.StartTimeTicks,
                p.EndTimeTicks,
                p.RepeatCount,
                p.IntegerValue,
                p.DoubleValue,
                p.HistogramSum,
                p.HistogramCount
            FROM aggregated_metric_points p
            ORDER BY p.StartTimeTicks;
            """, queryParameters).AsList();
        var (bucketCounts, explicitBounds) = isHistogram
            ? MaterializeMetricHistogramData(connection, queryParameters)
            : (Array.Empty<MetricHistogramBucketRecord>().ToLookup(record => record.PointId),
                Array.Empty<MetricHistogramBoundRecord>().ToLookup(record => record.PointId));
        var exemplars = MaterializeMetricExemplars(connection, queryParameters);

        var result = new DimensionScope(_otlpContext.Options.MaxMetricsCount, []);
        if (startTime is not null && endTime is not null)
        {
            foreach (var point in points)
            {
                MetricValueBase value = point.PointType switch
                {
                    LongPointType => new MetricValue<long>(point.IntegerValue!.Value, new DateTime(point.StartTimeTicks, DateTimeKind.Utc), new DateTime(point.EndTimeTicks, DateTimeKind.Utc)),
                    DoublePointType => new MetricValue<double>(point.DoubleValue!.Value, new DateTime(point.StartTimeTicks, DateTimeKind.Utc), new DateTime(point.EndTimeTicks, DateTimeKind.Utc)),
                    HistogramPointType => new HistogramValue(
                        bucketCounts[point.PointId].Select(bucket => checked((ulong)bucket.BucketCount)).ToArray(),
                        point.HistogramSum!.Value,
                        checked((ulong)point.HistogramCount!.Value),
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
                result.Values.Add(value);
            }
        }
        return result;
    }

    private static bool MatchesDimensionFilters(IEnumerable<OwnedAttributeRecord> attributes, IReadOnlyDictionary<string, IReadOnlyList<string?>> dimensionFilters)
    {
        foreach (var (key, values) in dimensionFilters)
        {
            var value = attributes.FirstOrDefault(attribute => attribute.AttributeKey == key)?.AttributeValue;
            if (!values.Contains(value))
            {
                return false;
            }
        }
        return true;
    }

    private static void PopulateKnownAttributeValues(
        IReadOnlyList<long> dimensionIds,
        ILookup<long, OwnedAttributeRecord> attributes,
        Dictionary<string, List<string?>> knownAttributeValues)
    {
        for (var dimensionIndex = 0; dimensionIndex < dimensionIds.Count; dimensionIndex++)
        {
            var dimensionId = dimensionIds[dimensionIndex];
            foreach (var key in knownAttributeValues.Keys.Union(attributes[dimensionId].Select(attribute => attribute.AttributeKey)).Distinct().ToList())
            {
                if (!knownAttributeValues.TryGetValue(key, out var values))
                {
                    values = [];
                    knownAttributeValues.Add(key, values);
                    if (dimensionIndex > 0)
                    {
                        values.Add(null);
                    }
                }
                var value = attributes[dimensionId].FirstOrDefault(attribute => attribute.AttributeKey == key)?.AttributeValue;
                if (!values.Contains(value))
                {
                    values.Add(value);
                }
            }
        }
    }

    private static (ILookup<long, MetricHistogramBucketRecord> BucketCounts, ILookup<long, MetricHistogramBoundRecord> ExplicitBounds) MaterializeMetricHistogramData(
        SqliteConnection connection,
        DynamicParameters queryParameters)
    {
        var bucketCounts = connection.Query<MetricHistogramBucketRecord>($"""
            WITH {EffectiveMetricPointsCteSql}
            SELECT
                p.start_time_ticks AS PointId,
                SUM(b.bucket_count) AS BucketCount
            FROM telemetry_metric_histogram_bucket_counts b
            JOIN effective_metric_points p ON p.point_id = b.point_id
            GROUP BY p.start_time_ticks, b.ordinal
            ORDER BY p.start_time_ticks, b.ordinal;
            """, queryParameters).ToLookup(record => record.PointId);
        var explicitBounds = connection.Query<MetricHistogramBoundRecord>($"""
            WITH {EffectiveMetricPointsCteSql}
            SELECT
                p.start_time_ticks AS PointId,
                MAX(b.explicit_bound) AS ExplicitBound
            FROM telemetry_metric_histogram_explicit_bounds b
            JOIN effective_metric_points p ON p.point_id = b.point_id
            GROUP BY p.start_time_ticks, b.ordinal
            ORDER BY p.start_time_ticks, b.ordinal;
            """, queryParameters).ToLookup(record => record.PointId);

        return (bucketCounts, explicitBounds);
    }

    private static ILookup<long, MetricsExemplar> MaterializeMetricExemplars(
        SqliteConnection connection,
        DynamicParameters queryParameters)
    {
        var records = connection.Query<MetricExemplarRecord>($"""
            WITH {SelectedMetricPointsCteSql}
            SELECT
                e.exemplar_id AS ExemplarId,
                p.start_time_ticks AS PointId,
                e.start_time_ticks AS StartTimeTicks,
                e.exemplar_value AS ExemplarValue,
                e.span_id AS SpanId,
                e.trace_id AS TraceId
            FROM telemetry_metric_exemplars e
            JOIN selected_metric_points p ON p.point_id = e.point_id
            ORDER BY p.start_time_ticks, e.exemplar_id;
            """, queryParameters).AsList();
        var attributes = connection.Query<OwnedAttributeRecord>($"""
            WITH {SelectedMetricPointsCteSql}
            SELECT a.exemplar_id AS OwnerId, a.attribute_key AS AttributeKey, a.attribute_value AS AttributeValue
            FROM telemetry_metric_exemplar_attributes a
            JOIN telemetry_metric_exemplars e ON e.exemplar_id = a.exemplar_id
            JOIN selected_metric_points p ON p.point_id = e.point_id
            ORDER BY a.exemplar_id, a.ordinal;
            """, queryParameters).ToLookup(record => record.OwnerId);
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
            ClearIngestionCaches();
        }
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
        public Dictionary<(long InstrumentId, long AttributeHash), List<MetricDimensionState>> Dimensions { get; } = [];
        public Dictionary<long, int> DimensionCounts { get; } = [];
        public HashSet<long> LoadedDimensionInstruments { get; } = [];
        public HashSet<MetricDimensionState> DimensionsToTrim { get; } = [];
        public List<PendingMetricDimension> PendingDimensions { get; } = [];
        public List<PendingMetricDimensionAttribute> PendingDimensionAttributes { get; } = [];

        public void Clear()
        {
            Dimensions.Clear();
            DimensionCounts.Clear();
            LoadedDimensionInstruments.Clear();
            DimensionsToTrim.Clear();
            PendingDimensions.Clear();
            PendingDimensionAttributes.Clear();
        }
    }

    private sealed record PendingMetricDimension(long InstrumentId, long AttributeHash, MetricDimensionState Dimension);

    private sealed class InsertedMetricDimensionRecord
    {
        public required long DimensionId { get; init; }
        public required long InstrumentId { get; init; }
        public required long AttributeHash { get; init; }
    }

    private sealed record PendingMetricDimensionAttribute(MetricDimensionState Dimension, int Ordinal, string Key, string Value);

    private sealed class MetricDimensionState
    {
        public long DimensionId { get; set; }
        public required KeyValuePair<string, string>[] Attributes { get; init; }
        public MetricPointRecord? LatestPoint { get; set; }
        public PendingMetricPoint? PendingPoint { get; set; }
    }

    private sealed class MetricPointBatch
    {
        public Dictionary<long, MetricPointUpdate> Updates { get; } = [];
        public List<PendingMetricPoint> Inserts { get; } = [];
        public Dictionary<MetricExemplarKey, PendingMetricExemplar> Exemplars { get; } = [];

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

    private readonly record struct MetricExemplarKey(long PointId, long StartTimeTicks, double Value);

    private sealed class PendingMetricExemplar
    {
        public required long PointId { get; init; }
        public required long StartTimeTicks { get; init; }
        public required double Value { get; init; }
        public required string SpanId { get; init; }
        public required string TraceId { get; init; }
        public required KeyValuePair<string, string>[] Attributes { get; init; }
        public long? ExemplarId { get; set; }
    }

    private sealed record PendingMetricExemplarAttribute(long ExemplarId, int Ordinal, string Key, string Value);

    private sealed record PendingHistogramBucketCount(long PointId, int Ordinal, long BucketCount);

    private sealed record PendingHistogramExplicitBound(long PointId, int Ordinal, double ExplicitBound);

    private sealed class InsertedMetricExemplarRecord
    {
        public required long ExemplarId { get; init; }
        public required long PointId { get; init; }
        public required long StartTimeTicks { get; init; }
        public required double ExemplarValue { get; init; }
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
        public long? HistogramCount { get; init; }
        public required long Flags { get; init; }
        public long PointId { get; set; }
        public int SourcePointCount { get; set; } = 1;
        public long[]? HistogramBucketCounts { get; init; }
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
        public long? HistogramCount { get; init; }
    }

    private class MetricPointRecord
    {
        public required long PointId { get; init; }
        public required int PointType { get; init; }
        public required long EndTimeTicks { get; set; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public long? HistogramCount { get; init; }
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
        public required long BucketCount { get; init; }
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