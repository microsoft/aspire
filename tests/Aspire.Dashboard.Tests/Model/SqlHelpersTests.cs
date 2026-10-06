// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SqlHelpersTests
{
    [Theory]
    [InlineData("db.query.text")]
    [InlineData("db.statement")]
    [InlineData("commandText")]
    [InlineData("CommandText")]
    [InlineData("queryText")]
    [InlineData("QUERYTEXT")]
    [InlineData("SQL")]
    [InlineData("sqlQuery")]
    [InlineData("SqlStatement")]
    [InlineData("sql.query")]
    [InlineData("sql.statement")]
    [InlineData("SQL.QUERY")]
    [InlineData("SQL.STATEMENT")]
    public void GetFormat_QueryField_ReturnsSql(string name)
    {
        Assert.Equal(DashboardUIHelpers.SqlFormat, SqlHelpers.GetFormat(name, []));
    }

    [Theory]
    [InlineData("Message")]
    [InlineData("Query")]
    [InlineData("db.query.summary")]
    [InlineData("db.query.parameter.sql")]
    [InlineData("db.operation.name")]
    [InlineData("sql.parameters")]
    [InlineData("commandTimeout")]
    [InlineData("notSqlQuery")]
    [InlineData("DB.QUERY.TEXT")]
    [InlineData("Db.Statement")]
    public void GetFormat_OtherField_ReturnsNull(string name)
    {
        Assert.Null(SqlHelpers.GetFormat(name, []));
    }

    [Theory]
    [InlineData("db.query.text", "db.system.name", "microsoft.sql_server", true)]
    [InlineData("db.statement", "db.system", "mssql", true)]
    [InlineData("db.query.text", "db.system.name", "postgresql", true)]
    [InlineData("db.statement", "db.system", "mysql", true)]
    [InlineData("db.query.text", "db.system.name", "sqlite", true)]
    [InlineData("db.query.text", "db.system.name", "firebirdsql", true)]
    [InlineData("db.query.text", "db.system.name", "other_sql", true)]
    [InlineData("db.query.text", "db.system.name", "redis", false)]
    [InlineData("db.statement", "db.system", "mongodb", false)]
    [InlineData("db.query.text", "db.system.name", "elasticsearch", false)]
    [InlineData("db.query.text", "db.system.name", "neo4j", false)]
    [InlineData("db.query.text", "db.system.name", "custom", false)]
    [InlineData("queryText", "db.system.name", "redis", false)]
    [InlineData("commandText", "db.system", "mssql", true)]
    [InlineData("db.query.text", "db.system.name", "POSTGRESQL", false)]
    [InlineData("sql", "db.system.name", "redis", true)]
    [InlineData("sqlQuery", "db.system.name", "redis", true)]
    [InlineData("sqlStatement", "db.system.name", "redis", true)]
    [InlineData("sql.query", "db.system.name", "redis", true)]
    [InlineData("sql.statement", "db.system.name", "redis", true)]
    public void GetFormat_SemanticConvention_RespectsDatabaseSystem(string name, string systemKey, string system, bool isSql)
    {
        Assert.Equal(isSql ? DashboardUIHelpers.SqlFormat : null, SqlHelpers.GetFormat(name, [KeyValuePair.Create(systemKey, system)]));
    }

    [Fact]
    public void GetFormat_BothDatabaseSystems_PrefersCurrentConvention()
    {
        Assert.Null(SqlHelpers.GetFormat("db.query.text",
        [
            KeyValuePair.Create("db.system", "mssql"),
            KeyValuePair.Create("db.system.name", "redis")
        ]));
    }
}
