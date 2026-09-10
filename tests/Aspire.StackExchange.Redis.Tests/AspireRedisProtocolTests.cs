// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Aspire.StackExchange.Redis.Tests;

public class AspireRedisProtocolTests
{
    [Fact]
    public void BindsSupportedConfigurationOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:redis", "localhost"),
            new KeyValuePair<string, string?>("Aspire:StackExchange:Redis:ConfigurationOptions:ConnectTimeout", "3000"),
            new KeyValuePair<string, string?>("Aspire:StackExchange:Redis:ConfigurationOptions:HeartbeatInterval", "00:00:02"),
            new KeyValuePair<string, string?>("Aspire:StackExchange:Redis:ConfigurationOptions:Ssl", "true")
        ]);

        builder.AddRedisClient("redis");

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        Assert.Equal(3000, options.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.HeartbeatInterval);
        Assert.True(options.Ssl);
    }

    [Theory]
    [InlineData(null, RedisProtocol.Resp3)]
    [InlineData("resp2", RedisProtocol.Resp2)]
    public void UsesExpectedRespProtocol(string? protocol, RedisProtocol expectedProtocol)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionString = protocol is null ? "localhost" : $"localhost,protocol={protocol}";
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:redis", connectionString)
        ]);

        builder.AddRedisClient("redis");

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        Assert.Equal(expectedProtocol, options.Protocol ?? options.Defaults.Protocol);
    }
}
