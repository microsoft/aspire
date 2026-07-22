// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class DashboardSqliteDatabaseTests(ITestOutputHelper testOutputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(testOutputHelper);

    [Fact]
    public void InitializeSchema_HistogramCountsUseIntegerStorage()
    {
        using var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "dashboard.db"), pooling: false);
        database.InitializeSchema();
        using var connection = database.OpenConnection();

        var histogramCountColumnType = connection.QuerySingle<string>("SELECT type FROM pragma_table_info('telemetry_metric_points') WHERE name = 'histogram_count';");
        var bucketCountColumnType = connection.QuerySingle<string>("SELECT type FROM pragma_table_info('telemetry_metric_histogram_bucket_counts') WHERE name = 'bucket_count';");

        Assert.Equal("INTEGER", histogramCountColumnType);
        Assert.Equal("INTEGER", bucketCountColumnType);
    }

    [Fact]
    public async Task DapperQuery_CreatesActivityWithQueryInformation()
    {
        using var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "dashboard.db"), pooling: false);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(database.ActivitySource, onActivityStopped: activities.Enqueue);
        using var connection = database.OpenConnection();
        var query = $"-- {Guid.NewGuid():N}{Environment.NewLine}SELECT 42;";

        var result = await connection.QuerySingleAsync<int>(query);

        Assert.Equal(42, result);
        var activity = Assert.Single(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
        Assert.Equal(TracingSqliteConnection.ActivitySourceName, activity.Source.Name);
        Assert.Equal("SELECT sqlite", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("sqlite", activity.GetTagItem("db.system.name"));
        Assert.Equal("dashboard.db", activity.GetTagItem(OtlpSpan.PeerServiceAttributeKey));
        Assert.Equal("dashboard.db", activity.GetTagItem("db.namespace"));
        Assert.Equal("SELECT", activity.GetTagItem("db.operation.name"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    [Fact]
    public void DapperFailure_SetsActivityErrorInformation()
    {
        using var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "dashboard.db"), pooling: false);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(database.ActivitySource, onActivityStopped: activities.Enqueue);
        using var connection = database.OpenConnection();
        var query = $"SELECT * FROM missing_{Guid.NewGuid():N};";

        var exception = Assert.Throws<SqliteException>(() => connection.Query(query));

        var activity = Assert.Single(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(exception.Message, activity.StatusDescription);
        Assert.Equal(typeof(SqliteException).FullName, activity.GetTagItem("error.type"));
    }

    [Fact]
    public void DataReader_ActivityStopsWhenReaderIsDisposed()
    {
        using var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "dashboard.db"), pooling: false);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(database.ActivitySource, onActivityStopped: activities.Enqueue);
        using DbConnection connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        var query = $"SELECT '{Guid.NewGuid():N}';";
        command.CommandText = query;

        var reader = command.ExecuteReader();

        Assert.DoesNotContain(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
        Assert.True(reader.Read());
        reader.Dispose();
        Assert.Single(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
    }

    [Fact]
    public void DapperQueryMultiple_ActivitySpansAllResultSets()
    {
        using var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "dashboard.db"), pooling: false);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(database.ActivitySource, onActivityStopped: activities.Enqueue);
        using var connection = database.OpenConnection();
        var query = $"SELECT '{Guid.NewGuid():N}'; SELECT '{Guid.NewGuid():N}';";

        using (var results = connection.QueryMultiple(query))
        {
            Assert.DoesNotContain(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
            Assert.NotEmpty(results.ReadSingle<string>());
            Assert.DoesNotContain(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
            Assert.NotEmpty(results.ReadSingle<string>());
        }

        Assert.Single(activities, activity => Equals(activity.GetTagItem("db.query.text"), query));
    }

    [Fact]
    public void CommitTransaction_CreatesActivityWithDatabaseInformation()
    {
        using var database = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "dashboard.db"), pooling: false);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(database.ActivitySource, onActivityStopped: activities.Enqueue);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        connection.Execute("SELECT 1;", transaction: transaction);
        transaction.Commit();

        var activity = Assert.Single(activities, activity => Equals(activity.GetTagItem("db.query.text"), "COMMIT;"));
        Assert.Equal("COMMIT sqlite", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal("sqlite", activity.GetTagItem("db.system.name"));
        Assert.Equal("dashboard.db", activity.GetTagItem(OtlpSpan.PeerServiceAttributeKey));
        Assert.Equal("dashboard.db", activity.GetTagItem("db.namespace"));
        Assert.Equal("COMMIT", activity.GetTagItem("db.operation.name"));
    }

    [Fact]
    public void ActivityListener_DoesNotCaptureOtherDatabaseActivities()
    {
        using var observedDatabase = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "observed.db"), pooling: false);
        using var otherDatabase = new DashboardSqliteDatabase(Path.Combine(_workspace.Path, "other.db"), pooling: false);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(observedDatabase.ActivitySource, onActivityStopped: activities.Enqueue);
        using var observedConnection = observedDatabase.OpenConnection();
        using var otherConnection = otherDatabase.OpenConnection();

        observedConnection.QuerySingle<int>("SELECT 1;");
        otherConnection.QuerySingle<int>("SELECT 2;");

        Assert.Single(activities, activity => Equals(activity.GetTagItem("db.query.text"), "SELECT 1;"));
        Assert.DoesNotContain(activities, activity => Equals(activity.GetTagItem("db.query.text"), "SELECT 2;"));
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }
}
