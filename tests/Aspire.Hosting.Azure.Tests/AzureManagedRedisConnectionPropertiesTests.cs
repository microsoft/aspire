// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureManagedRedisConnectionPropertiesTests
{
    [Fact]
    public void AzureManagedRedisResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var redis = builder.AddAzureManagedRedis("redis");

        var properties = ((IResourceWithConnectionString)redis.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{redis.outputs.hostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("10000", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("rediss://{redis.outputs.hostName}:10000", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureManagedRedisResourceWithAccessKeyAuthenticationGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var redis = builder.AddAzureManagedRedis("redis").WithAccessKeyAuthentication();

        var properties = ((IResourceWithConnectionString)redis.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{redis.outputs.hostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("10000", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("rediss://:{redis-kv.secrets.primaryaccesskey--redis}@{redis.outputs.hostName}:10000", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{redis-kv.secrets.primaryaccesskey--redis}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureRedisCacheResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
#pragma warning disable CS0618 // Type or member is obsolete
        var redis = builder.AddAzureRedis("redis");
#pragma warning restore CS0618

        var properties = ((IResourceWithConnectionString)redis.Resource).GetConnectionProperties().ToArray();

        // Not implemented for the obsolete resource
        Assert.Empty(properties);
    }

    [Fact]
    public void AzureRedisCacheResourceWithAccessKeyAuthenticationGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
#pragma warning disable CS0618 // Type or member is obsolete
        var redis = builder.AddAzureRedis("redis").WithAccessKeyAuthentication();
#pragma warning restore CS0618

        var properties = ((IResourceWithConnectionString)redis.Resource).GetConnectionProperties().ToArray();

        // Not implemented for the obsolete resource
        Assert.Empty(properties);
    }
}
