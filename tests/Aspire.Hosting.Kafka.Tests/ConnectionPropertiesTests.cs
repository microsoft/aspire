// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kafka.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void KafkaServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var resource = new KafkaServerResource("kafka");

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{kafka.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{kafka.bindings.tcp.port}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void KafkaServerResourceGetConnectionPropertiesIncludesCredentialsWhenPasswordIsConfigured()
    {
        var user = new ParameterResource("user", _ => "kafkauser");
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new KafkaServerResource("kafka", user, password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{kafka.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{kafka.bindings.tcp.port}", property.Value.ValueExpression);
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
            });
    }

    [Fact]
    public void KafkaServerResourceUsesDefaultUserNameWhenNoUserNameParameterIsConfigured()
    {
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new KafkaServerResource("kafka", null, password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        var userName = Assert.Single(properties, p => p.Key == "Username");
        Assert.Equal("kafka", userName.Value.ValueExpression);
    }
}