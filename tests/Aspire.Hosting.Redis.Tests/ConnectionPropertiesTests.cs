// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Redis.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void RedisResourceGetConnectionPropertiesReturnsExpectedValues()
    {
    var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new RedisResource("redis", password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();
        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{redis.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{redis.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("redis://:{password.value}@{redis.bindings.tcp.host}:{redis.bindings.tcp.port}", property.Value.ValueExpression);
            });
    }
}
