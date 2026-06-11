// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Oracle.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void OracleDatabaseServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new OracleDatabaseServerResource("oracle", password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{oracle.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{oracle.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("system", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("oracle://system:{password.value}@{oracle.bindings.tcp.host}:{oracle.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:oracle:thin:@//{oracle.bindings.tcp.host}:{oracle.bindings.tcp.port}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void OracleDatabaseResourceGetConnectionPropertiesIncludesDatabaseSpecificValues()
    {
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var server = new OracleDatabaseServerResource("oracle", password);
        var resource = new OracleDatabaseResource("oracleDb", "Orders", server);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Host" && property.Value.ValueExpression == "{oracle.bindings.tcp.host}");
        Assert.Contains(properties, property => property.Key == "Port" && property.Value.ValueExpression == "{oracle.bindings.tcp.port}");
        Assert.Contains(properties, property => property.Key == "Username" && property.Value.ValueExpression == "system");
        Assert.Contains(properties, property => property.Key == "Password" && property.Value.ValueExpression == "{password.value}");
        Assert.Contains(properties, property => property.Key == "DatabaseName" && property.Value.ValueExpression == "Orders");
        Assert.Contains(
            properties,
            property => property.Key == "JdbcConnectionString" &&
                        property.Value.ValueExpression == "jdbc:oracle:thin:@//{oracle.bindings.tcp.host}:{oracle.bindings.tcp.port}/Orders");
    }

    [Fact]
    public async Task VerifyManifestWithConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();

        const string databaseName = "db#test";

        var server = builder.AddOracle("server");
        var database = server.AddDatabase("db", databaseName);

        // Force connection properties to be generated
        var app = builder.AddExecutable("app", "command", ".")
            .WithReference(server)
            .WithReference(database);

        var oracleManifest = await ManifestUtils.GetManifest(app.Resource);

        await Verify(oracleManifest.ToString(), "json");
    }

}
