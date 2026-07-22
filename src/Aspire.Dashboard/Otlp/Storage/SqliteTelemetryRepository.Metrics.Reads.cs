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
    private const string MetricPointRangeFilterSql = "(@ApplyRange = 0 OR (p.start_time_ticks <= @EndTicks AND p.end_time_ticks >= r.start_time_ticks))";
    private const string SelectedMetricPointsCteSql = $"""
        selected_metric_points AS (
            SELECT p.*
            FROM telemetry_metric_points p
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
    private static readonly string s_rolledUpMetricPointsCteSql = $"""
        {EffectiveMetricPointsCteSql},
        bucketed_metric_points AS (
            SELECT
                p.*,
                CASE
                    WHEN @PointIntervalTicks = 0 THEN p.start_time_ticks
                    ELSE (p.start_time_ticks / @PointIntervalTicks) * @PointIntervalTicks
                END AS rollup_start_time_ticks
            FROM effective_metric_points p
        ),
        ranked_rollup_metric_points AS (
            SELECT
                p.*,
                MAX(p.end_time_ticks) OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_ticks) AS rollup_end_time_ticks,
                SUM(p.repeat_count) OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_ticks) AS rollup_repeat_count,
                ROW_NUMBER() OVER (
                    PARTITION BY p.dimension_id, p.point_type, p.rollup_start_time_ticks
                    ORDER BY
                        CASE WHEN p.point_type = {HistogramPointType} THEN p.start_time_ticks END DESC,
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
                p.rollup_start_time_ticks AS start_time_ticks,
                p.rollup_end_time_ticks AS end_time_ticks,
                p.rollup_repeat_count AS repeat_count,
                p.integer_value,
                p.double_value,
                p.histogram_sum,
                p.histogram_count,
                p.flags
            FROM ranked_rollup_metric_points p
            WHERE p.rollup_rank = 1
        )
        """;
    private const string FullFidelityMetricPointsCteSql = $"""
        {EffectiveMetricPointsCteSql},
        rolled_up_metric_points AS (
            SELECT *
            FROM effective_metric_points
        )
        """;

    private OtlpInstrumentData? GetInstrumentFromDatabase(GetInstrumentRequest request)
    {
        var instruments = GetCachedInstruments(request.ResourceKey, request.MeterName, request.InstrumentName);
        if (instruments.Count == 0)
        {
            return null;
        }

        using var connection = _database.OpenConnection();
        var knownAttributeValues = new Dictionary<string, List<string?>>();
        var dimensions = MaterializeMetricDimensions(
            connection,
            instruments.Select(instrument => instrument.InstrumentId).ToArray(),
            instruments[0].Summary.Type == OtlpInstrumentType.Histogram,
            request.StartTime,
            request.EndTime,
            request.DimensionFilters,
            request.DimensionCursors,
            request.DataPointInterval,
            knownAttributeValues);
        return new OtlpInstrumentData
        {
            Summary = instruments[0].Summary,
            Dimensions = dimensions,
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

    private List<DimensionScope> MaterializeMetricDimensions(
        SqliteConnection connection,
        IReadOnlyList<long> instrumentIds,
        bool isHistogram,
        DateTime? startTime,
        DateTime? endTime,
        IReadOnlyDictionary<string, IReadOnlyList<string?>> dimensionFilters,
        IReadOnlyList<MetricDimensionCursor> dimensionCursors,
        TimeSpan? dataPointInterval,
        Dictionary<string, List<string?>> knownAttributeValues)
    {
        if (dataPointInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dataPointInterval), interval, "The metric data point interval must be greater than zero.");
        }

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
        if (selectedDimensionIds.Length == 0)
        {
            return [];
        }

        var queryParameters = new DynamicParameters();
        queryParameters.Add("ApplyRange", startTime is not null && endTime is not null);
        queryParameters.Add("EndTicks", endTime?.Ticks ?? 0);
        queryParameters.Add("PointIntervalTicks", dataPointInterval?.Ticks ?? 0);
        var dimensionQueryRangesCteSql = CreateMetricDimensionQueryRangesCte(
            selectedDimensionIds,
            attributes,
            dimensionCursors,
            startTime,
            queryParameters);
        var metricPointsCteSql = dataPointInterval is null ? FullFidelityMetricPointsCteSql : s_rolledUpMetricPointsCteSql;
        var points = connection.Query<MetricPointDataRecord>($"""
            WITH {dimensionQueryRangesCteSql},
            {metricPointsCteSql}
            SELECT
                p.point_id AS PointId,
                p.dimension_id AS DimensionId,
                p.point_type AS PointType,
                p.start_time_ticks AS StartTimeTicks,
                p.end_time_ticks AS EndTimeTicks,
                p.repeat_count AS RepeatCount,
                p.integer_value AS IntegerValue,
                p.double_value AS DoubleValue,
                p.histogram_sum AS HistogramSum,
                p.histogram_count AS HistogramCount
            FROM rolled_up_metric_points p
            ORDER BY p.dimension_id, p.start_time_ticks, p.point_id;
            """, queryParameters).ToLookup(record => record.DimensionId);
        var (bucketCounts, explicitBounds) = isHistogram
            ? MaterializeMetricHistogramData(connection, dimensionQueryRangesCteSql, metricPointsCteSql, queryParameters)
            : (Array.Empty<MetricHistogramBucketRecord>().ToLookup(record => record.PointId),
                Array.Empty<MetricHistogramBoundRecord>().ToLookup(record => record.PointId));
        var exemplars = MaterializeMetricExemplars(connection, dimensionQueryRangesCteSql, metricPointsCteSql, queryParameters);

        var results = new List<DimensionScope>(selectedDimensionIds.Length);
        foreach (var dimensionId in selectedDimensionIds)
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
                    dimension.Values.Add(value);
                }
            }
            results.Add(dimension);
        }
        return results;
    }

    private static string CreateMetricDimensionQueryRangesCte(
        IReadOnlyList<long> selectedDimensionIds,
        ILookup<long, OwnedAttributeRecord> attributes,
        IReadOnlyList<MetricDimensionCursor> dimensionCursors,
        DateTime? defaultStartTime,
        DynamicParameters queryParameters)
    {
        var sql = new StringBuilder("metric_dimension_query_ranges(dimension_id, start_time_ticks) AS (VALUES ");
        for (var i = 0; i < selectedDimensionIds.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            var dimensionId = selectedDimensionIds[i];
            var dimensionAttributes = attributes[dimensionId];
            var cursor = dimensionCursors.FirstOrDefault(cursor =>
                cursor.Attributes.Length == dimensionAttributes.Count() &&
                cursor.Attributes.SequenceEqual(dimensionAttributes.Select(attribute => KeyValuePair.Create(attribute.AttributeKey, attribute.AttributeValue))));
            queryParameters.Add($"DimensionId{i}", dimensionId);
            queryParameters.Add($"DimensionStartTicks{i}", cursor?.StartTime.Ticks ?? defaultStartTime?.Ticks ?? 0);
            sql.Append(CultureInfo.InvariantCulture, $"(@DimensionId{i}, @DimensionStartTicks{i})");
        }
        sql.Append(')');
        return sql.ToString();
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
        string dimensionQueryRangesCteSql,
        string metricPointsCteSql,
        DynamicParameters queryParameters)
    {
        var bucketCounts = connection.Query<MetricHistogramBucketRecord>($"""
            WITH {dimensionQueryRangesCteSql},
            {metricPointsCteSql}
            SELECT
                p.point_id AS PointId,
                b.bucket_count AS BucketCount
            FROM telemetry_metric_histogram_bucket_counts b
            JOIN rolled_up_metric_points p ON p.point_id = b.point_id
            ORDER BY p.point_id, b.ordinal;
            """, queryParameters).ToLookup(record => record.PointId);
        var explicitBounds = connection.Query<MetricHistogramBoundRecord>($"""
            WITH {dimensionQueryRangesCteSql},
            {metricPointsCteSql}
            SELECT
                p.point_id AS PointId,
                b.explicit_bound AS ExplicitBound
            FROM telemetry_metric_histogram_explicit_bounds b
            JOIN rolled_up_metric_points p ON p.point_id = b.point_id
            ORDER BY p.point_id, b.ordinal;
            """, queryParameters).ToLookup(record => record.PointId);

        return (bucketCounts, explicitBounds);
    }

    private static ILookup<long, MetricsExemplar> MaterializeMetricExemplars(
        SqliteConnection connection,
        string dimensionQueryRangesCteSql,
        string metricPointsCteSql,
        DynamicParameters queryParameters)
    {
        var records = connection.Query<MetricExemplarRecord>($"""
            WITH {dimensionQueryRangesCteSql},
            {metricPointsCteSql}
            SELECT
                e.exemplar_id AS ExemplarId,
                p.point_id AS PointId,
                e.start_time_ticks AS StartTimeTicks,
                e.exemplar_value AS ExemplarValue,
                e.span_id AS SpanId,
                e.trace_id AS TraceId
            FROM telemetry_metric_exemplars e
            JOIN selected_metric_points source ON source.point_id = e.point_id
            JOIN metric_dimension_query_ranges r ON r.dimension_id = source.dimension_id
            JOIN rolled_up_metric_points p ON
                p.dimension_id = source.dimension_id AND
                p.point_type = source.point_type AND
                p.start_time_ticks = CASE
                    WHEN @PointIntervalTicks = 0 THEN source.start_time_ticks
                    ELSE (source.start_time_ticks / @PointIntervalTicks) * @PointIntervalTicks
                END
            WHERE @ApplyRange = 0 OR (e.start_time_ticks >= r.start_time_ticks AND e.start_time_ticks <= @EndTicks)
            ORDER BY p.point_id, e.exemplar_id;
            """, queryParameters).AsList();
        var attributes = connection.Query<OwnedAttributeRecord>($"""
            WITH {dimensionQueryRangesCteSql},
            {metricPointsCteSql}
            SELECT a.exemplar_id AS OwnerId, a.attribute_key AS AttributeKey, a.attribute_value AS AttributeValue
            FROM telemetry_metric_exemplar_attributes a
            JOIN telemetry_metric_exemplars e ON e.exemplar_id = a.exemplar_id
            JOIN selected_metric_points source ON source.point_id = e.point_id
            JOIN metric_dimension_query_ranges r ON r.dimension_id = source.dimension_id
            JOIN rolled_up_metric_points p ON
                p.dimension_id = source.dimension_id AND
                p.point_type = source.point_type AND
                p.start_time_ticks = CASE
                    WHEN @PointIntervalTicks = 0 THEN source.start_time_ticks
                    ELSE (source.start_time_ticks / @PointIntervalTicks) * @PointIntervalTicks
                END
            WHERE @ApplyRange = 0 OR (e.start_time_ticks >= r.start_time_ticks AND e.start_time_ticks <= @EndTicks)
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