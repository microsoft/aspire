// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.SqlServer.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void SqlServerServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
    var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new SqlServerServerResource("sql", password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.bindings.tcp.host}:{sql.bindings.tcp.port};trustServerCertificate=true", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{sql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mssql://sa:{password.value}@{sql.bindings.tcp.host}:{sql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("sa", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void SqlServerDatabaseResourceGetConnectionPropertiesIncludesDatabaseSpecificValues()
    {
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var server = new SqlServerServerResource("sql", password);
        var resource = new SqlServerDatabaseResource("sqlDb", "Orders", server);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("Orders", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.bindings.tcp.host}:{sql.bindings.tcp.port};databaseName=Orders;trustServerCertificate=true", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{sql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mssql://sa:{password.value}@{sql.bindings.tcp.host}:{sql.bindings.tcp.port}/Orders", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("sa", property.Value.ValueExpression);
            });
    }
}
