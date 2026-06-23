// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using Aspire.TestUtilities;
using Aspire.Hosting.Utils;
using NATS.Client.Core;
using Polly;

namespace Aspire.Hosting.Nats.Tests;

public class NatsClusterFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyNatsClusterResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var node = builder.AddNats("nats-1");
        var cluster = builder.AddNatsCluster("cluster").WithMember(node);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(cluster.Resource.Name, cts.Token);

        var connectionString = await cluster.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

        await pipeline.ExecuteAsync(async token =>
        {
            var client = new NatsConnection(new()
            {
                Url = connectionString!,
            });
            await client.ConnectAsync();
        }, cts.Token);

        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyMultiNodeNatsClusterResource()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new() { MaxRetryAttempts = 10, Delay = TimeSpan.FromSeconds(1) })
            .Build();

        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        var node1 = builder.AddNats("nats-1");
        var node2 = builder.AddNats("nats-2");
        var node3 = builder.AddNats("nats-3");
        var cluster = builder.AddNatsCluster("cluster")
            .WithMember(node1)
            .WithMember(node2)
            .WithMember(node3);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(cluster.Resource.Name, cts.Token);

        var connectionString = await cluster.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

        await pipeline.ExecuteAsync(async token =>
        {
            var client = new NatsConnection(new()
            {
                Url = connectionString!,
            });
            await client.ConnectAsync();
        }, cts.Token);

        await app.StopAsync();
    }
}
