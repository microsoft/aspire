// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.PostgreSQL.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void PostgresServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var user = new ParameterResource("user", _ => "pgadmin");
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new PostgresServerResource("postgres", user, password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{postgres.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{postgres.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("{user.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("postgresql://{user.value}:{password.value}@{postgres.bindings.tcp.host}:{postgres.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:postgresql://{postgres.bindings.tcp.host}:{postgres.bindings.tcp.port}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void PostgresDatabaseResourceGetConnectionPropertiesIncludesDatabaseSpecificValues()
    {
        var user = new ParameterResource("user", _ => "pgadmin");
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var server = new PostgresServerResource("postgres", user, password);
        var resource = new PostgresDatabaseResource("postgresDb", "Customers", server);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Host" && property.Value.ValueExpression == "{postgres.bindings.tcp.host}");
        Assert.Contains(properties, property => property.Key == "Port" && property.Value.ValueExpression == "{postgres.bindings.tcp.port}");
        Assert.Contains(properties, property => property.Key == "Username" && property.Value.ValueExpression == "{user.value}");
        Assert.Contains(properties, property => property.Key == "Password" && property.Value.ValueExpression == "{password.value}");
        Assert.Contains(properties, property => property.Key == "DatabaseName" && property.Value.ValueExpression == "Customers");
        Assert.Contains(
            properties,
            property => property.Key == "Uri" &&
                        property.Value.ValueExpression == "postgresql://{user.value}:{password.value}@{postgres.bindings.tcp.host}:{postgres.bindings.tcp.port}/Customers");
        Assert.Contains(
            properties,
            property => property.Key == "JdbcConnectionString" &&
                        property.Value.ValueExpression == "jdbc:postgresql://{postgres.bindings.tcp.host}:{postgres.bindings.tcp.port}/Customers");
    }
}
