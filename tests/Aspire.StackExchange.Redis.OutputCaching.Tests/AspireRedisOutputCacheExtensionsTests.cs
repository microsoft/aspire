// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.StackExchange.Redis.OutputCaching.Tests;

public class AspireRedisOutputCacheExtensionsTests
{
    [Fact]
    public void AddsRedisOutputCacheCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddRedisOutputCache("redis");

        using var host = builder.Build();
        var cacheStore = host.Services.GetRequiredService<IOutputCacheStore>();

        // note the RedisOutputCacheStore is internal
        Assert.StartsWith("Redis", cacheStore.GetType().Name);
    }

    [Fact]
    public void AddsRedisBuilderOutputCacheCorrectly()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddRedisClientBuilder("redis").WithOutputCache();

        using var host = builder.Build();
        var cacheStore = host.Services.GetRequiredService<IOutputCacheStore>();

        // note the RedisOutputCacheStore is internal
        Assert.StartsWith("Redis", cacheStore.GetType().Name);
    }

#if NET11_0_OR_GREATER
    [Theory]
    [InlineData("plain")]
    [InlineData("keyed")]
    [InlineData("builder")]
    public void RegistersOutputCachePolicyProvider(string api)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration["ConnectionStrings:redis"] = "localhost:6379,abortConnect=false";

        switch (api)
        {
            case "plain":   builder.AddRedisOutputCache("redis"); break;
            case "keyed":   builder.AddKeyedRedisOutputCache("redis"); break;
            case "builder": builder.AddRedisClientBuilder("redis").WithOutputCache(); break;
        }

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<IOutputCachePolicyProvider>());
    }
#endif

}
