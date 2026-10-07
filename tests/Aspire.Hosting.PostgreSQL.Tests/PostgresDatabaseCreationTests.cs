// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.PostgreSQL.Tests;

public class PostgresDatabaseCreationTests
{
    [Fact]
    public async Task NonTransientFailure_DoesNotStopRemainingDatabases()
    {
        var databases = CreateDatabases(builder => { });
        var session = new FakeSession
        {
            OnCreate = (database, attempt) => database.Name == "db1"
                ? throw new InvalidOperationException("not a connection failure")
                : Task.CompletedTask
        };

        await RunAsync(databases, session);

        Assert.Equal(["db1", "db2", "db3"], session.CreateCalls);
        Assert.Equal(1, session.OpenCount);
    }

    [Fact]
    public async Task TransientFailure_RetriesOnFreshConnection_WithoutRepeatingCompletedDatabases()
    {
        var databases = CreateDatabases(builder => { });
        var session = new FakeSession
        {
            OnCreate = (database, attempt) => database.Name == "db2" && attempt == 1
                ? throw new IOException("connection reset")
                : Task.CompletedTask
        };

        await RunAsync(databases, session);

        Assert.Equal(["db1", "db2", "db2", "db3"], session.CreateCalls);
        Assert.Equal(2, session.OpenCount);
        Assert.Empty(session.ExistsCalls);
    }

    [Fact]
    public async Task InterruptedCustomScript_RunsAgain_WhenDatabaseDoesNotExist()
    {
        var databases = CreateDatabases(db1 => db1.WithCreationScript("CREATE DATABASE db1;"));
        var session = new FakeSession
        {
            OnCreate = (database, attempt) => database.Name == "db1" && attempt == 1
                ? throw new IOException("connection reset")
                : Task.CompletedTask,
            OnExists = databaseName => false
        };

        await RunAsync(databases, session);

        Assert.Equal(["db1", "db1", "db2", "db3"], session.CreateCalls);
        Assert.Equal(["db1"], session.ExistsCalls);
    }

    [Fact]
    public async Task InterruptedCustomScript_IsNotRepeated_WhenDatabaseExists()
    {
        var databases = CreateDatabases(db1 => db1.WithCreationScript("CREATE DATABASE db1;"));
        var session = new FakeSession
        {
            OnCreate = (database, attempt) => database.Name == "db1" && attempt == 1
                ? throw new IOException("connection reset")
                : Task.CompletedTask,
            OnExists = databaseName => true
        };

        await RunAsync(databases, session);

        Assert.Equal(["db1", "db2", "db3"], session.CreateCalls);
        Assert.Equal(["db1"], session.ExistsCalls);
        Assert.Equal(2, session.OpenCount);
    }

    private static List<PostgresDatabaseResource> CreateDatabases(Action<IResourceBuilder<PostgresDatabaseResource>> configureDb1)
    {
        var builder = DistributedApplication.CreateBuilder();
        var postgres = builder.AddPostgres("pg");

        var db1 = postgres.AddDatabase("db1");
        configureDb1(db1);

        return [db1.Resource, postgres.AddDatabase("db2").Resource, postgres.AddDatabase("db3").Resource];
    }

    private static Task RunAsync(List<PostgresDatabaseResource> databases, FakeSession session) =>
        PostgresBuilderExtensions.CreateDatabasesWithRetryAsync(
            databases,
            openConnectionAsync: ct => Task.FromResult(session.Open()),
            createDatabaseAsync: (connection, database, ct) => session.Create(database),
            databaseExistsAsync: (connection, databaseName, ct) => Task.FromResult(session.Exists(databaseName)),
            NullLogger.Instance,
            "pg",
            initialRetryDelay: TimeSpan.Zero,
            CancellationToken.None).DefaultTimeout();

    private sealed class FakeSession
    {
        private readonly Dictionary<string, int> _attempts = [];

        public Func<PostgresDatabaseResource, int, Task> OnCreate { get; init; } = (database, attempt) => Task.CompletedTask;
        public Func<string, bool> OnExists { get; init; } = databaseName => false;
        public int OpenCount { get; private set; }
        public List<string> CreateCalls { get; } = [];
        public List<string> ExistsCalls { get; } = [];

        public FakeConnection Open()
        {
            OpenCount++;
            return new FakeConnection();
        }

        public Task Create(PostgresDatabaseResource database)
        {
            CreateCalls.Add(database.Name);
            var attempt = _attempts[database.Name] = _attempts.GetValueOrDefault(database.Name) + 1;
            return OnCreate(database, attempt);
        }

        public bool Exists(string databaseName)
        {
            ExistsCalls.Add(databaseName);
            return OnExists(databaseName);
        }
    }

    private sealed class FakeConnection : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
