// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.Redis.Tests;

public class RedisReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder) =>
        builder.AddRedis("redis");

    [Theory]
    [InlineData(false, 6379)]
    [InlineData(true, 6380)]
    public async Task ReplUsesConfiguredPasswordAndLoopbackPort(bool tlsEnabled, int expectedPort)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var redis = builder.AddRedis("redis", password: password)
            .WithEndpoint("tcp", endpoint => endpoint.TlsEnabled = tlsEnabled);
        if (tlsEnabled)
        {
            redis.WithEndpoint(targetPort: 6380, name: "secondary");
        }

        var options = await RedisBuilderExtensions.CreateReplOptionsAsync(redis.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("redis-cli (redis)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "REDISCLI_AUTH", "container-id", "redis-cli",
            "-h", "127.0.0.1", "-p", expectedPort.ToString(System.Globalization.CultureInfo.InvariantCulture)], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("REDISCLI_AUTH", variable.Key);
            Assert.Equal("quotes'\" $; spaces", variable.Value);
        });
    }

    [Fact]
    public async Task ReplSupportsPasswordlessRedis()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var redis = builder.AddRedis("redis").WithPassword(null);

        var options = await RedisBuilderExtensions.CreateReplOptionsAsync(redis.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal(["exec", "-it", "container-id", "redis-cli", "-h", "127.0.0.1", "-p", "6379"], execOptions.Arguments);
        Assert.Empty(execOptions.EnvironmentVariables);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedCommand()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var password = builder.AddParameter("password", "repl-password", secret: true);
        var redis = builder.AddRedis("redis", password: password);
        await using var app = builder.Build();

        await VerifyReplAsync(app, redis.Resource, "redis-cli (redis)",
            "127.0.0.1:", "PING\r", "PONG");
    }
}
