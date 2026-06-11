// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.StackExchange.Redis.Tests;
using Aspire.TestUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Xunit;

namespace Aspire.StackExchange.Redis.DistributedCaching.Tests;

public class AspireRedisDistributedCacheExtensionsTests
{
    [Fact]
    public void AddsRedisDistributedCacheCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddRedisDistributedCache("redis");

        using var host = builder.Build();
        var cache = host.Services.GetRequiredService<IDistributedCache>();

        Assert.IsAssignableFrom<RedisCache>(cache);
    }

    [Fact]
    public void AddsRedisBuilderDistributedCacheCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddRedisClientBuilder("redis").WithDistributedCache();

        using var host = builder.Build();
        var cache = host.Services.GetRequiredService<IDistributedCache>();

        Assert.IsAssignableFrom<RedisCache>(cache);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task AddsRedisBuilderDistributedCacheCanConfigure()
    {
        await using var container = await RedisContainerFixture.CreateContainerAsync();

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:redis", container.GetConnectionString()),
        ]);

        builder.AddRedisClientBuilder("redis")
            .WithDistributedCache(options => options.InstanceName = "myCache");

        using var host = builder.Build();
        var distributedCache = host.Services.GetRequiredService<IDistributedCache>();

        distributedCache.SetString("key", "value");

        var connection = host.Services.GetRequiredService<IConnectionMultiplexer>();
        var key = Assert.Single(connection.GetServers().Single().Keys());
        Assert.StartsWith("myCache", key);
    }

    /// <summary>
    /// Tests that you can use keyed services for distributed caches.
    /// </summary>
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task CanAddMultipleKeyedCachingServicesBuilder()
    {
        await using var container1 = await RedisContainerFixture.CreateContainerAsync();
        await using var container2 = await RedisContainerFixture.CreateContainerAsync();
        await using var container3 = await RedisContainerFixture.CreateContainerAsync();

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:redis1", container1.GetConnectionString()),
            new KeyValuePair<string, string?>("ConnectionStrings:redis2", container2.GetConnectionString()),
            new KeyValuePair<string, string?>("ConnectionStrings:redis3", container3.GetConnectionString()),
        ]);

        builder.AddRedisClientBuilder("redis1")
            .WithKeyedDistributedCache("dcache1", options => options.InstanceName = "dcache1");
        builder.AddKeyedRedisClientBuilder("redis2")
            .WithKeyedDistributedCache("dcache2"); // don't configure the options to ensure configuring separately works
        builder.AddKeyedRedisClientBuilder("redis3")
            .WithKeyedDistributedCache("dcache3", options => options.InstanceName = "dcache3");

        using var host = builder.Build();

        var connection1 = host.Services.GetRequiredService<IConnectionMultiplexer>();
        var connection2 = host.Services.GetRequiredKeyedService<IConnectionMultiplexer>("redis2");
        var connection3 = host.Services.GetRequiredKeyedService<IConnectionMultiplexer>("redis3");
        var dcache1 = host.Services.GetRequiredKeyedService<IDistributedCache>("dcache1");
        var dcache2 = host.Services.GetRequiredKeyedService<IDistributedCache>("dcache2");
        var dcache3 = host.Services.GetRequiredKeyedService<IDistributedCache>("dcache3");

        Assert.NotSame(connection1, connection2);
        Assert.NotSame(connection1, connection3);
        Assert.NotSame(connection2, connection3);
        Assert.NotSame(dcache1, dcache2);
        Assert.NotSame(dcache1, dcache3);
        Assert.NotSame(dcache2, dcache3);

        Assert.Equal(container1.GetConnectionString(), connection1.Configuration);
        Assert.Equal(container2.GetConnectionString(), connection2.Configuration);
        Assert.Equal(container3.GetConnectionString(), connection3.Configuration);

        // set a value in the first distributed cache and ensure it is only in the redis1 server
        dcache1.SetString("key1", "value1");

        var key = Assert.Single(connection1.GetServers().Single().Keys());
        Assert.Equal("dcache1key1", key);
        Assert.Empty(connection2.GetServers().Single().Keys());
        Assert.Empty(connection3.GetServers().Single().Keys());

        // set a value in the second distributed cache and ensure it is only in the redis2 server
        dcache2.SetString("key2", "value2");

        key = Assert.Single(connection1.GetServers().Single().Keys());
        Assert.Equal("dcache1key1", key);
        key = Assert.Single(connection2.GetServers().Single().Keys());
        Assert.Equal("key2", key);
        Assert.Empty(connection3.GetServers().Single().Keys());

        // set a value in the third distributed cache and ensure it is only in the redis3 server
        dcache3.SetString("key3", "value3");

        key = Assert.Single(connection1.GetServers().Single().Keys());
        Assert.Equal("dcache1key1", key);
        key = Assert.Single(connection2.GetServers().Single().Keys());
        Assert.Equal("key2", key);
        key = Assert.Single(connection3.GetServers().Single().Keys());
        Assert.Equal("dcache3key3", key);
    }
}
