// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.Data.Sqlite;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Adds tracing to commands executed by Dapper through a SQLite connection.
/// </summary>
internal sealed class TracingSqliteConnection(string connectionString, string databasePath, ActivitySource activitySource) : SqliteConnection(connectionString)
{
    internal const string ActivitySourceName = "Aspire.Dashboard.Sqlite";

    protected override DbCommand CreateDbCommand() => new TracingDbCommand(base.CreateDbCommand(), Path.GetFileName(databasePath), activitySource);

    private sealed class TracingDbCommand(DbCommand command, string databaseName, ActivitySource activitySource) : DbCommand
    {
        [AllowNull]
        public override string CommandText
        {
            get => command.CommandText;
            set => command.CommandText = value;
        }

        public override int CommandTimeout
        {
            get => command.CommandTimeout;
            set => command.CommandTimeout = value;
        }

        public override CommandType CommandType
        {
            get => command.CommandType;
            set => command.CommandType = value;
        }

        public override bool DesignTimeVisible
        {
            get => command.DesignTimeVisible;
            set => command.DesignTimeVisible = value;
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => command.UpdatedRowSource;
            set => command.UpdatedRowSource = value;
        }

        protected override DbConnection? DbConnection
        {
            get => command.Connection;
            set => command.Connection = value;
        }

        protected override DbParameterCollection DbParameterCollection => command.Parameters;

        protected override DbTransaction? DbTransaction
        {
            get => command.Transaction;
            set => command.Transaction = value;
        }

        public override void Cancel() => command.Cancel();

        public override int ExecuteNonQuery() => ExecuteWithActivity(command.ExecuteNonQuery);

        public override object? ExecuteScalar() => ExecuteWithActivity(command.ExecuteScalar);

        public override void Prepare() => command.Prepare();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            ExecuteWithActivityAsync(() => command.ExecuteNonQueryAsync(cancellationToken));

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            ExecuteWithActivityAsync(() => command.ExecuteScalarAsync(cancellationToken));

        public override Task PrepareAsync(CancellationToken cancellationToken = default) => command.PrepareAsync(cancellationToken);

        public override ValueTask DisposeAsync() => command.DisposeAsync();

        protected override DbParameter CreateDbParameter() => command.CreateParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            ExecuteWithActivity(() => command.ExecuteReader(behavior));

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
            ExecuteWithActivityAsync(() => command.ExecuteReaderAsync(behavior, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                command.Dispose();
            }

            base.Dispose(disposing);
        }

        private T ExecuteWithActivity<T>(Func<T> execute)
        {
            using var activity = StartActivity();
            try
            {
                return execute();
            }
            catch (Exception exception)
            {
                RecordException(activity, exception);
                throw;
            }
        }

        private async Task<T> ExecuteWithActivityAsync<T>(Func<Task<T>> execute)
        {
            using var activity = StartActivity();
            try
            {
                return await execute().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordException(activity, exception);
                throw;
            }
        }

        private Activity? StartActivity()
        {
            var operationName = GetOperationName(CommandText);
            var activity = activitySource.StartActivity(
                operationName is null ? "sqlite query" : $"{operationName} sqlite",
                ActivityKind.Client);
            if (activity is not null)
            {
                activity.SetTag("db.system.name", "sqlite");
                activity.SetTag(OtlpSpan.PeerServiceAttributeKey, databaseName);
                activity.SetTag("db.namespace", databaseName);
                activity.SetTag("db.query.text", CommandText);
                activity.SetTag("db.operation.name", operationName);
            }

            return activity;
        }

        private static string? GetOperationName(string query)
        {
            var querySpan = query.AsSpan();
            while (true)
            {
                querySpan = querySpan.TrimStart();

                // Embedded schema scripts start with license comments before the first SQL statement.
                if (querySpan.StartsWith("--"))
                {
                    var lineEndIndex = querySpan.IndexOfAny('\r', '\n');
                    if (lineEndIndex < 0)
                    {
                        return null;
                    }

                    querySpan = querySpan[(lineEndIndex + 1)..];
                    continue;
                }

                if (querySpan.StartsWith("/*"))
                {
                    var commentEndIndex = querySpan.IndexOf("*/");
                    if (commentEndIndex < 0)
                    {
                        return null;
                    }

                    querySpan = querySpan[(commentEndIndex + 2)..];
                    continue;
                }

                break;
            }

            var separatorIndex = querySpan.IndexOfAny(" \t\r\n;");
            var operationSpan = separatorIndex >= 0 ? querySpan[..separatorIndex] : querySpan;
            return operationSpan.IsEmpty ? null : operationSpan.ToString().ToUpperInvariant();
        }

        private static void RecordException(Activity? activity, Exception exception)
        {
            activity?.SetTag("error.type", exception.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        }
    }
}