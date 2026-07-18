// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
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

    internal new TracingSqliteTransaction BeginTransaction() =>
        new(base.BeginTransaction(), Path.GetFileName(databasePath), activitySource);

    internal new TracingSqliteTransaction BeginTransaction(IsolationLevel isolationLevel) =>
        new(base.BeginTransaction(isolationLevel), Path.GetFileName(databasePath), activitySource);

    protected override DbCommand CreateDbCommand() => new TracingDbCommand(base.CreateDbCommand(), Path.GetFileName(databasePath), activitySource);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => BeginTransaction(isolationLevel);

    private static Activity? StartActivity(string query, string databaseName, ActivitySource activitySource)
    {
        var operationName = GetOperationName(query);
        var activity = activitySource.StartActivity(
            operationName is null ? "sqlite query" : $"{operationName} sqlite",
            ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("db.system.name", "sqlite");
            activity.SetTag(OtlpSpan.PeerServiceAttributeKey, databaseName);
            activity.SetTag("db.namespace", databaseName);
            activity.SetTag("db.query.text", query);
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

    internal sealed class TracingSqliteTransaction(SqliteTransaction transaction, string databaseName, ActivitySource activitySource) : DbTransaction
    {
        internal SqliteTransaction InnerTransaction => transaction;

        public override IsolationLevel IsolationLevel => transaction.IsolationLevel;

        protected override DbConnection? DbConnection => transaction.Connection;

        public override bool SupportsSavepoints => transaction.SupportsSavepoints;

        public override void Commit() => ExecuteWithActivity("COMMIT;", transaction.Commit);

        public override Task CommitAsync(CancellationToken cancellationToken = default) =>
            ExecuteWithActivityAsync("COMMIT;", () => transaction.CommitAsync(cancellationToken));

        public override void Rollback() => ExecuteWithActivity("ROLLBACK;", transaction.Rollback);

        public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
            ExecuteWithActivityAsync("ROLLBACK;", () => transaction.RollbackAsync(cancellationToken));

        public override void Save(string savepointName) => transaction.Save(savepointName);

        public override Task SaveAsync(string savepointName, CancellationToken cancellationToken = default) =>
            transaction.SaveAsync(savepointName, cancellationToken);

        public override void Rollback(string savepointName) => transaction.Rollback(savepointName);

        public override Task RollbackAsync(string savepointName, CancellationToken cancellationToken = default) =>
            transaction.RollbackAsync(savepointName, cancellationToken);

        public override void Release(string savepointName) => transaction.Release(savepointName);

        public override Task ReleaseAsync(string savepointName, CancellationToken cancellationToken = default) =>
            transaction.ReleaseAsync(savepointName, cancellationToken);

        public override ValueTask DisposeAsync() => transaction.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                transaction.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ExecuteWithActivity(string query, Action execute)
        {
            using var activity = StartActivity(query, databaseName, activitySource);
            try
            {
                execute();
            }
            catch (Exception exception)
            {
                RecordException(activity, exception);
                throw;
            }
        }

        private async Task ExecuteWithActivityAsync(string query, Func<Task> execute)
        {
            using var activity = StartActivity(query, databaseName, activitySource);
            try
            {
                await execute().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordException(activity, exception);
                throw;
            }
        }
    }

    private sealed class TracingDbCommand(DbCommand command, string databaseName, ActivitySource activitySource) : DbCommand
    {
        private DbTransaction? _transaction;

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
            get => _transaction ?? command.Transaction;
            set
            {
                _transaction = value;
                command.Transaction = value is TracingSqliteTransaction tracingTransaction
                    ? tracingTransaction.InnerTransaction
                    : value;
            }
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

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var activity = StartActivity();
            try
            {
                var reader = command.ExecuteReader(behavior);
                return activity is null ? reader : new TracingDbDataReader(reader, activity);
            }
            catch (Exception exception)
            {
                RecordException(activity, exception);
                activity?.Dispose();
                throw;
            }
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            var activity = StartActivity();
            try
            {
                var reader = await command.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
                return activity is null ? reader : new TracingDbDataReader(reader, activity);
            }
            catch (Exception exception)
            {
                RecordException(activity, exception);
                activity?.Dispose();
                throw;
            }
        }

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

        private Activity? StartActivity() => TracingSqliteConnection.StartActivity(CommandText, databaseName, activitySource);

        private sealed class TracingDbDataReader(DbDataReader reader, Activity activity) : DbDataReader
        {
            private Activity? _activity = activity;

            public override object this[int ordinal] => reader[ordinal];
            public override object this[string name] => reader[name];
            public override int Depth => reader.Depth;
            public override int FieldCount => reader.FieldCount;
            public override bool HasRows => reader.HasRows;
            public override bool IsClosed => reader.IsClosed;
            public override int RecordsAffected => reader.RecordsAffected;
            public override int VisibleFieldCount => reader.VisibleFieldCount;

            public override void Close()
            {
                try
                {
                    reader.Close();
                }
                catch (Exception exception)
                {
                    RecordException(_activity, exception);
                    throw;
                }
                finally
                {
                    CompleteActivity();
                }
            }

            public override bool GetBoolean(int ordinal) => reader.GetBoolean(ordinal);
            public override byte GetByte(int ordinal) => reader.GetByte(ordinal);
            public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
            public override char GetChar(int ordinal) => reader.GetChar(ordinal);
            public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
            public override string GetDataTypeName(int ordinal) => reader.GetDataTypeName(ordinal);
            public override DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);
            public override decimal GetDecimal(int ordinal) => reader.GetDecimal(ordinal);
            public override double GetDouble(int ordinal) => reader.GetDouble(ordinal);
            public override IEnumerator GetEnumerator() => reader.GetEnumerator();
            public override Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);
            public override T GetFieldValue<T>(int ordinal) => reader.GetFieldValue<T>(ordinal);
            public override float GetFloat(int ordinal) => reader.GetFloat(ordinal);
            public override Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);
            public override short GetInt16(int ordinal) => reader.GetInt16(ordinal);
            public override int GetInt32(int ordinal) => reader.GetInt32(ordinal);
            public override long GetInt64(int ordinal) => reader.GetInt64(ordinal);
            public override string GetName(int ordinal) => reader.GetName(ordinal);
            public override int GetOrdinal(string name) => reader.GetOrdinal(name);
            public override Type GetProviderSpecificFieldType(int ordinal) => reader.GetProviderSpecificFieldType(ordinal);
            public override object GetProviderSpecificValue(int ordinal) => reader.GetProviderSpecificValue(ordinal);
            public override int GetProviderSpecificValues(object[] values) => reader.GetProviderSpecificValues(values);
            public override DataTable? GetSchemaTable() => reader.GetSchemaTable();
            public override Stream GetStream(int ordinal) => reader.GetStream(ordinal);
            public override string GetString(int ordinal) => reader.GetString(ordinal);
            public override TextReader GetTextReader(int ordinal) => reader.GetTextReader(ordinal);
            public override object GetValue(int ordinal) => reader.GetValue(ordinal);
            public override int GetValues(object[] values) => reader.GetValues(values);
            public override bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);

            public override bool NextResult() => ExecuteReaderOperation(reader.NextResult);

            public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
                ExecuteReaderOperationAsync(() => reader.NextResultAsync(cancellationToken));

            public override bool Read() => ExecuteReaderOperation(reader.Read);

            public override Task<bool> ReadAsync(CancellationToken cancellationToken) =>
                ExecuteReaderOperationAsync(() => reader.ReadAsync(cancellationToken));

            public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
                reader.GetFieldValueAsync<T>(ordinal, cancellationToken);

            public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
                reader.IsDBNullAsync(ordinal, cancellationToken);

            public override async ValueTask DisposeAsync()
            {
                try
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordException(_activity, exception);
                    throw;
                }
                finally
                {
                    CompleteActivity();
                }

                GC.SuppressFinalize(this);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try
                    {
                        reader.Dispose();
                    }
                    catch (Exception exception)
                    {
                        RecordException(_activity, exception);
                        throw;
                    }
                    finally
                    {
                        CompleteActivity();
                    }
                }

                base.Dispose(disposing);
            }

            private T ExecuteReaderOperation<T>(Func<T> operation)
            {
                try
                {
                    return operation();
                }
                catch (Exception exception)
                {
                    RecordException(_activity, exception);
                    CompleteActivity();
                    throw;
                }
            }

            private async Task<T> ExecuteReaderOperationAsync<T>(Func<Task<T>> operation)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordException(_activity, exception);
                    CompleteActivity();
                    throw;
                }
            }

            private void CompleteActivity() => Interlocked.Exchange(ref _activity, null)?.Dispose();
        }
    }
}