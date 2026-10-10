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

    private readonly MetricIngestionState _metricIngestionState = new();

    private async Task AddMetricsToDatabaseAsync(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
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
                    CachedResourceView cachedView;
                    try
                    {
                        cachedResource = GetOrAddCachedResource(connection, transaction, resourceMetricsItem.Resource.GetResourceKey());
                        cachedView = GetOrAddCachedResourceView(connection, transaction, cachedResource, resourceMetricsItem.Resource.Attributes);
                    }
                    catch (Exception exception)
                    {
                        context.FailureCount += resourceMetricsItem.ScopeMetrics.Sum(scope => scope.Metrics.Sum(OtlpHelpers.GetMetricDataPointCount));
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
                            context.FailureCount += scopeMetrics.Metrics.Sum(OtlpHelpers.GetMetricDataPointCount);
                            _otlpContext.Logger.LogInformation(exception, "Error adding metric scope.");
                            continue;
                        }

                        var validatedMetrics = new List<Metric>(scopeMetrics.Metrics.Count);
                        foreach (var metric in scopeMetrics.Metrics)
                        {
                            var aggregationTemporality = MapAggregationTemporality(metric);
                            if (!IsSupportedAggregationTemporality(aggregationTemporality))
                            {
                                context.FailureCount += OtlpHelpers.GetMetricDataPointCount(metric);
                                _otlpContext.Logger.LogInformation(
                                    "Error adding metric instrument {MetricName}. Aggregation temporality {AggregationTemporality} is not supported.",
                                    metric.Name,
                                    (int)aggregationTemporality);
                                continue;
                            }

                            validatedMetrics.Add(metric);
                        }

                        EnsureCachedInstruments(connection, transaction, cachedResource, cachedView, cachedScope, validatedMetrics);
                        foreach (var metric in validatedMetrics)
                        {
                            AddMetricToDatabase(connection, transaction, context, cachedResource, cachedView, cachedScope, metric, _metricIngestionState, pointBatch);
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
        CachedResourceView cachedView,
        CachedResourceScope cachedScope,
        Metric metric,
        MetricIngestionState ingestionState,
        MetricPointBatch pointBatch)
    {
        var pointCount = OtlpHelpers.GetMetricDataPointCount(metric);
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
            cachedInstrument = GetOrAddCachedInstrument(connection, transaction, cachedResource, cachedView, cachedScope, metric);
            if (metric.DataCase == Metric.DataOneofCase.Histogram &&
                cachedInstrument.Summary.AggregationTemporality != MapAggregationTemporality(metric))
            {
                throw new InvalidOperationException("Histogram aggregation temporality changed.");
            }
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
                    AddNumberMetricPoint(connection, transaction, context, cachedInstrument.InstrumentId, point, ingestionState, pointBatch);
                }
                break;
            case Metric.DataOneofCase.Sum:
                foreach (var point in metric.Sum.DataPoints)
                {
                    AddNumberMetricPoint(connection, transaction, context, cachedInstrument.InstrumentId, point, ingestionState, pointBatch);
                }
                break;
            case Metric.DataOneofCase.Histogram:
                foreach (var point in metric.Histogram.DataPoints)
                {
                    AddHistogramMetricPoint(connection, transaction, context, cachedInstrument.InstrumentId,
                        cachedInstrument.Summary.AggregationTemporality, point, ingestionState, pointBatch);
                }
                break;
        }
    }

    private void AddNumberMetricPoint(
        SqliteConnection connection,
        IDbTransaction transaction,
        AddContext context,
        long instrumentId,
        NumberDataPoint point,
        MetricIngestionState ingestionState,
        MetricPointBatch pointBatch)
    {
        try
        {
            OtlpHelpers.ValidateNumberDataPoint(point);
            var pointType = point.ValueCase switch
            {
                NumberDataPoint.ValueOneofCase.AsInt => LongPointType,
                NumberDataPoint.ValueOneofCase.AsDouble => DoublePointType,
                _ => throw new InvalidOperationException("Metric data point has no value.")
            };
            var dimension = GetOrAddMetricDimension(connection, transaction, instrumentId, point.Attributes, ingestionState);
            var pendingLatest = dimension.PendingPoint;
            var latest = dimension.LatestPoint;
            var latestPointType = pendingLatest?.PointType ?? latest?.PointType;
            var latestEndTimeUnixNano = pendingLatest?.EndTimeUnixNano ?? latest?.EndTimeUnixNano;
            var sameValue = latestPointType == pointType && (pendingLatest is not null
                ? pointType == LongPointType ? pendingLatest.IntegerValue == point.AsInt : pendingLatest.DoubleValue == point.AsDouble
                : pointType == LongPointType ? latest?.IntegerValue == point.AsInt : latest?.DoubleValue == point.AsDouble);
            var endTimeUnixNano = checked((long)point.TimeUnixNano);
            if (sameValue)
            {
                if (pendingLatest is not null)
                {
                    pendingLatest.EndTimeUnixNano = endTimeUnixNano;
                    pendingLatest.RepeatCount++;
                    pendingLatest.SourcePointCount++;
                    pendingLatest.Exemplars.AddRange(point.Exemplars);
                }
                else
                {
                    pointBatch.AddUpdate(latest!.PointId, endTimeUnixNano, incrementRepeatCount: true);
                    latest.EndTimeUnixNano = endTimeUnixNano;
                    QueueMetricExemplars(pointBatch, latest.PointId, point.Exemplars);
                    context.SuccessCount++;
                }
            }
            else
            {
                var start = checked((long)point.StartTimeUnixNano);
                if (latestPointType == pointType)
                {
                    start = latestEndTimeUnixNano!.Value;
                }
                var pendingPoint = new PendingMetricPoint
                {
                    Context = context,
                    Dimension = dimension,
                    PointType = pointType,
                    StartTimeUnixNano = start,
                    EndTimeUnixNano = endTimeUnixNano,
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
        OtlpAggregationTemporality temporality,
        HistogramDataPoint point,
        MetricIngestionState ingestionState,
        MetricPointBatch pointBatch)
    {
        try
        {
            OtlpHelpers.ValidateHistogramDataPoint(point);
            var dimension = GetOrAddMetricDimension(connection, transaction, instrumentId, point.Attributes, ingestionState);
            var pendingLatest = dimension.PendingPoint;
            var latest = dimension.LatestPoint;
            var previousHistogram = pendingLatest?.Histogram ?? latest?.Histogram;
            var histogramCount = checked((long)point.Count);
            var histogram = HistogramValue.Create(point, temporality, previousHistogram);
            var endTimeUnixNano = checked((long)histogram.EndTimeUnixNano);
            if (ReferenceEquals(histogram, previousHistogram))
            {
                if (pendingLatest is not null)
                {
                    pendingLatest.EndTimeUnixNano = endTimeUnixNano;
                    pendingLatest.SourcePointCount++;
                    pendingLatest.Exemplars.AddRange(point.Exemplars);
                }
                else
                {
                    pointBatch.AddUpdate(latest!.PointId, endTimeUnixNano, incrementRepeatCount: false);
                    latest.EndTimeUnixNano = endTimeUnixNano;
                    QueueMetricExemplars(pointBatch, latest.PointId, point.Exemplars);
                    context.SuccessCount++;
                }
            }
            else
            {
                // Reject an unsupported bucket individually before queuing it, rather than failing
                // the entire batch when packing its SQLite blob.
                foreach (var count in histogram.Values)
                {
                    _ = checked((long)count);
                }
                var pendingPoint = new PendingMetricPoint
                {
                    Context = context,
                    Dimension = dimension,
                    PointType = HistogramPointType,
                    StartTimeUnixNano = checked((long)histogram.StartTimeUnixNano),
                    EndTimeUnixNano = endTimeUnixNano,
                    RepeatCount = 1,
                    HistogramSum = histogram.Sum,
                    HistogramCount = histogramCount,
                    Histogram = histogram,
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

    private void ExecuteMetricPointBatch(SqliteConnection connection, IDbTransaction transaction, MetricPointBatch pointBatch)
    {
        foreach (var updates in pointBatch.Updates.Values.Chunk(MaxMetricPointBatchSize))
        {
            var sql = new StringBuilder("""
                WITH updates(point_id, end_time_unix_nano, repeat_delta) AS (
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
                sql.Append(CultureInfo.InvariantCulture, $"        (@PointId{index}, @EndTimeUnixNano{index}, @RepeatDelta{index})");
                parameters.Add($"PointId{index}", update.PointId);
                parameters.Add($"EndTimeUnixNano{index}", update.EndTimeUnixNano);
                parameters.Add($"RepeatDelta{index}", update.RepeatDelta);
                index++;
            }
            sql.AppendLine();
            sql.Append("""
                )
                UPDATE telemetry_metric_points AS points
                SET end_time_unix_nano = updates.end_time_unix_nano,
                    repeat_count = points.repeat_count + updates.repeat_delta
                FROM updates
                WHERE points.point_id = updates.point_id;
                """);
            connection.Execute(sql.ToString(), parameters, transaction);
        }

        string[] columns =
        [
            "dimension_id", "point_type", "start_time_unix_nano", "end_time_unix_nano", "repeat_count",
            "integer_value", "double_value", "histogram_sum", "histogram_count", "bucket_counts", "explicit_bounds", "flags",
            "histogram_aggregation_start_unix_nano", "histogram_aggregation_id"
        ];
        BindRowParameters<PendingMetricPoint> bindParameters = static (point, parameters) =>
        {
            parameters[0].Value = point.Dimension.DimensionId;
            parameters[1].Value = point.PointType;
            parameters[2].Value = point.StartTimeUnixNano;
            parameters[3].Value = point.EndTimeUnixNano;
            parameters[4].Value = point.RepeatCount;
            parameters[5].Value = point.IntegerValue ?? (object)DBNull.Value;
            parameters[6].Value = point.DoubleValue ?? (object)DBNull.Value;
            parameters[7].Value = point.HistogramSum ?? (object)DBNull.Value;
            parameters[8].Value = point.HistogramCount ?? (object)DBNull.Value;
            parameters[9].Value = point.Histogram is not null ? PackUInt64Values(point.Histogram.Values) : DBNull.Value;
            parameters[10].Value = point.Histogram is not null ? PackDoubleValues(point.Histogram.ExplicitBounds) : DBNull.Value;
            parameters[11].Value = point.Flags;
            parameters[12].Value = point.Histogram is { } histogram ? checked((long)histogram.AggregationStartUnixNano) : DBNull.Value;
            parameters[13].Value = point.Histogram?.AggregationId ?? (object)DBNull.Value;
        };

        List<long> numberPointIds;
        Dictionary<MetricPointKey, long>? histogramPointIds = null;
        var previousMaxPointId = 0L;
        if (pointBatch.Inserts.Exists(static point => point.Histogram is not null))
        {
            // AUTOINCREMENT assigns every new row an ID above the existing maximum. UPSERT
            // preserves old IDs, so count each newly allocated ID once, including repeated inputs.
            previousMaxPointId = connection.QuerySingle<long>("SELECT COALESCE(MAX(point_id), 0) FROM telemetry_metric_points;", transaction: transaction);
            numberPointIds = [];
            var histogramIds = new Dictionary<MetricPointKey, long>();
            var returnedCount = 0;
            SqliteBatchInsert.BatchInsertRows(
                pointBatch.Inserts, MaxMetricPointBatchSize, columns.Length,
                rowCount => SqliteBatchInsert.CreateBatchInsertCommand(
                    connection, transaction, rowCount, "telemetry_metric_points", columns,
                    returningColumnName: "point_id, dimension_id, start_time_unix_nano, histogram_aggregation_id, point_type",
                    onConflictClause: $"""
                        ON CONFLICT(dimension_id, start_time_unix_nano, histogram_aggregation_id) WHERE point_type = {HistogramPointType}
                        DO UPDATE SET
                            end_time_unix_nano = excluded.end_time_unix_nano,
                            histogram_sum = excluded.histogram_sum,
                            histogram_count = excluded.histogram_count,
                            bucket_counts = excluded.bucket_counts,
                            explicit_bounds = excluded.explicit_bounds,
                            flags = excluded.flags,
                            histogram_aggregation_start_unix_nano = excluded.histogram_aggregation_start_unix_nano
                        """),
                bindParameters,
                command =>
                {
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var pointId = reader.GetInt64(0);
                        var pointType = reader.GetInt32(4);
                        if (pointType == HistogramPointType)
                        {
                            var key = new MetricPointKey(reader.GetInt64(1), pointType, reader.GetInt64(2), reader.GetInt64(3));
                            histogramIds[key] = pointId;
                        }
                        else
                        {
                            numberPointIds.Add(pointId);
                        }
                        returnedCount++;
                    }
                });
            if (returnedCount != pointBatch.Inserts.Count)
            {
                throw new InvalidOperationException($"The metric batch returned {returnedCount} rows; expected {pointBatch.Inserts.Count}.");
            }
            // Only numeric IDs are all newly generated and can be sorted into input order.
            // Histogram retries return old IDs and must be correlated by their logical key.
            numberPointIds.Sort();
            histogramPointIds = histogramIds;
        }
        else
        {
            numberPointIds = SqliteBatchInsert.BatchInsertRows(
                connection, transaction, pointBatch.Inserts, MaxMetricPointBatchSize,
                "telemetry_metric_points", columns, "point_id", bindParameters);
        }

        var numberIndex = 0;
        foreach (var point in pointBatch.Inserts)
        {
            point.PointId = histogramPointIds is not null && point.Histogram is { } histogram
                ? histogramPointIds[new MetricPointKey(point.Dimension.DimensionId, point.PointType, point.StartTimeUnixNano, histogram.AggregationId)]
                : numberPointIds[numberIndex++];
            QueueMetricExemplars(pointBatch, point.PointId, point.Exemplars);
        }
        InsertMetricExemplars(connection, transaction, pointBatch.Exemplars);

        var insertedIds = histogramPointIds is not null ? new HashSet<long>() : null;
        foreach (var point in pointBatch.Inserts)
        {
            point.Context.SuccessCount += point.SourcePointCount;
            if (insertedIds is null || (point.PointId > previousMaxPointId && insertedIds.Add(point.PointId)))
            {
                point.Dimension.PointCount++;
            }

            if (ReferenceEquals(point.Dimension.PendingPoint, point))
            {
                point.Dimension.LatestPoint = new MetricPointRecord
                {
                    PointId = point.PointId,
                    PointType = point.PointType,
                    EndTimeUnixNano = point.EndTimeUnixNano,
                    IntegerValue = point.IntegerValue,
                    DoubleValue = point.DoubleValue,
                    HistogramCount = point.HistogramCount,
                    Histogram = point.Histogram
                };
                point.Dimension.PendingPoint = null;
            }
        }
    }

    private MetricDimensionState GetOrAddMetricDimension(
        SqliteConnection connection,
        IDbTransaction transaction,
        long instrumentId,
        RepeatedField<KeyValue> pointAttributes,
        MetricIngestionState ingestionState)
    {
        var attributes = pointAttributes.ToKeyValuePairs(_otlpContext);
        Array.Sort(attributes, MetricAttributeComparer.Instance);
        var attributeHash = GetMetricDimensionAttributeHash(attributes);
        var cacheKey = (instrumentId, attributeHash);
        if (ingestionState.LoadedDimensionInstruments.Add(instrumentId))
        {
            var dimensions = connection.Query<MetricDimensionStateRecord>("""
                SELECT
                    d.dimension_id AS DimensionId,
                    (SELECT COUNT(*) FROM telemetry_metric_points WHERE dimension_id = d.dimension_id) AS PointCount,
                    a.attribute_key AS AttributeKey,
                    a.attribute_value AS AttributeValue,
                    p.point_id AS PointId,
                    p.point_type AS PointType,
                    p.start_time_unix_nano AS StartTimeUnixNano,
                    p.end_time_unix_nano AS EndTimeUnixNano,
                    p.integer_value AS IntegerValue,
                    p.double_value AS DoubleValue,
                    p.histogram_count AS HistogramCount,
                    p.histogram_sum AS HistogramSum,
                    p.histogram_aggregation_start_unix_nano AS HistogramAggregationStartUnixNano,
                    p.histogram_aggregation_id AS HistogramAggregationId,
                    p.bucket_counts AS HistogramBucketCounts,
                    p.explicit_bounds AS HistogramExplicitBounds,
                    i.aggregation_temporality AS AggregationTemporality
                FROM telemetry_metric_dimensions d
                JOIN telemetry_metric_instruments i ON i.instrument_id = d.instrument_id
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
                        PointCount = first.PointCount,
                        Attributes = group
                            .Where(record => record.AttributeKey is not null)
                            .Select(record => KeyValuePair.Create(record.AttributeKey!, record.AttributeValue!))
                            .ToArray(),
                        LatestPoint = first.PointId is not null
                            ? new MetricPointRecord
                            {
                                PointId = first.PointId.Value,
                                PointType = first.PointType!.Value,
                                EndTimeUnixNano = first.EndTimeUnixNano!.Value,
                                IntegerValue = first.IntegerValue,
                                DoubleValue = first.DoubleValue,
                                HistogramCount = first.HistogramCount,
                                Histogram = first.PointType == HistogramPointType
                                    ? new HistogramValue(
                                        UnpackUInt64Values(first.HistogramBucketCounts!), first.HistogramSum!.Value,
                                        checked((ulong)first.HistogramCount!.Value),
                                        checked((ulong)first.StartTimeUnixNano!.Value),
                                        checked((ulong)first.EndTimeUnixNano.Value),
                                        UnpackDoubleValues(first.HistogramExplicitBounds!),
                                        checked((ulong)first.HistogramAggregationStartUnixNano!.Value),
                                        first.HistogramAggregationId!.Value,
                                        (OtlpAggregationTemporality)first.AggregationTemporality)
                                    : null
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
            var loadedKnownAttributeValues = new KnownAttributeValuesState();
            foreach (var loadedDimension in dimensions)
            {
                loadedKnownAttributeValues.LoadDimension(loadedDimension.Attributes);
            }
            ingestionState.KnownAttributeValues.Add(instrumentId, loadedKnownAttributeValues);
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

        var knownAttributeValues = ingestionState.KnownAttributeValues[instrumentId];
        knownAttributeValues.ValidateDimension(attributes);
        var dimensionCount = ingestionState.DimensionCounts[instrumentId];
        if (dimensionCount >= TelemetryRepositoryLimits.MaxDimensionCount)
        {
            throw new InvalidOperationException($"Dimension limit of {TelemetryRepositoryLimits.MaxDimensionCount} reached.");
        }
        knownAttributeValues.AddDimension(attributes);
        var dimension = new MetricDimensionState { Attributes = attributes };
        ingestionState.PendingDimensions.Add(new PendingMetricDimension(instrumentId, attributeHash, dimension));
        ingestionState.PendingDimensionAttributes.AddRange(attributes.Select((attribute, ordinal) => new PendingMetricDimensionAttribute(
            dimension,
            ordinal,
            attribute.Key,
            attribute.Value)));

        candidates.Add(dimension);
        ingestionState.DimensionCounts[instrumentId] = dimensionCount + 1;
        return dimension;
    }

    private static void InsertMetricDimensions(
        SqliteConnection connection,
        IDbTransaction transaction,
        List<PendingMetricDimension> dimensions)
    {
        var dimensionIds = SqliteBatchInsert.BatchInsertRows(
            connection,
            transaction,
            dimensions,
            MaxMetricPointBatchSize,
            "telemetry_metric_dimensions",
            ["instrument_id", "attribute_hash"],
            "dimension_id",
            static (dimension, parameters) =>
            {
                parameters[0].Value = dimension.InstrumentId;
                parameters[1].Value = dimension.AttributeHash;
            });
        for (var i = 0; i < dimensions.Count; i++)
        {
            dimensions[i].Dimension.DimensionId = dimensionIds[i];
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

    private static byte[] PackUInt64Values(ReadOnlySpan<ulong> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(ulong))];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * sizeof(ulong)), values[i]);
        }
        return bytes;
    }

    private static byte[] PackDoubleValues(ReadOnlySpan<double> values)
    {
        var bytes = new byte[checked(values.Length * sizeof(double))];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * sizeof(double)), BitConverter.DoubleToInt64Bits(values[i]));
        }
        return bytes;
    }

    private static ulong[] UnpackUInt64Values(ReadOnlySpan<byte> bytes)
    {
        ValidatePackedValueLength(bytes);
        var values = new ulong[bytes.Length / sizeof(long)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = checked((ulong)BinaryPrimitives.ReadInt64LittleEndian(bytes[(i * sizeof(long))..]));
        }
        return values;
    }

    private static double[] UnpackDoubleValues(ReadOnlySpan<byte> bytes)
    {
        ValidatePackedValueLength(bytes);
        var values = new double[bytes.Length / sizeof(double)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes[(i * sizeof(double))..]));
        }
        return values;
    }

    private static void ValidatePackedValueLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(long) != 0)
        {
            throw new InvalidOperationException("Packed histogram data length must be a multiple of 8 bytes.");
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
            var value = exemplar.HasAsDouble ? exemplar.AsDouble : exemplar.AsInt;
            if (!double.IsFinite(value) || !OtlpHelpers.TryValidateMetricExemplarTimestamp(exemplar, _otlpContext))
            {
                continue;
            }
            var timeUnixNano = checked((long)exemplar.TimeUnixNano);
            pointBatch.Exemplars.TryAdd(
                new MetricExemplarKey(pointId, timeUnixNano, value),
                new PendingMetricExemplar
                {
                    PointId = pointId,
                    TimeUnixNano = timeUnixNano,
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
                    point_id, time_unix_nano, exemplar_value, span_id, trace_id)
                VALUES
                """);
            var parameters = new DynamicParameters();
            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.AppendLine(",");
                }
                sql.Append(CultureInfo.InvariantCulture, $"    (@PointId{index}, @TimeUnixNano{index}, @Value{index}, @SpanId{index}, @TraceId{index})");
                parameters.Add($"PointId{index}", batch[index].PointId);
                parameters.Add($"TimeUnixNano{index}", batch[index].TimeUnixNano);
                parameters.Add($"Value{index}", batch[index].Value);
                parameters.Add($"SpanId{index}", batch[index].SpanId);
                parameters.Add($"TraceId{index}", batch[index].TraceId);
            }
            sql.Append("""
                RETURNING
                    exemplar_id AS ExemplarId,
                    point_id AS PointId,
                    time_unix_nano AS TimeUnixNano,
                    exemplar_value AS ExemplarValue;
                """);
            foreach (var inserted in connection.Query<InsertedMetricExemplarRecord>(sql.ToString(), parameters, transaction))
            {
                exemplars[new MetricExemplarKey(inserted.PointId, inserted.TimeUnixNano, inserted.ExemplarValue)].ExemplarId = inserted.ExemplarId;
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
        foreach (var dimension in dimensions)
        {
            var removeCount = dimension.PointCount - _otlpContext.Options.MaxMetricsCount;
            if (removeCount <= 0)
            {
                continue;
            }

            // Count cached points instead of ranking the entire history on every insert. The dimension-order
            // index lets SQLite visit only the oldest surplus points, even after the retention limit is reached.
            connection.Execute("""
                DELETE FROM telemetry_metric_points
                WHERE point_id IN (
                    SELECT point_id
                    FROM telemetry_metric_points
                    WHERE dimension_id = @DimensionId
                    ORDER BY point_id
                    LIMIT @RemoveCount
                );
                """, new { dimension.DimensionId, RemoveCount = removeCount }, transaction);
            dimension.PointCount -= removeCount;
        }
    }

    private async Task ClearSelectedMetricsFromDatabaseAsync(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        using var connection = _database.OpenConnection();
        foreach (var resource in connection.Query<TelemetryResourceRecord>("SELECT resource_name AS ResourceName, instance_id AS InstanceId FROM telemetry_resources;"))
        {
            var key = new ResourceKey(resource.ResourceName, resource.InstanceId);
            if (selectedResources.TryGetValue(key.GetCompositeName(), out var dataTypes) && dataTypes.Contains(AspireDataType.Metrics) && !dataTypes.Contains(AspireDataType.Resource))
            {
                await ClearMetricsFromDatabaseAsync(key).ConfigureAwait(false);
            }
        }
    }

    private async Task ClearMetricsFromDatabaseAsync(ResourceKey? resourceKey)
    {
        using (await _database.WriteLock.LockAsync().ConfigureAwait(false))
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

    private static bool IsSupportedAggregationTemporality(OtlpAggregationTemporality aggregationTemporality)
    {
        return aggregationTemporality is
            OtlpAggregationTemporality.Unspecified or
            OtlpAggregationTemporality.Delta or
            OtlpAggregationTemporality.Cumulative;
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
        public Dictionary<long, KnownAttributeValuesState> KnownAttributeValues { get; } = [];
        public HashSet<long> LoadedDimensionInstruments { get; } = [];
        public HashSet<MetricDimensionState> DimensionsToTrim { get; } = [];
        public List<PendingMetricDimension> PendingDimensions { get; } = [];
        public List<PendingMetricDimensionAttribute> PendingDimensionAttributes { get; } = [];

        public void Clear()
        {
            Dimensions.Clear();
            DimensionCounts.Clear();
            KnownAttributeValues.Clear();
            LoadedDimensionInstruments.Clear();
            DimensionsToTrim.Clear();
            PendingDimensions.Clear();
            PendingDimensionAttributes.Clear();
        }
    }

    private sealed record PendingMetricDimension(long InstrumentId, long AttributeHash, MetricDimensionState Dimension);

    private sealed record PendingMetricDimensionAttribute(MetricDimensionState Dimension, int Ordinal, string Key, string Value);

    private sealed class MetricDimensionState
    {
        public long DimensionId { get; set; }
        public long PointCount { get; set; }
        public required KeyValuePair<string, string>[] Attributes { get; init; }
        public MetricPointRecord? LatestPoint { get; set; }
        public PendingMetricPoint? PendingPoint { get; set; }
    }

    private sealed class MetricPointBatch
    {
        public Dictionary<long, MetricPointUpdate> Updates { get; } = [];
        public List<PendingMetricPoint> Inserts { get; } = [];
        public Dictionary<MetricExemplarKey, PendingMetricExemplar> Exemplars { get; } = [];

        public void AddUpdate(long pointId, long endTimeUnixNano, bool incrementRepeatCount)
        {
            if (!Updates.TryGetValue(pointId, out var update))
            {
                update = new MetricPointUpdate { PointId = pointId };
                Updates.Add(pointId, update);
            }
            update.EndTimeUnixNano = endTimeUnixNano;
            if (incrementRepeatCount)
            {
                update.RepeatDelta++;
            }
        }
    }

    private readonly record struct MetricExemplarKey(long PointId, long TimeUnixNano, double Value);

    private sealed class PendingMetricExemplar
    {
        public required long PointId { get; init; }
        public required long TimeUnixNano { get; init; }
        public required double Value { get; init; }
        public required string SpanId { get; init; }
        public required string TraceId { get; init; }
        public required KeyValuePair<string, string>[] Attributes { get; init; }
        public long? ExemplarId { get; set; }
    }

    private sealed record PendingMetricExemplarAttribute(long ExemplarId, int Ordinal, string Key, string Value);

    internal sealed class InsertedMetricExemplarRecord
    {
        public required long ExemplarId { get; init; }
        public required long PointId { get; init; }
        public required long TimeUnixNano { get; init; }
        public required double ExemplarValue { get; init; }
    }

    private sealed class MetricPointUpdate
    {
        public required long PointId { get; init; }
        public long EndTimeUnixNano { get; set; }
        public long RepeatDelta { get; set; }
    }

    private sealed class PendingMetricPoint
    {
        public required AddContext Context { get; init; }
        public required MetricDimensionState Dimension { get; init; }
        public required int PointType { get; init; }
        public required long StartTimeUnixNano { get; init; }
        public required long EndTimeUnixNano { get; set; }
        public required long RepeatCount { get; set; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public double? HistogramSum { get; init; }
        public long? HistogramCount { get; init; }
        public HistogramValue? Histogram { get; init; }
        public required long Flags { get; init; }
        public long PointId { get; set; }
        public int SourcePointCount { get; set; } = 1;
        public List<Exemplar> Exemplars { get; } = [];
    }

    internal sealed class MetricDimensionStateRecord
    {
        public required long DimensionId { get; init; }
        public required long PointCount { get; init; }
        public string? AttributeKey { get; init; }
        public string? AttributeValue { get; init; }
        public long? PointId { get; init; }
        public int? PointType { get; init; }
        public long? StartTimeUnixNano { get; init; }
        public long? EndTimeUnixNano { get; init; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public long? HistogramCount { get; init; }
        public double? HistogramSum { get; init; }
        public long? HistogramAggregationStartUnixNano { get; init; }
        public long? HistogramAggregationId { get; init; }
        public int AggregationTemporality { get; init; }
        public byte[]? HistogramBucketCounts { get; init; }
        public byte[]? HistogramExplicitBounds { get; init; }
    }

    internal class MetricPointRecord
    {
        public required long PointId { get; init; }
        public required int PointType { get; init; }
        public required long EndTimeUnixNano { get; set; }
        public long? IntegerValue { get; init; }
        public double? DoubleValue { get; init; }
        public long? HistogramCount { get; init; }
        public HistogramValue? Histogram { get; init; }
    }
}