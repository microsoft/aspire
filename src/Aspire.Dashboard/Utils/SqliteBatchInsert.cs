// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace Aspire.Dashboard.Utils;

internal static class SqliteBatchInsert
{
    internal static DbCommand CreateBatchInsertCommand(
        DbConnection connection,
        IDbTransaction transaction,
        int rowCount,
        string tableName,
        IReadOnlyList<string> columnNames)
    {
        var command = connection.CreateCommand();
        command.Transaction = (DbTransaction)transaction;
        var sql = new StringBuilder("INSERT INTO ");
        sql.Append(tableName);
        sql.Append(" (\n    ");
        sql.AppendJoin(", ", columnNames);
        sql.Append("\n)\nVALUES\n");
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            if (rowIndex > 0)
            {
                sql.AppendLine(",");
            }

            sql.Append("    (");
            for (var parameterIndex = 0; parameterIndex < columnNames.Count; parameterIndex++)
            {
                if (parameterIndex > 0)
                {
                    sql.Append(", ");
                }

                var parameterName = string.Create(CultureInfo.InvariantCulture, $"@param_{columnNames[parameterIndex]}_{rowIndex + 1}");
                sql.Append(parameterName);
                var parameter = command.CreateParameter();
                parameter.ParameterName = parameterName;
                command.Parameters.Add(parameter);
            }
            sql.Append(')');
        }
        sql.Append(';');
        command.CommandText = sql.ToString();
        command.Prepare();
        return command;
    }

    internal static void BatchInsertRows<T>(
        DbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<T> data,
        int batchSize,
        string tableName,
        IReadOnlyList<string> columnNames,
        BindRowParameters<T> bindRowParameters)
    {
        BatchInsertRows(
            data,
            batchSize,
            columnNames.Count,
            rowCount => CreateBatchInsertCommand(connection, transaction, rowCount, tableName, columnNames),
            bindRowParameters);
    }

    internal static void BatchInsertRows<T>(
        IReadOnlyList<T> data,
        int batchSize,
        int parametersPerRow,
        Func<int, DbCommand> commandFactory,
        BindRowParameters<T> bindRowParameters)
    {
        DbCommand? command = null;
        DbParameter[] parameters = [];
        try
        {
            for (var batchStart = 0; batchStart < data.Count; batchStart += batchSize)
            {
                var rowCount = Math.Min(batchSize, data.Count - batchStart);
                var parameterCount = checked(rowCount * parametersPerRow);
                if (command is null || parameters.Length != parameterCount)
                {
                    command?.Dispose();
                    command = commandFactory(rowCount);
                    parameters = command.Parameters.Cast<DbParameter>().ToArray();
                    if (parameters.Length != parameterCount)
                    {
                        throw new InvalidOperationException($"The batch insert command has {parameters.Length} parameters; expected {parameterCount}.");
                    }
                }

                for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    bindRowParameters(
                        data[batchStart + rowIndex],
                        parameters.AsSpan(rowIndex * parametersPerRow, parametersPerRow));
                }

                command.ExecuteNonQuery();
            }
        }
        finally
        {
            command?.Dispose();
        }
    }
}

internal delegate void BindRowParameters<in T>(T row, ReadOnlySpan<DbParameter> parameters);