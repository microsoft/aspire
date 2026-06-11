// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class DistributedApplicationBuilderExtensionsTests
{
    [Fact]
    public void CreateResourceBuilderByNameRequiresExistingResource()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var missingException = Assert.Throws<InvalidOperationException>(() => appBuilder.CreateResourceBuilder<RedisResource>("non-existent-resource"));
        Assert.Contains("not found", missingException.Message);
    }

    [Fact]
    public void CreateResourceBuilderByNameRequiresCompatibleType()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var originalRedis = appBuilder.AddRedis("redis");
        var incorrectTypeException = Assert.Throws<InvalidOperationException>(() => appBuilder.CreateResourceBuilder<PostgresServerResource>("redis"));
        Assert.Contains("not assignable", incorrectTypeException.Message);
    }

    [Fact]
    public void CreateResourceBuilderByNameSupportsUpCast()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var originalRedis = appBuilder.AddRedis("redis");

        // RedisResource implements ContainerResource, so this is acceptable.
        var newRedisBuilder = appBuilder.CreateResourceBuilder<ContainerResource>("redis");
        Assert.Same(originalRedis.Resource, newRedisBuilder.Resource);
    }

    [Fact]
    public void CreateResourceBuilderByReturnsSameResourceInstance()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var originalRedis = appBuilder.AddRedis("redis");
        var newRedisBuilder = appBuilder.CreateResourceBuilder<RedisResource>("redis");
        Assert.Same(originalRedis.Resource, newRedisBuilder.Resource);
    }

    [Fact]
    public void TryCreateResourceBuilderReturnsFalseForNonExistentResource()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var result = appBuilder.TryCreateResourceBuilder<RedisResource>("non-existent-resource", out var builder);
        Assert.False(result);
        Assert.Null(builder);
    }

    [Fact]
    public void TryCreateResourceBuilderReturnsFalseForIncompatibleType()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.AddRedis("redis");
        var result = appBuilder.TryCreateResourceBuilder<PostgresServerResource>("redis", out var builder);
        Assert.False(result);
        Assert.Null(builder);
    }

    [Fact]
    public void TryCreateResourceBuilderSupportsUpCast()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var originalRedis = appBuilder.AddRedis("redis");

        // RedisResource implements ContainerResource, so this is acceptable.
        var result = appBuilder.TryCreateResourceBuilder<ContainerResource>("redis", out var newRedisBuilder);
        Assert.True(result);
        Assert.NotNull(newRedisBuilder);
        Assert.Same(originalRedis.Resource, newRedisBuilder.Resource);
    }

    [Fact]
    public void TryCreateResourceBuilderReturnsSameResourceInstance()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var originalRedis = appBuilder.AddRedis("redis");
        var result = appBuilder.TryCreateResourceBuilder<RedisResource>("redis", out var newRedisBuilder);
        Assert.True(result);
        Assert.NotNull(newRedisBuilder);
        Assert.Same(originalRedis.Resource, newRedisBuilder.Resource);
    }
}
