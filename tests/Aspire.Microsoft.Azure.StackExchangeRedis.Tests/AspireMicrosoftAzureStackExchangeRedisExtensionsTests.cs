// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.StackExchange.Redis;
using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;
using Xunit;

namespace Aspire.Microsoft.Azure.StackExchangeRedis.Tests;

public class AspireMicrosoftAzureStackExchangeRedisExtensionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithAzureAuthenticationWorks(bool useKeyedRedis)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:redis", "redis-mj4npr46.eastus2.redis.azure.net:10000,ssl=true")
        ]);

        AspireRedisClientBuilder redisBuilder;
        if (useKeyedRedis)
        {
            redisBuilder = builder.AddKeyedRedisClientBuilder("redis");
        }
        else
        {
            redisBuilder = builder.AddRedisClientBuilder("redis");
        }

        redisBuilder.WithAzureAuthentication(new FakeTokenCredential());

        using var host = builder.Build();

        var configurationOptions = useKeyedRedis ?
            host.Services.GetRequiredService<IOptionsMonitor<ConfigurationOptions>>().Get("redis") :
            host.Services.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        // ensure the options was configured correctly
        Assert.NotNull(configurationOptions);
        Assert.Equal(FakeTokenCredential.Token, configurationOptions.Password);

        var defaults = configurationOptions.Defaults;
        Assert.IsType<IAzureCacheTokenEvents>(defaults, exactMatch: false);
        Assert.IsType<AzureOptionsProvider>(defaults, exactMatch: false);
    }

    [Fact]
    public void WithAzureAuthenticationNoopsWithNonAzure()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:redis", "localhost:59869,password=p@ssw0rd1")
        ]);

        builder.AddRedisClientBuilder("redis")
            .WithAzureAuthentication(new FakeTokenCredential());

        using var host = builder.Build();

        var configurationOptions = host.Services.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        // ensure the options was configured correctly
        Assert.NotNull(configurationOptions);
        Assert.Equal("p@ssw0rd1", configurationOptions.Password);

        var defaults = configurationOptions.Defaults;
        Assert.IsNotType<IAzureCacheTokenEvents>(defaults, exactMatch: false);
        Assert.IsNotType<AzureOptionsProvider>(defaults, exactMatch: false);
        Assert.IsNotType<AzureManagedRedisOptionsProvider>(defaults, exactMatch: false);
    }
}
