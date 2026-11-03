// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aspire.Dashboard.Benchmarks;

[MemoryDiagnoser]
[Config(typeof(Config))]
public class TelemetryRepositoryMetricRepeatsBenchmarks
{
    private const int DimensionCount = 1000;
    private const int DimensionsPerExport = 100;
    private string _temporaryDirectory = null!;
    private DashboardSqliteDatabase _database = null!;
    private SqliteTelemetryRepository _repository = null!;
    private NumberDataPoint[] _points = null!;
    private readonly List<RepeatedField<ResourceMetrics>> _exports = [];

    [Params(100, 4600)]
    public int PointsPerDimension { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-metric-repeats-").FullName;
        _database = new DashboardSqliteDatabase(Path.Combine(_temporaryDirectory, "metrics.db"));
        await _database.InitializeSchemaAsync(CancellationToken.None);
        _repository = TelemetryRepositoryMetricsBenchmarks.CreateRepository(_database);
        var batch = TelemetryRepositoryMetricsBenchmarks.CreateLongDurationMetricBatches(DimensionCount, isHistogram: false, totalSampleCount: 1).Single();
        var context = new AddContext();
        await _repository.AddMetricsAsync(context, batch);
        if (context.SuccessCount != DimensionCount || context.FailureCount > 0)
        {
            throw new InvalidOperationException($"Expected {DimensionCount} initial points, added {context.SuccessCount} and rejected {context.FailureCount}.");
        }
        _points = batch[0].ScopeMetrics[0].Metrics[0].Sum.DataPoints.ToArray();
        var startTimeUnixNano = checked((long)_points[0].TimeUnixNano);
        const long intervalUnixNano = 2_000_000_000;
        // Seed history in SQLite rather than spending minutes measuring setup through ingestion.
        // Reopen the repository afterwards so its point counts and latest values match the database.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _database.DatabasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // This unpooled setup connection is closed before measuring the repository's default cache.
            command.CommandText = "PRAGMA cache_size = -65536; PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();
            command.CommandText = """
                WITH RECURSIVE samples(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM samples WHERE value + 1 < @PointCount
                )
                INSERT INTO telemetry_metric_points(
                    dimension_id, point_type, start_time_unix_nano, end_time_unix_nano,
                    repeat_count, integer_value, flags, end_time_bucket)
                SELECT d.dimension_id, 1,
                    @StartTime + (samples.value - 1) * @Interval,
                    @StartTime + samples.value * @Interval,
                    1, samples.value, 0, (@StartTime + samples.value * @Interval) / @TimeBucketWidth
                FROM samples
                CROSS JOIN telemetry_metric_dimensions d
                ORDER BY samples.value, d.dimension_id;
                """;
            command.Parameters.AddWithValue("PointCount", PointsPerDimension);
            command.Parameters.AddWithValue("StartTime", startTimeUnixNano);
            command.Parameters.AddWithValue("Interval", intervalUnixNano);
            command.Parameters.AddWithValue("TimeBucketWidth", SqliteTelemetryRepository.MetricTimeBucketWidthUnixNano);
            var inserted = command.ExecuteNonQuery();
            if (inserted != DimensionCount * (PointsPerDimension - 1))
            {
                throw new InvalidOperationException($"Expected {DimensionCount * (PointsPerDimension - 1)} history points, inserted {inserted}.");
            }
        }

        _repository.Dispose();
        _repository = TelemetryRepositoryMetricsBenchmarks.CreateRepository(_database);
        foreach (var point in _points)
        {
            point.AsInt = PointsPerDimension - 1;
            point.StartTimeUnixNano = point.TimeUnixNano = checked((ulong)(startTimeUnixNano + (PointsPerDimension - 1) * intervalUnixNano));
        }
        batch[0].ScopeMetrics[0].Metrics[0].Sum.DataPoints.Clear();
        // Keep the working set spread across many dimensions and separate exports.
        // A few dimensions at capacity do not reproduce the repeat-update index churn.
        foreach (var points in _points.Chunk(DimensionsPerExport))
        {
            var export = batch.Clone();
            var exportPoints = export[0].ScopeMetrics[0].Metrics[0].Sum.DataPoints;
            exportPoints.Clear();
            exportPoints.Add(points);
            _exports.Add(export);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _repository.Dispose();
        _database.ClearPool();
        _database.Dispose();
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    [Benchmark(Description = "TelemetryRepository: repeat metrics across 1000 dimensions")]
    public async Task<int> AddRepeatedMetrics()
    {
        foreach (var point in _points)
        {
            point.TimeUnixNano += 1_000_000_000;
        }
        var context = new AddContext();
        foreach (var export in _exports)
        {
            await _repository.AddMetricsAsync(context, export);
        }
        if (context.SuccessCount != DimensionCount || context.FailureCount > 0)
        {
            throw new InvalidOperationException($"Expected {DimensionCount} repeated points, added {context.SuccessCount} and rejected {context.FailureCount}.");
        }

        return context.SuccessCount;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            // Warm caches and repeated commits are essential to measuring steady-state ingestion.
            AddJob(Job.Dry.WithStrategy(RunStrategy.Monitoring)
                .WithWarmupCount(3).WithIterationCount(12)
                .WithToolchain(InProcessNoEmitToolchain.Instance).DontEnforcePowerPlan());

            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}
