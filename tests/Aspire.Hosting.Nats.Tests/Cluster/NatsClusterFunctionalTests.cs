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

        var node = builder.AddNats("nats-1").WithJetStream(); // NOTE: JetStream is needed in order to verify the cluster's mechanisms in the assert phase of the tests.
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

        var node1 = builder.AddNats("nats-1").WithJetStream(); // NOTE: JetStream is needed in order to verify the cluster's mechanisms in the assert phase of the tests.
        var node2 = builder.AddNats("nats-2").WithJetStream();
        var node3 = builder.AddNats("nats-3").WithJetStream();
        var cluster = builder.AddNatsCluster("cluster")
            .WithMember(node1)
            .WithMember(node2)
            .WithMember(node3);

        using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(cluster.Resource.Name, cts.Token);

        var connectionString = await cluster.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

        // NOTE: This the most reliable way to verify that the cluster is functional. We publish a message through one node and subscribe on a different node, proving the message crosses the cluster boundary
        await pipeline.ExecuteAsync(async token =>
        {
            var node1ConnectionString = await node1.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);
            var node2ConnectionString = await node2.Resource.ConnectionStringExpression.GetValueAsync(cts.Token);

            await using var node1Connection = new NatsConnection(new() { Url = node1ConnectionString! });
            await using var node2Connection = new NatsConnection(new() { Url = node2ConnectionString! });

            await node1Connection.ConnectAsync();
            await node2Connection.ConnectAsync();

            var subscribedSignal = new TaskCompletionSource();
            var subscriptionTask = Task.Run(async () =>
            {
                var subscription = await node2Connection.SubscribeCoreAsync<string>("test.subject", cancellationToken: cts.Token);
                await node2Connection.PingAsync(cts.Token); // NOTE: See https://docs.nats.io/using-nats/developer/sending/caches
                subscribedSignal.SetResult();

                await foreach (var msg in subscription.Msgs.ReadAllAsync(cts.Token))
                {
                    break;
                }
            }, cts.Token);

            await subscribedSignal.Task;
            await node1Connection.PublishAsync("test.subject", "hello from node 1", cancellationToken: cts.Token);

            await subscriptionTask;
        }, cts.Token);

        await app.StopAsync();
    }
}
