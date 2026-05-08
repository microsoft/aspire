// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQTopologyProvisionerTests
{
    [Fact]
    public async Task ProvisionTopologyAsync_AppliesResourcesInCorrectOrder()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var exchange = vhost.AddExchange("myexchange");
        var queue = vhost.AddQueue("myqueue");
        exchange.WithBinding(queue, "myroutingkey");
        var shovel = vhost.AddShovel("myshovel", queue, exchange);

        var fakeClient = new FakeRabbitMQProvisioningClient();
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();

        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        Assert.True(((IRabbitMQProvisionable)vhost.Resource).ProvisionedTask.IsCompletedSuccessfully);
        Assert.True(((IRabbitMQProvisionable)queue.Resource).ProvisionedTask.IsCompletedSuccessfully);
        Assert.True(((IRabbitMQProvisionable)exchange.Resource).ProvisionedTask.IsCompletedSuccessfully);
        Assert.True(((IRabbitMQProvisionable)shovel.Resource).ProvisionedTask.IsCompletedSuccessfully);

        // CreateVirtualHostAsync must be first
        Assert.Equal("CreateVirtualHostAsync(myvhost)", fakeClient.Calls[0]);

        // Exchange and queue declares happen in phase 2 (parallel — order may vary)
        Assert.Contains(fakeClient.Calls, c => c == "DeclareExchangeAsync(myvhost, myexchange, direct, True, False)");
        Assert.Contains(fakeClient.Calls, c => c == "DeclareQueueAsync(myvhost, myqueue, True, False, False)");

        // Binding and shovel happen in phase 3 (after phase 2)
        var exchangeDeclareIndex = fakeClient.Calls.IndexOf("DeclareExchangeAsync(myvhost, myexchange, direct, True, False)");
        var queueDeclareIndex = fakeClient.Calls.IndexOf("DeclareQueueAsync(myvhost, myqueue, True, False, False)");
        var lastDeclareIndex = Math.Max(exchangeDeclareIndex, queueDeclareIndex);

        var bindIndex = fakeClient.Calls.FindIndex(c => c == "BindQueueAsync(myvhost, myexchange, myqueue, myroutingkey)");
        var shovelIndex = fakeClient.Calls.FindIndex(c => c.StartsWith("PutShovelAsync(myvhost, myshovel,"));

        Assert.True(bindIndex > lastDeclareIndex, "BindQueueAsync must come after all declares");
        Assert.True(shovelIndex > lastDeclareIndex, "PutShovelAsync must come after all declares");
    }

    [Fact]
    public async Task ProvisionTopologyAsync_VhostFails_FaultsVhostAndChildrenStayPending()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("myqueue");
        var exchange = vhost.AddExchange("myexchange");

        var fakeClient = new FailingFakeRabbitMQProvisioningClient();
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();

        // Provisioner no longer throws — it captures failures into TCSs
        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        // Vhost itself is faulted
        Assert.True(((IRabbitMQProvisionable)vhost.Resource).ProvisionedTask.IsFaulted);

        // Children stay pending (Starting) — no cascade fault
        Assert.False(((IRabbitMQProvisionable)queue.Resource).ProvisionedTask.IsCompleted);
        Assert.False(((IRabbitMQProvisionable)exchange.Resource).ProvisionedTask.IsCompleted);
    }

    [Fact]
    public async Task ProvisionTopologyAsync_QueueBFails_OnlyQueueBTCSFaulted()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var queueA = vhost.AddQueue("queueA");
        var queueB = vhost.AddQueue("queueB");

        var fakeClient = new FakeRabbitMQProvisioningClient();
        fakeClient.FailQueueNames.Add("queueB");
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();

        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        // Queue A succeeded
        Assert.True(((IRabbitMQProvisionable)queueA.Resource).ProvisionedTask.IsCompletedSuccessfully);
        // Queue B failed
        Assert.True(((IRabbitMQProvisionable)queueB.Resource).ProvisionedTask.IsFaulted);
        // Vhost itself succeeded
        Assert.True(((IRabbitMQProvisionable)vhost.Resource).ProvisionedTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ProvisionTopologyAsync_BindingFails_OnlyExchangeTCSFaulted_DestQueueUnaffected()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var exchange = vhost.AddExchange("myexchange");
        var queue = vhost.AddQueue("myqueue");
        exchange.WithBinding(queue, "key");

        var fakeClient = new FakeRabbitMQProvisioningClient();
        fakeClient.FailBindingSourceExchangeNames.Add("myexchange");
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();

        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        // Exchange is faulted (binding failed)
        Assert.True(((IRabbitMQProvisionable)exchange.Resource).ProvisionedTask.IsFaulted);
        // Destination queue is unaffected — it declared successfully
        Assert.True(((IRabbitMQProvisionable)queue.Resource).ProvisionedTask.IsCompletedSuccessfully);
        // Vhost itself succeeded
        Assert.True(((IRabbitMQProvisionable)vhost.Resource).ProvisionedTask.IsCompletedSuccessfully);
    }
}
