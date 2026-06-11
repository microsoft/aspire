// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.MySql.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void MySqlServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new MySqlServerResource("mysql", password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{mysql.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{mysql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("root", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mysql://root:{password.value}@{mysql.bindings.tcp.host}:{mysql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:mysql://{mysql.bindings.tcp.host}:{mysql.bindings.tcp.port}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void MySqlDatabaseResourceGetConnectionPropertiesIncludesDatabaseSpecificValues()
    {
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var server = new MySqlServerResource("mysql", password);
        var resource = new MySqlDatabaseResource("mysqlDb", "Orders", server);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Host" && property.Value.ValueExpression == "{mysql.bindings.tcp.host}");
        Assert.Contains(properties, property => property.Key == "Port" && property.Value.ValueExpression == "{mysql.bindings.tcp.port}");
        Assert.Contains(properties, property => property.Key == "Username" && property.Value.ValueExpression == "root");
        Assert.Contains(properties, property => property.Key == "Password" && property.Value.ValueExpression == "{password.value}");
        Assert.Contains(properties, property => property.Key == "DatabaseName" && property.Value.ValueExpression == "Orders");
        Assert.Contains(
            properties,
            property => property.Key == "Uri" &&
                        property.Value.ValueExpression == "mysql://root:{password.value}@{mysql.bindings.tcp.host}:{mysql.bindings.tcp.port}/Orders");
        Assert.Contains(
            properties,
            property => property.Key == "JdbcConnectionString" &&
                        property.Value.ValueExpression == "jdbc:mysql://{mysql.bindings.tcp.host}:{mysql.bindings.tcp.port}/Orders");
    }

    [Fact]
    public async Task VerifyManifestWithConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();

        const string databaseName = "db#test";

        var server = builder.AddMySql("server");
        var database = server.AddDatabase("db", databaseName);

        // Force connection properties to be generated
        var app = builder.AddExecutable("app", "command", ".")
            .WithReference(server)
            .WithReference(database);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        await Verify(manifest.ToString(), "json");
    }
}
