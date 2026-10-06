// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SqlHelpersTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", DashboardUIHelpers.SqlFormat)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command.Custom", DashboardUIHelpers.SqlFormat)]
    [InlineData("Microsoft.EntityFrameworkCore", null)]
    [InlineData("Microsoft.EntityFrameworkCore.Query", null)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Transaction", null)]
    [InlineData("Npgsql.Command", DashboardUIHelpers.SqlFormat)]
    [InlineData("Npgsql.Command.Custom", DashboardUIHelpers.SqlFormat)]
    [InlineData("MySqlConnector.MySqlCommand", DashboardUIHelpers.SqlFormat)]
    [InlineData("MySqlConnector.MySqlCommand.Custom", DashboardUIHelpers.SqlFormat)]
    [InlineData("NHibernate.SQL", DashboardUIHelpers.SqlFormat)]
    [InlineData("NHibernate.SQL.Custom", DashboardUIHelpers.SqlFormat)]
    [InlineData("Npgsql", null)]
    [InlineData("Npgsql.Connection", null)]
    [InlineData("Npgsql.Transaction", null)]
    [InlineData("MySqlConnector", null)]
    [InlineData("MySqlConnector.MySqlConnection", null)]
    [InlineData("MySqlConnector.ConnectionPool", null)]
    [InlineData("NHibernate", null)]
    [InlineData("NHibernate.Transaction", null)]
    [InlineData("MyApp.Microsoft.EntityFrameworkCore.Database.Command", null)]
    [InlineData("MyApp.Npgsql.Command", null)]
    [InlineData("MyApp.MySqlConnector.MySqlCommand", null)]
    [InlineData("MyApp.NHibernate.SQL", null)]
    [InlineData("nhibernate.sql", null)]
    [InlineData("", null)]
    public void GetLogMessageFormat_OnlyKnownCommandSources_ReturnsSql(string source, string? expectedFormat)
    {
        Assert.Equal(expectedFormat, SqlHelpers.GetLogMessageFormat(source));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.EntityFrameworkCore.Query")]
    [InlineData("Npgsql")]
    [InlineData("Npgsql.Command")]
    [InlineData("MySqlConnector")]
    [InlineData("MySqlConnector.MySqlCommand")]
    public void GetLogFormat_KnownDatabaseSource_ReturnsSql(string source)
    {
        Assert.Equal(DashboardUIHelpers.SqlFormat, SqlHelpers.GetLogFormat("commandText", source, []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MyApp.Controllers.DatabaseController")]
    [InlineData("MyApp.Microsoft.EntityFrameworkCore")]
    [InlineData("MyApp.Npgsql.Command")]
    [InlineData("MyApp.MySqlConnector")]
    [InlineData("Microsoft.EntityFramework")]
    [InlineData("microsoft.entityframeworkcore.Database.Command")]
    [InlineData("MongoDB.Command")]
    public void GetLogFormat_OtherSourceWithoutDatabaseSystem_ReturnsNull(string source)
    {
        Assert.Null(SqlHelpers.GetLogFormat("commandText", source, []));
        Assert.Null(SqlHelpers.GetLogFormat("db.query.text", source, []));
        Assert.Null(SqlHelpers.GetLogFormat("sql", source, []));
    }

    [Theory]
    [InlineData("MyApp.Database", "db.query.text", "db.system.name", "postgresql", DashboardUIHelpers.SqlFormat)]
    [InlineData("MyApp.Database", "db.statement", "db.system", "mysql", DashboardUIHelpers.SqlFormat)]
    [InlineData("MyApp.Database", "commandText", "db.system.name", "mssql", DashboardUIHelpers.SqlFormat)]
    [InlineData("MyApp.Database", "queryText", "db.system", "postgresql", DashboardUIHelpers.SqlFormat)]
    [InlineData("NHibernate.SQL", "commandText", "db.system.name", "mssql", DashboardUIHelpers.SqlFormat)]
    [InlineData("", "db.query.text", "db.system.name", "sqlite", DashboardUIHelpers.SqlFormat)]
    [InlineData("MyApp.Database", "db.query.text", "db.system.name", "redis", null)]
    [InlineData("MyApp.Database", "db.statement", "db.system", "mongodb", null)]
    [InlineData("MyApp.Database", "commandText", "db.system.name", "custom", null)]
    [InlineData("MyApp.Database", "Message", "db.system.name", "postgresql", null)]
    [InlineData("MyApp.Database", "sql", "db.system.name", "redis", DashboardUIHelpers.SqlFormat)]
    public void GetLogFormat_OtherSourceWithDatabaseSystem_RespectsFieldAndDatabaseSystem(string source, string name, string systemKey, string system, string? expectedFormat)
    {
        Assert.Equal(expectedFormat, SqlHelpers.GetLogFormat(name, source, [KeyValuePair.Create(systemKey, system)]));
    }

    [Theory]
    [InlineData("db.system.name")]
    [InlineData("db.system")]
    public void GetLogFormat_OtherSourceWithEmptyDatabaseSystem_ReturnsNull(string systemKey)
    {
        Assert.Null(SqlHelpers.GetLogFormat("db.query.text", "MyApp.Database", [KeyValuePair.Create(systemKey, "")]));
    }

    [Fact]
    public void GetLogFormat_OtherSourceWithUnrelatedMetadata_ReturnsNull()
    {
        Assert.Null(SqlHelpers.GetLogFormat("db.query.text", "MyApp.Database", [KeyValuePair.Create("service.name", "postgresql")]));
    }

    [Theory]
    [InlineData("postgresql", "redis", DashboardUIHelpers.SqlFormat)]
    [InlineData("redis", "postgresql", null)]
    public void GetLogFormat_BothDatabaseSystems_PrefersCurrentConvention(string currentSystem, string legacySystem, string? expectedFormat)
    {
        Assert.Equal(expectedFormat, SqlHelpers.GetLogFormat("db.query.text", "MyApp.Database",
        [
            KeyValuePair.Create("db.system", legacySystem),
            KeyValuePair.Create("db.system.name", currentSystem)
        ]));
    }

    [Theory]
    [InlineData("commandText", "mssql", DashboardUIHelpers.SqlFormat)]
    [InlineData("db.statement", "mysql", DashboardUIHelpers.SqlFormat)]
    [InlineData("db.query.text", "postgresql", DashboardUIHelpers.SqlFormat)]
    [InlineData("db.query.text", "redis", null)]
    [InlineData("db.statement", "mongodb", null)]
    [InlineData("Message", "postgresql", null)]
    public void GetLogFormat_KnownSource_RespectsFieldAndDatabaseSystem(string name, string system, string? expectedFormat)
    {
        Assert.Equal(expectedFormat, SqlHelpers.GetLogFormat(name, "Microsoft.EntityFrameworkCore.Database.Command",
            [KeyValuePair.Create("db.system.name", system)]));
    }

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
