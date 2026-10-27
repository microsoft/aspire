// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeSymbolicWorkloadSocketTests
{
    [Fact]
    public async Task PublishedServerConsumesSecretAndConnectionReferencesAcrossRealRedisRevisions()
    {
        var executable = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_SERVER");
        var dcp = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_DCP");
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (executable is null || dcp is null || node is null)
        {
            Assert.Skip("This contract smoke requires explicitly supplied published server, DCP and Node executables; the dedicated native CI lane supplies them.");
        }
        var clock = TimeProvider.System;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
        var token = timeout.Token;
        await using var host = await NativeClientTestHost.StartWithWorkloadsAsync(executable, dcp);
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(15), clock, token);
        var workspace = await client.Server.CreateApplicationWorkspaceAsync(token);
        var firstSecret = RandomNumberGenerator.GetHexString(32);
        var secondSecret = RandomNumberGenerator.GetHexString(32);
        var first = await workspace.BeginRevisionAsync(token);
        var (redis, consumer, secretHandle) = await DeclareAsync(first, firstSecret, token);
        await workspace.CommitRevisionAsync(first, token);
        var execution = await workspace.GetApplicationExecutionAsync(token);
        var redisOwner = await client.Server.JoinResourceExecutionAsync(await execution.InviteResourceExecutionAsync(redis, token), token);
        var consumerOwner = await client.Server.JoinResourceExecutionAsync(await execution.InviteResourceExecutionAsync(consumer, token), token);
        var redisLaunch = new ContainerWorkloadLaunch
        {
            Image = "docker.io/library/redis:8.2-alpine", Entrypoint = "/bin/sh", Mounts = [], Environment = [],
            Arguments = ["-c", """exec redis-server --requirepass "$REDIS_PASSWORD" --save "" --appendonly no"""],
            Endpoints = [new WorkloadEndpointDeclaration
            {
                Name = "tcp", Protocol = "TCP", UriScheme = "redis", TargetPort = 6379, HostPort = 0, EnvironmentVariable = ""
            }]
        };
        var redisBindings = new SymbolicLaunchBindings
        {
            Environment = [new SymbolicLaunchEnvironment { Name = "REDIS_PASSWORD", ConfigurationKey = "password" }], Arguments = []
        };
        var consumerLaunch = new ExecutableWorkloadLaunch
        {
            ExecutablePath = node, WorkingDirectory = AppContext.BaseDirectory, Environment = [],
            Arguments = [Path.Combine(AppContext.BaseDirectory, "AotSmoke", "SymbolicRedisConsumer.mts")],
            Endpoints = [new WorkloadEndpointDeclaration
            {
                Name = "http", Protocol = "TCP", UriScheme = "http", TargetPort = 0, HostPort = 0, EnvironmentVariable = "PORT"
            }]
        };
        var consumerBindings = new SymbolicLaunchBindings
        {
            Environment = [new SymbolicLaunchEnvironment { Name = "NATIVE_REDIS_URI", ConfigurationKey = "connection" }], Arguments = []
        };
        try
        {
            var redisAllocation = await redisOwner.StartContainerWithValuesAsync(1, "container", redisLaunch, redisBindings, token);
            var redisEndpoint = Assert.Single(redisAllocation.Endpoints);
            var waitingConsumer = consumerOwner.StartExecutableWithValuesAsync(1, "host", consumerLaunch, consumerBindings, token);
            Assert.False(waitingConsumer.IsCompleted);
            await RedisProtocolTestClient.WaitForAuthenticatedPingAsync(redisEndpoint, firstSecret, clock, token);
            Assert.False(await RedisProtocolTestClient.PingAsync(redisEndpoint, null, token));
            await redisOwner.CompleteResourceConfigurationAsync(1, new RuntimeOperationResult { Status = "succeeded", Message = "Authenticated Redis is ready." }, token);
            await redisOwner.PublishObservationAsync(new ResourceObservation { State = "Running", Healthy = true, Urls = [] }, token);
            var consumerAllocation = await waitingConsumer;
            await VerifyConsumerAsync(consumerAllocation, clock, token);

            var revision = await workspace.BeginRevisionAsync(token);
            await DeclareAsync(revision, secondSecret, token);
            await workspace.CommitRevisionAsync(revision, token);
            var stale = await Assert.ThrowsAsync<NativeCapabilityException>(() =>
                consumerOwner.RestartExecutableWithValuesAsync(1, "host", consumerLaunch, consumerBindings, token));
            Assert.Equal("VALUE_REVISION_STALE", stale.Code);
            var revoked = await Assert.ThrowsAsync<NativeCapabilityException>(() => secretHandle.EncodeUriValueAsync(token));
            Assert.Equal("HANDLE_NOT_FOUND", revoked.Code);
            var redisRevision = (await redisOwner.ReadResourceConfigurationAsync(token)).Revision;
            var replacement = await redisOwner.RestartContainerWithValuesAsync(redisRevision, "container", redisLaunch, redisBindings, token);
            var replacementEndpoint = Assert.Single(replacement.Endpoints);
            await RedisProtocolTestClient.WaitForAuthenticatedPingAsync(replacementEndpoint, secondSecret, clock, token);
            Assert.False(await RedisProtocolTestClient.PingAsync(replacementEndpoint, firstSecret, token));
            await redisOwner.CompleteResourceConfigurationAsync(redisRevision, new RuntimeOperationResult { Status = "succeeded", Message = "Updated Redis is ready." }, token);
            await redisOwner.PublishObservationAsync(new ResourceObservation { State = "Running", Healthy = true, Urls = [] }, token);
            var consumerRevision = (await consumerOwner.ReadResourceConfigurationAsync(token)).Revision;
            var updatedConsumer = await consumerOwner.RestartExecutableWithValuesAsync(consumerRevision, "host", consumerLaunch, consumerBindings, token);
            await VerifyConsumerAsync(updatedConsumer, clock, token);
            Assert.Empty((await consumerOwner.ReadResourceConfigurationAsync(token)).Properties);
            Assert.True(Assert.Single((await consumerOwner.InspectExecutionSymbolicConfigurationAsync(token)).Properties).IsSecret);
        }
        finally
        {
            await workspace.RetireApplicationWorkspaceAsync();
        }
    }

    private static async Task<(DeclaredResource Redis, DeclaredResource Consumer, DeclaredValue Secret)> DeclareAsync(
        Composition composition, string password, CancellationToken cancellationToken)
    {
        var redis = await composition.AddResourceAsync("cache", "example/redis", cancellationToken);
        var consumer = await composition.AddResourceAsync("consumer", "example/executable", cancellationToken);
        await consumer.WaitForAsync(redis, cancellationToken);
        var secret = await composition.AddSecretParameterAsync("cache-password", password, cancellationToken);
        await redis.SetSymbolicConfigurationAsync("password", secret, cancellationToken);
        var connection = await composition.CreateLiteralValueAsync("redis://:", cancellationToken);
        connection = await connection.ConcatValueAsync(await secret.EncodeUriValueAsync(cancellationToken), cancellationToken);
        connection = await connection.ConcatValueAsync(await composition.CreateLiteralValueAsync("@", cancellationToken), cancellationToken);
        connection = await connection.ConcatValueAsync(await composition.CreateEndpointValueAsync(redis, "tcp", "hostAndPort", cancellationToken), cancellationToken);
        await consumer.SetSymbolicConfigurationAsync("connection", connection, cancellationToken);

        return (redis, consumer, secret);
    }

    private static async Task VerifyConsumerAsync(WorkloadAllocation allocation, TimeProvider clock, CancellationToken cancellationToken)
    {
        var endpoint = Assert.Single(allocation.Endpoints);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var address = new UriBuilder("http", endpoint.Host, endpoint.Port).Uri;
        while (true)
        {
            try
            {
                var response = await http.GetStringAsync(address, cancellationToken);
                Assert.Equal("verified\n", response);

                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, cancellationToken);
            }
        }
    }
}
