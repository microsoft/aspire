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

        Assert.True(vhost.Resource.TopologyReady.Task.IsCompletedSuccessfully);

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
    public async Task ProvisionTopologyAsync_SetsExceptionOnTopologyReady_WhenProvisioningFails()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var fakeClient = new FailingFakeRabbitMQProvisioningClient();
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(() => RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default));

        Assert.True(vhost.Resource.TopologyReady.Task.IsFaulted);
    }

}
