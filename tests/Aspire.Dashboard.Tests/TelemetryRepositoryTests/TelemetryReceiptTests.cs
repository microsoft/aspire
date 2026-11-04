// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public sealed class TelemetryReceiptTests(ITestOutputHelper output) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Theory]
    [InlineData(AspireDataType.StructuredLogs)]
    [InlineData(AspireDataType.Traces)]
    [InlineData(AspireDataType.Metrics)]
    public async Task AcceptedTelemetry_RemainsReceivedAfterRemoval(AspireDataType signal)
    {
        var path = Path.Combine(_workspace.Path, "dashboard.db");
        using var context = SqliteRepositoryTestHelpers.CreateTelemetryRepository(path);
        var repository = context.Repository;
        Assert.False(repository.HasReceivedTelemetry);

        await AddSignalAsync(repository, signal);
        Assert.True(repository.HasReceivedTelemetry);

        using (var resumed = SqliteRepositoryTestHelpers.CreateTelemetryRepository(path))
        {
            Assert.True(resumed.Repository.HasReceivedTelemetry);
        }

        await repository.ClearSelectedSignalsAsync(new()
        {
            [repository.GetResources().Single().ResourceKey.GetCompositeName()] = [AspireDataType.Resource, AspireDataType.StructuredLogs, AspireDataType.Traces, AspireDataType.Metrics]
        });
        await repository.ClearStructuredLogsAsync();
        await repository.ClearTracesAsync();
        await repository.ClearMetricsAsync();
        Assert.Empty(repository.GetResources());
        Assert.True(repository.HasReceivedTelemetry);

        using var restarted = SqliteRepositoryTestHelpers.CreateTelemetryRepository(path);
        Assert.False(restarted.Repository.HasReceivedTelemetry);
    }

    [Fact]
    public async Task EmptyEnvelopes_DoNotCountAsReceived()
    {
        using var context = SqliteRepositoryTestHelpers.CreateTelemetryRepository(Path.Combine(_workspace.Path, "dashboard.db"));
        var repository = context.Repository;
        await repository.AddLogsAsync(new(), [new ResourceLogs { Resource = CreateResource("test") }]);
        await repository.AddTracesAsync(new(), [new ResourceSpans { Resource = CreateResource("test") }]);
        await repository.AddMetricsAsync(new(), [new ResourceMetrics { Resource = CreateResource("test") }]);

        Assert.NotEmpty(repository.GetResources());
        Assert.False(repository.HasReceivedTelemetry);
    }

    [Theory]
    [InlineData(AspireDataType.StructuredLogs)]
    [InlineData(AspireDataType.Traces)]
    [InlineData(AspireDataType.Metrics)]
    public async Task PausedTelemetry_DoesNotCountAsReceived(AspireDataType signal)
    {
        var pauseManager = new PauseManager();
        pauseManager.SetStructuredLogsPaused(true);
        pauseManager.SetTracesPaused(true);
        pauseManager.SetMetricsPaused(true);
        using var context = SqliteRepositoryTestHelpers.CreateTelemetryRepository(
            Path.Combine(_workspace.Path, "dashboard.db"), pauseManager: pauseManager);

        await AddSignalAsync(context.Repository, signal);

        Assert.False(context.Repository.HasReceivedTelemetry);
    }

    [Fact]
    public async Task RejectedMetric_DoesNotCountAsReceived()
    {
        using var context = SqliteRepositoryTestHelpers.CreateTelemetryRepository(Path.Combine(_workspace.Path, "dashboard.db"));
        var metric = CreateSumMetric("test", DateTime.UnixEpoch);
        metric.Sum.AggregationTemporality = (AggregationTemporality)42;
        var addContext = new AddContext();
        await context.Repository.AddMetricsAsync(addContext,
        [
            new ResourceMetrics
            {
                Resource = CreateResource("test"),
                ScopeMetrics = { new ScopeMetrics { Scope = CreateScope(), Metrics = { metric } } }
            }
        ]);

        Assert.True(addContext.FailureCount > 0);
        Assert.False(context.Repository.HasReceivedTelemetry);
    }

    [Fact]
    public async Task FailedTransaction_DoesNotCountAsReceived()
    {
        var path = Path.Combine(_workspace.Path, "dashboard.db");
        using var context = SqliteRepositoryTestHelpers.CreateTelemetryRepository(path);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        connection.Execute("""
            CREATE TRIGGER reject_logs BEFORE INSERT ON telemetry_logs
            BEGIN
                SELECT RAISE(ABORT, 'Test write failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => AddSignalAsync(context.Repository, AspireDataType.StructuredLogs));

        Assert.False(context.Repository.HasReceivedTelemetry);
    }

    [Fact]
    public async Task ConcurrentSignals_KeepReceivedState()
    {
        using var context = SqliteRepositoryTestHelpers.CreateTelemetryRepository(Path.Combine(_workspace.Path, "dashboard.db"));
        Assert.False(context.Repository.HasReceivedTelemetry);

        await Task.WhenAll(
            AddSignalAsync(context.Repository, AspireDataType.StructuredLogs),
            AddSignalAsync(context.Repository, AspireDataType.Traces),
            AddSignalAsync(context.Repository, AspireDataType.Metrics));

        Assert.True(context.Repository.HasReceivedTelemetry);
    }

    private static Task AddSignalAsync(SqliteTelemetryRepository repository, AspireDataType signal) => signal switch
    {
        AspireDataType.StructuredLogs => repository.AddLogsAsync(new(),
        [
            new ResourceLogs
            {
                Resource = CreateResource("test"),
                ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord(message: "test") } } }
            }
        ]),
        AspireDataType.Traces => repository.AddTracesAsync(new(),
        [
            new ResourceSpans
            {
                Resource = CreateResource("test"),
                ScopeSpans = { new ScopeSpans { Scope = CreateScope(), Spans = { CreateSpan("trace", "span", DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(1)) } } }
            }
        ]),
        AspireDataType.Metrics => repository.AddMetricsAsync(new(),
        [
            new ResourceMetrics
            {
                Resource = CreateResource("test"),
                ScopeMetrics = { new ScopeMetrics { Scope = CreateScope(), Metrics = { CreateSumMetric("test", DateTime.UnixEpoch) } } }
            }
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(signal))
    };

    public void Dispose() => _workspace.Dispose();
}
