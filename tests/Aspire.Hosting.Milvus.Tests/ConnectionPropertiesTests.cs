// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Milvus.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void MilvusServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var apiKey = new ParameterResource("apiKey", _ => "p@ssw0rd1", secret: true);
        var resource = new MilvusServerResource("milvus", apiKey);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{milvus.bindings.grpc.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{milvus.bindings.grpc.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Token", property.Key);
                Assert.Equal("root:{apiKey.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{milvus.bindings.grpc.url}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void MilvusDatabaseResourceGetConnectionPropertiesIncludesDatabaseSpecificValues()
    {
    var apiKey = new ParameterResource("apiKey", _ => "p@ssw0rd1", secret: true);
        var server = new MilvusServerResource("milvus", apiKey);
        var resource = new MilvusDatabaseResource("milvusDb", "Vectors", server);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Host" && property.Value.ValueExpression == "{milvus.bindings.grpc.host}");
        Assert.Contains(properties, property => property.Key == "Port" && property.Value.ValueExpression == "{milvus.bindings.grpc.port}");
        Assert.Contains(properties, property => property.Key == "Token" && property.Value.ValueExpression == "root:{apiKey.value}");
        Assert.Contains(properties, property => property.Key == "Uri" && property.Value.ValueExpression == "{milvus.bindings.grpc.url}");
        Assert.Contains(properties, property => property.Key == "DatabaseName" && property.Value.ValueExpression == "Vectors");
    }
}
