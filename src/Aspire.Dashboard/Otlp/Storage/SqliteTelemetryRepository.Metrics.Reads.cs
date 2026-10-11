// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.Otlp.Storage;

public sealed partial class SqliteTelemetryRepository
{
    private const int MaxMetricReadBatchSize = 500;
    private const string MetricPointRangeFilterSql = "p.start_time_unix_nano <= @EndUnixNano AND p.end_time_unix_nano >= r.start_time_unix_nano";
    private const string MetricSourcePointRangeFilterSql = "source.start_time_unix_nano <= @EndUnixNano AND source.end_time_unix_nano >= r.start_time_unix_nano";
    private const string SelectedMetricPointsCteSql = $"""
        selected_metric_points AS (
            SELECT
                p.point_id,
                p.dimension_id,
                p.point_type,
                p.start_time_unix_nano,
                p.end_time_unix_nano,
                p.repeat_count,
                p.integer_value,
                p.double_value,
                p.histogram_sum,
                p.histogram_count,
                p.histogram_aggregation_start_unix_nano,
                p.histogram_aggregation_id,
                i.aggregation_temporality
            FROM telemetry_metric_points p
            JOIN telemetry_metric_dimensions d ON d.dimension_id = p.dimension_id
            JOIN telemetry_metric_instruments i ON i.instrument_id = d.instrument_id
            JOIN metric_dimension_query_ranges r ON r.dimension_id = p.dimension_id
            WHERE {MetricPointRangeFilterSql}
        )
        """;
    private const string EffectiveMetricPointsCteSql = $"""
        {SelectedMetricPointsCteSql},
        ranked_metric_points AS (
            SELECT
                p.*,
                ROW_NUMBER() OVER (
                    PARTITION BY p.start_time_unix_nano, p.dimension_id, p.histogram_aggregation_id
                    ORDER BY p.point_id DESC) AS point_rank
            FROM selected_metric_points p
        ),
        effective_metric_points AS (
            SELECT *
            FROM ranked_metric_points
            WHERE point_rank = 1
        )
        """;
    // An aggregation identity separates cumulative resets within one rollup. Delta points each
    // have their own identity and retain their interval start, so no delta observations are discarded.
    // Cumulative representatives start at their earliest source interval, not the rounded bucket
    // boundary: rounding a reset at 3.5s to 3s would lower the preceding window's count.
    private static readonly string s_rolledUpMetricPointsCteSql = $"""
        {EffectiveMetricPointsCteSql},
        bucketed_metric_points AS (
            SELECT
                p.*,
                (p.start_time_unix_nano / @PointIntervalUnixNano) * @PointIntervalUnixNano AS rollup_start_time_unix_nano
            FROM effective_metric_points p
        ),
        ranked_rollup_metric_points AS (
            SELECT
                p.*,
                MIN(p.start_time_unix_nano) OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_unix_nano, p.histogram_aggregation_id) AS rollup_source_start_time_unix_nano,
                MAX(p.end_time_unix_nano) OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_unix_nano, p.histogram_aggregation_id) AS rollup_end_time_unix_nano,
                SUM(p.repeat_count) OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_unix_nano, p.histogram_aggregation_id) AS rollup_repeat_count,
                ROW_NUMBER() OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_unix_nano, p.histogram_aggregation_id
                    ORDER BY
                        CASE WHEN p.point_type = {HistogramPointType} THEN p.start_time_unix_nano END DESC,
                        p.integer_value DESC,
                        p.double_value DESC,
                        p.point_id DESC) AS rollup_rank
            FROM bucketed_metric_points p
        ),
        rolled_up_metric_points AS (
            SELECT
                p.point_id,
                p.dimension_id,
                p.point_type,
                CASE
                    WHEN p.point_type = {HistogramPointType} AND p.aggregation_temporality = {(int)OtlpAggregationTemporality.Delta} THEN p.start_time_unix_nano
                    WHEN p.point_type = {HistogramPointType} THEN p.rollup_source_start_time_unix_nano
                    ELSE p.rollup_start_time_unix_nano
                END AS start_time_unix_nano,
                p.rollup_end_time_unix_nano AS end_time_unix_nano,
                p.rollup_repeat_count AS repeat_count,
                p.integer_value,
                p.double_value,
                p.histogram_sum,
                p.histogram_count,
                p.histogram_aggregation_start_unix_nano,
                p.histogram_aggregation_id,
                p.aggregation_temporality
            FROM ranked_rollup_metric_points p
            WHERE p.rollup_rank = 1
        )
        """;
    private const string FullFidelityMetricPointsCteSql = $"""
        {EffectiveMetricPointsCteSql},
        rolled_up_metric_points AS (
            SELECT
                p.point_id,
                p.dimension_id,
                p.point_type,
                p.start_time_unix_nano,
                p.end_time_unix_nano,
                p.repeat_count,
                p.integer_value,
                p.double_value,
                p.histogram_sum,
                p.histogram_count,
                p.histogram_aggregation_start_unix_nano,
                p.histogram_aggregation_id,
                p.aggregation_temporality
            FROM effective_metric_points p
        )
        """;

    private OtlpInstrumentData? GetInstrumentFromDatabase(GetInstrumentRequest request, CancellationToken cancellationToken)
    {
        var instruments = GetCachedInstruments(request.ResourceKey, request.MeterName, request.InstrumentName);
        if (instruments.Count == 0)
        {
            return null;
        }

        using var connection = _database.OpenConnection();
        using var interrupt = connection.RegisterInterrupt(cancellationToken);
        // Keep dimensions, points, exemplars, and their attributes in one snapshot
        // so point trimming cannot change the result between queries.
        using var transaction = connection.BeginTransaction(deferred: true);
        var knownAttributeValues = new Dictionary<string, List<string?>>();
        var dimensions = MaterializeMetricDimensions(
            connection,
            transaction,
            instruments,
            request.StartTime,
            request.EndTime,
            request.DimensionFilters,
            request.DimensionCursors,
            request.DataPointInterval,
            request.IncludeExemplars,
            request.PopulateExemplarAttributes,
            knownAttributeValues,
            out var hasOverflow);
        return new OtlpInstrumentData
        {
            Summary = instruments[0].Summary,
            Dimensions = dimensions,
            KnownAttributeValues = knownAttributeValues,
            HasOverflow = hasOverflow
        };
    }

    private DateTime? GetInstrumentLatestEndTimeFromDatabase(ResourceKey resourceKey, string meterName, string instrumentName)
    {
        using var connection = _database.OpenConnection();
        var endTimeUnixNano = connection.QuerySingleOrDefault<long?>("""
            SELECT MAX(p.end_time_unix_nano)
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
        return endTimeUnixNano is not null ? OtlpHelpers.UnixNanoSecondsToDateTime(checked((ulong)endTimeUnixNano.Value)) : null;
    }

    private List<DimensionScope> MaterializeMetricDimensions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CachedInstrument> instruments,
        DateTime? startTime,
        DateTime? endTime,
        IReadOnlyDictionary<string, IReadOnlyList<string?>> dimensionFilters,
        IReadOnlyList<MetricDimensionCursor> dimensionCursors,
        TimeSpan? dataPointInterval,
        bool includeExemplars,
        bool populateExemplarAttributes,
        Dictionary<string, List<string?>> knownAttributeValues,
        out bool hasOverflow)
    {
        if (dataPointInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dataPointInterval), interval, "The metric data point interval must be greater than zero.");
        }
        if (dataPointInterval is { } largeInterval && largeInterval.Ticks > long.MaxValue / TimeSpan.NanosecondsPerTick)
        {
            throw new ArgumentOutOfRangeException(nameof(dataPointInterval), largeInterval, "The metric data point interval must fit in signed nanoseconds.");
        }

        var dimensions = GetMetricDimensions(
            connection,
            transaction,
            instruments,
            dimensionFilters,
            knownAttributeValues,
            out hasOverflow);
        if (dimensions.Count == 0)
        {
            return [];
        }

        var results = dimensions
            .Select(dimension => dimension.Scope)
            .ToList();
        if (startTime is null || endTime is null)
        {
            return results;
        }

        var queryParameters = new DynamicParameters();
        queryParameters.Add("EndUnixNano", GetMetricQueryTimeUnixNano(endTime.Value, upperBound: true));
        queryParameters.Add("PointIntervalUnixNano", dataPointInterval is { } pointInterval ? pointInterval.Ticks * TimeSpan.NanosecondsPerTick : 0);
        var dimensionQueryRangesCteSql = CreateMetricDimensionQueryRangesCte(
            dimensions,
            dimensionCursors,
            startTime,
            queryParameters);
        var metricPointsCteSql = dataPointInterval is null ? FullFidelityMetricPointsCteSql : s_rolledUpMetricPointsCteSql;
        var pointRecords = connection.Query<MetricPointDataRecord>($"""
            WITH {dimensionQueryRangesCteSql},
            {metricPointsCteSql}
            SELECT
                p.point_id AS PointId,
                p.dimension_id AS DimensionId,
                p.point_type AS PointType,
                p.start_time_unix_nano AS StartTimeUnixNano,
                p.end_time_unix_nano AS EndTimeUnixNano,
                p.repeat_count AS RepeatCount,
                p.integer_value AS IntegerValue,
                p.double_value AS DoubleValue,
                p.histogram_sum AS HistogramSum,
                p.histogram_count AS HistogramCount,
                p.histogram_aggregation_start_unix_nano AS HistogramAggregationStartUnixNano,
                p.histogram_aggregation_id AS HistogramAggregationId,
                p.aggregation_temporality AS AggregationTemporality,
                stored.start_time_unix_nano AS SourceStartTimeUnixNano,
                stored.bucket_counts AS BucketCounts,
                stored.explicit_bounds AS ExplicitBounds
            FROM rolled_up_metric_points p
            JOIN telemetry_metric_points stored ON stored.point_id = p.point_id
            ORDER BY p.dimension_id, p.start_time_unix_nano, p.point_id;
            """, queryParameters, transaction).AsList();
        var points = pointRecords.ToLookup(record => record.DimensionId);
        var exemplars = includeExemplars
            ? MaterializeMetricExemplars(
                connection,
                transaction,
                dimensionQueryRangesCteSql,
                queryParameters,
                dataPointInterval,
                pointRecords,
                populateExemplarAttributes)
            : Array.Empty<KeyValuePair<long, MetricsExemplar>>().ToLookup(pair => pair.Key, pair => pair.Value);

        for (var dimensionIndex = 0; dimensionIndex < dimensions.Count; dimensionIndex++)
        {
            var dimensionId = dimensions[dimensionIndex].DimensionId;
            var dimension = results[dimensionIndex];
            foreach (var point in points[dimensionId])
            {
                MetricValueBase value = point.PointType switch
                {
                    LongPointType => new MetricValue<long>(point.IntegerValue!.Value, checked((ulong)point.StartTimeUnixNano), checked((ulong)point.EndTimeUnixNano)),
                    DoublePointType => new MetricValue<double>(point.DoubleValue!.Value, checked((ulong)point.StartTimeUnixNano), checked((ulong)point.EndTimeUnixNano)),
                    HistogramPointType => CreateHistogramValue(point),
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
        return results;
    }

    private List<StoredMetricDimension> GetMetricDimensions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CachedInstrument> instruments,
        IReadOnlyDictionary<string, IReadOnlyList<string?>> dimensionFilters,
        Dictionary<string, List<string?>> knownAttributeValues,
        out bool hasOverflow)
    {
        var dimensionRecords = connection.Query<MetricDimensionAttributeRecord>("""
            SELECT
                d.dimension_id AS DimensionId,
                d.instrument_id AS InstrumentId,
                a.attribute_key AS AttributeKey,
                a.attribute_value AS AttributeValue
            FROM telemetry_metric_dimensions d
            LEFT JOIN telemetry_metric_dimension_attributes a ON a.dimension_id = d.dimension_id
            WHERE d.instrument_id IN @InstrumentIds
            ORDER BY d.dimension_id, a.ordinal;
            """, new { InstrumentIds = instruments.Select(instrument => instrument.InstrumentId) }, transaction).AsList();
        var dimensionIds = dimensionRecords.Select(record => record.DimensionId).Distinct().ToArray();
        var pointAttributes = dimensionRecords
            .Where(record => record.AttributeKey is not null)
            .Select(record => new OwnedAttributeRecord
            {
                OwnerId = record.DimensionId,
                AttributeKey = record.AttributeKey!,
                AttributeValue = record.AttributeValue!
            })
            .ToLookup(record => record.OwnerId);
        hasOverflow = dimensionIds.Any(dimensionId => IsOverflowDimension(pointAttributes[dimensionId]));
        var instrumentsById = instruments.ToDictionary(instrument => instrument.InstrumentId);
        var instrumentIdsByDimensionId = dimensionRecords
            .DistinctBy(record => record.DimensionId)
            .ToDictionary(record => record.DimensionId, record => record.InstrumentId);
        // Dimension rows store only point attributes. Scope attributes are stored once with the scope and merged here for display and filtering.
        var attributes = dimensionIds
            .SelectMany(dimensionId => pointAttributes[dimensionId]
                .Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue))
                .Concat(instrumentsById[instrumentIdsByDimensionId[dimensionId]].Summary.Parent.Attributes)
                .Select(attribute => new OwnedAttributeRecord
                {
                    OwnerId = dimensionId,
                    AttributeKey = attribute.Key,
                    AttributeValue = attribute.Value
                }))
            .ToLookup(record => record.OwnerId);
        PopulateKnownAttributeValues(dimensionIds, attributes, knownAttributeValues);

        return dimensionIds
            .Where(dimensionId => MatchesDimensionFilters(attributes[dimensionId], dimensionFilters))
            .Select(dimensionId => new StoredMetricDimension(
                dimensionId,
                new DimensionScope(
                    _otlpContext.Options.MaxMetricsCount,
                    attributes[dimensionId].Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray())))
            .ToList();
    }

    private static bool IsOverflowDimension(IEnumerable<OwnedAttributeRecord> attributes)
    {
        using var enumerator = attributes.GetEnumerator();
        return enumerator.MoveNext() &&
            enumerator.Current is { AttributeKey: "otel.metric.overflow", AttributeValue: "true" } &&
            !enumerator.MoveNext();
    }

    private static string CreateMetricDimensionQueryRangesCte(
        IReadOnlyList<StoredMetricDimension> dimensions,
        IReadOnlyList<MetricDimensionCursor> dimensionCursors,
        DateTime? defaultStartTime,
        DynamicParameters queryParameters)
    {
        var sql = new StringBuilder("metric_dimension_query_ranges(dimension_id, start_time_unix_nano) AS (VALUES ");
        for (var i = 0; i < dimensions.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            var dimension = dimensions[i];
            var cursor = dimensionCursors.FirstOrDefault(cursor =>
                cursor.Attributes.SequenceEqual(dimension.Scope.Attributes));
            queryParameters.Add($"DimensionId{i}", dimension.DimensionId);
            queryParameters.Add($"DimensionStartUnixNano{i}", GetMetricQueryTimeUnixNano(cursor?.StartTime ?? defaultStartTime ?? DateTime.UnixEpoch, upperBound: false));
            sql.Append(CultureInfo.InvariantCulture, $"(@DimensionId{i}, @DimensionStartUnixNano{i})");
        }
        sql.Append(')');
        return sql.ToString();
    }

    private static long? GetMetricQueryTimeUnixNano(DateTime time, bool upperBound)
    {
        var ticks = time.Ticks - DateTime.UnixEpoch.Ticks;
        if (ticks < 0)
        {
            return upperBound ? null : 0;
        }
        if (ticks > long.MaxValue / TimeSpan.NanosecondsPerTick)
        {
            return upperBound ? long.MaxValue : null;
        }

        var nanoseconds = ticks * TimeSpan.NanosecondsPerTick;
        // DateTime filters include their entire display tick. Otherwise a read-only query ending at
        // the latest displayed time could exclude a point 20 ns into that tick. Min/MaxValue remain
        // usable as unbounded filters; bounds wholly outside the supported range match no points.
        const long tickRemainder = TimeSpan.NanosecondsPerTick - 1;
        return upperBound ? Math.Min(nanoseconds, long.MaxValue - tickRemainder) + tickRemainder : nanoseconds;
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
        // Point and scope attributes were already accepted during ingestion, so intentionally do not limit their
        // merged key or per-key value counts while building display metadata.
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
                        TryAddValue(values, null);
                    }
                }
                var value = attributes[dimensionId].FirstOrDefault(attribute => attribute.AttributeKey == key)?.AttributeValue;
                TryAddValue(values, value);
            }
        }

        static void TryAddValue(List<string?> values, string? value)
        {
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }
    }

    private static HistogramValue CreateHistogramValue(MetricPointDataRecord point) => new(
        UnpackUInt64Values(point.BucketCounts!),
        point.HistogramSum!.Value,
        checked((ulong)point.HistogramCount!.Value),
        checked((ulong)point.StartTimeUnixNano),
        checked((ulong)point.EndTimeUnixNano),
        UnpackDoubleValues(point.ExplicitBounds!),
        checked((ulong)point.HistogramAggregationStartUnixNano!.Value),
        point.HistogramAggregationId!.Value,
        (OtlpAggregationTemporality)point.AggregationTemporality);

    private static ILookup<long, MetricsExemplar> MaterializeMetricExemplars(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dimensionQueryRangesCteSql,
        DynamicParameters queryParameters,
        TimeSpan? dataPointInterval,
        IReadOnlyList<MetricPointDataRecord> points,
        bool populateExemplarAttributes)
    {
        var records = connection.Query<MetricExemplarRecord>($"""
            WITH {dimensionQueryRangesCteSql}
            SELECT
                e.exemplar_id AS ExemplarId,
                source.dimension_id AS DimensionId,
                source.point_type AS PointType,
                source.histogram_aggregation_id AS HistogramAggregationId,
                source.start_time_unix_nano AS SourceStartTimeUnixNano,
                e.time_unix_nano AS TimeUnixNano,
                e.exemplar_value AS ExemplarValue,
                e.span_id AS SpanId,
                e.trace_id AS TraceId
            FROM telemetry_metric_exemplars e
            JOIN telemetry_metric_points source ON source.point_id = e.point_id
            JOIN metric_dimension_query_ranges r ON r.dimension_id = source.dimension_id
            WHERE {MetricSourcePointRangeFilterSql}
              AND e.time_unix_nano >= r.start_time_unix_nano
              AND e.time_unix_nano <= @EndUnixNano
            ORDER BY source.dimension_id, source.start_time_unix_nano, e.exemplar_id;
            """, queryParameters, transaction).AsList();
        var pointIntervalUnixNano = dataPointInterval is { } interval ? interval.Ticks * TimeSpan.NanosecondsPerTick : (long?)null;
        // Match the SQL partition using source timestamps, independently of the displayed start.
        // A cumulative reset's representative can start at 3.5s while its SQL group starts at 3s.
        var pointIds = points.ToDictionary(
            point => GetPointKey(point.DimensionId, point.PointType, point.SourceStartTimeUnixNano, point.HistogramAggregationId),
            point => point.PointId);
        var mappedRecords = new List<(long PointId, MetricExemplarRecord Record)>();
        foreach (var record in records)
        {
            if (pointIds.TryGetValue(GetPointKey(record.DimensionId, record.PointType, record.SourceStartTimeUnixNano,
                record.HistogramAggregationId), out var pointId))
            {
                mappedRecords.Add((pointId, record));
            }
        }
        var attributes = populateExemplarAttributes
            ? MaterializeMetricExemplarAttributes(connection, transaction, mappedRecords.Select(item => item.Record.ExemplarId).Distinct().ToArray())
            : Array.Empty<OwnedAttributeRecord>().ToLookup(record => record.OwnerId);
        return mappedRecords
            .OrderBy(item => item.PointId)
            .ThenBy(item => item.Record.ExemplarId)
            .Select(item => new KeyValuePair<long, MetricsExemplar>(
                item.PointId,
                new MetricsExemplar
                {
                    TimeUnixNano = checked((ulong)item.Record.TimeUnixNano),
                    Value = item.Record.ExemplarValue,
                    SpanId = item.Record.SpanId,
                    TraceId = item.Record.TraceId,
                    Attributes = attributes[item.Record.ExemplarId].Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue)).ToArray()
                }))
            .ToLookup(pair => pair.Key, pair => pair.Value);

        MetricPointKey GetPointKey(long dimensionId, int pointType, long sourceStartTimeUnixNano, long? aggregationId)
        {
            var groupStartTimeUnixNano = pointIntervalUnixNano is { } intervalUnixNano
                ? (sourceStartTimeUnixNano / intervalUnixNano) * intervalUnixNano
                : sourceStartTimeUnixNano;
            return new MetricPointKey(dimensionId, pointType, groupStartTimeUnixNano, aggregationId);
        }
    }

    private static ILookup<long, OwnedAttributeRecord> MaterializeMetricExemplarAttributes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> exemplarIds)
    {
        var attributes = new List<OwnedAttributeRecord>();
        foreach (var exemplarIdBatch in exemplarIds.Chunk(MaxMetricReadBatchSize))
        {
            attributes.AddRange(connection.Query<OwnedAttributeRecord>("""
                SELECT exemplar_id AS OwnerId, attribute_key AS AttributeKey, attribute_value AS AttributeValue
                FROM telemetry_metric_exemplar_attributes
                WHERE exemplar_id IN @ExemplarIds
                ORDER BY exemplar_id, ordinal;
                """, new { ExemplarIds = exemplarIdBatch }, transaction));
        }
        return attributes.ToLookup(record => record.OwnerId);
    }

    internal sealed class MetricPointDataRecord : MetricPointRecord
    {
        public required long DimensionId { get; init; }
        public required long StartTimeUnixNano { get; init; }
        public required long SourceStartTimeUnixNano { get; init; }
        public required long RepeatCount { get; init; }
        public double? HistogramSum { get; init; }
        public long? HistogramAggregationStartUnixNano { get; init; }
        public long? HistogramAggregationId { get; init; }
        public int AggregationTemporality { get; init; }
        public byte[]? BucketCounts { get; init; }
        public byte[]? ExplicitBounds { get; init; }
    }

    internal sealed class MetricDimensionAttributeRecord
    {
        public required long DimensionId { get; init; }
        public required long InstrumentId { get; init; }
        public string? AttributeKey { get; init; }
        public string? AttributeValue { get; init; }
    }

    internal sealed class MetricExemplarRecord
    {
        public required long ExemplarId { get; init; }
        public required long DimensionId { get; init; }
        public required int PointType { get; init; }
        public long? HistogramAggregationId { get; init; }
        public required long SourceStartTimeUnixNano { get; init; }
        public required long TimeUnixNano { get; init; }
        public required double ExemplarValue { get; init; }
        public required string SpanId { get; init; }
        public required string TraceId { get; init; }
    }

    private sealed record StoredMetricDimension(long DimensionId, DimensionScope Scope);

    private readonly record struct MetricPointKey(long DimensionId, int PointType, long StartTimeUnixNano, long? HistogramAggregationId);
}