// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.MongoDB.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void MongoDbServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var user = new ParameterResource("user", _ => "mongoUser");
    var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new MongoDBServerResource("mongo", user, password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{mongo.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{mongo.bindings.tcp.port}", property.Value.ValueExpression);
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
                Assert.Equal("AuthenticationDatabase", property.Key);
                Assert.Equal("admin", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("AuthenticationMechanism", property.Key);
                Assert.Equal("SCRAM-SHA-256", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mongodb://{user.value}:{password.value}@{mongo.bindings.tcp.host}:{mongo.bindings.tcp.port}?authSource=admin&authMechanism=SCRAM-SHA-256", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void MongoDbDatabaseResourceGetConnectionPropertiesIncludesDatabaseSpecificValues()
    {
        var user = new ParameterResource("user", _ => "mongoUser");
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var server = new MongoDBServerResource("mongo", user, password);
        var resource = new MongoDBDatabaseResource("mongoDb", "Products", server);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Host" && property.Value.ValueExpression == "{mongo.bindings.tcp.host}");
        Assert.Contains(properties, property => property.Key == "Port" && property.Value.ValueExpression == "{mongo.bindings.tcp.port}");
        Assert.Contains(properties, property => property.Key == "Username" && property.Value.ValueExpression == "{user.value}");
        Assert.Contains(properties, property => property.Key == "Password" && property.Value.ValueExpression == "{password.value}");
        Assert.Contains(properties, property => property.Key == "AuthenticationDatabase" && property.Value.ValueExpression == "admin");
        Assert.Contains(properties, property => property.Key == "AuthenticationMechanism" && property.Value.ValueExpression == "SCRAM-SHA-256");
        Assert.Contains(properties, property => property.Key == "DatabaseName" && property.Value.ValueExpression == "Products");
        Assert.Contains(
            properties,
            property => property.Key == "Uri" &&
                        property.Value.ValueExpression == "mongodb://{user.value}:{password.value}@{mongo.bindings.tcp.host}:{mongo.bindings.tcp.port}/Products?authSource=admin&authMechanism=SCRAM-SHA-256");
    }

    [Fact]
    public async Task VerifyManifestWithConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();

        const string databaseName = "db#test";

        var server = builder.AddMongoDB("server");
        var database = server.AddDatabase("db", databaseName);

        var serverWithParameters = builder.AddMongoDB(
            "serverWithParameters",
            userName: builder.AddParameter("username", "some#User"),
            password: builder.AddParameter("password", "p@ssw0rd!", secret: true));
        var databaseWithParameters = serverWithParameters.AddDatabase("dbWithParameters", databaseName);

        // Force connection properties to be generated
        var app = builder.AddExecutable("app", "command", ".")
            .WithReference(server)
            .WithReference(database)
            .WithReference(serverWithParameters)
            .WithReference(databaseWithParameters);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        await Verify(manifest.ToString(), "json");
    }
}
