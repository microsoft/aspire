// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class AddRabbitMQChildResourcesTests
{
    [Fact]
    public void AddVirtualHost_CreatesResourceAndAddsToParent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        var vhost = server.AddVirtualHost("myvhost");

        Assert.Single(server.Resource.VirtualHosts);
        Assert.Equal(vhost.Resource, server.Resource.VirtualHosts[0]);
        Assert.Equal("myvhost", vhost.Resource.Name);
        Assert.Equal("myvhost", vhost.Resource.VirtualHostName);
        Assert.Equal(server.Resource, vhost.Resource.Parent);
    }

    [Fact]
    public void AddVirtualHost_WithCustomName_CreatesResourceAndAddsToParent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        var vhost = server.AddVirtualHost("myvhost", "custom-vhost");

        Assert.Single(server.Resource.VirtualHosts);
        Assert.Equal(vhost.Resource, server.Resource.VirtualHosts[0]);
        Assert.Equal("myvhost", vhost.Resource.Name);
        Assert.Equal("custom-vhost", vhost.Resource.VirtualHostName);
        Assert.Equal(server.Resource, vhost.Resource.Parent);
    }

    [Fact]
    public void AddVirtualHost_NonDefault_EnablesManagementPlugin()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        server.AddVirtualHost("myvhost");

        var endpoints = server.Resource.Annotations.OfType<EndpointAnnotation>();
        Assert.Contains(endpoints, e => e.Name == RabbitMQServerResource.ManagementEndpointName);
    }

    [Fact]
    public void AddVirtualHost_Default_DoesNotEnableManagementPlugin()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        server.AddVirtualHost("default", "/");

        var endpoints = server.Resource.Annotations.OfType<EndpointAnnotation>();
        Assert.DoesNotContain(endpoints, e => e.Name == RabbitMQServerResource.ManagementEndpointName);
    }

    [Fact]
    public void GetOrAddDefaultVirtualHost_IsIdempotent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        var vhost1 = server.GetOrAddDefaultVirtualHost();
        var vhost2 = server.GetOrAddDefaultVirtualHost();

        Assert.Same(vhost1, vhost2);
        Assert.Single(server.Resource.VirtualHosts);
        Assert.Equal("/", vhost1.VirtualHostName);
    }

    [Fact]
    public void AddQueue_OnVirtualHost_CreatesResourceAndAddsToParent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var queue = vhost.AddQueue("myqueue");

        Assert.Single(vhost.Resource.Queues);
        Assert.Equal(queue.Resource, vhost.Resource.Queues[0]);
        Assert.Equal("myqueue", queue.Resource.Name);
        Assert.Equal("myqueue", queue.Resource.QueueName);
        Assert.Equal(vhost.Resource, queue.Resource.Parent);
        Assert.Equal(RabbitMQQueueType.Classic, queue.Resource.QueueType);
    }

    [Fact]
    public void AddQueue_OnServer_CreatesDefaultVirtualHostAndAddsQueue()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        var queue = server.AddQueue("myqueue");

        Assert.Single(server.Resource.VirtualHosts);
        var vhost = server.Resource.VirtualHosts[0];
        Assert.Equal("/", vhost.VirtualHostName);

        Assert.Single(vhost.Queues);
        Assert.Equal(queue.Resource, vhost.Queues[0]);
    }

    [Fact]
    public void AddExchange_OnVirtualHost_CreatesResourceAndAddsToParent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var exchange = vhost.AddExchange("myexchange");

        Assert.Single(vhost.Resource.Exchanges);
        Assert.Equal(exchange.Resource, vhost.Resource.Exchanges[0]);
        Assert.Equal("myexchange", exchange.Resource.Name);
        Assert.Equal("myexchange", exchange.Resource.ExchangeName);
        Assert.Equal(vhost.Resource, exchange.Resource.Parent);
        Assert.Equal(RabbitMQExchangeType.Direct, exchange.Resource.ExchangeType);
    }

    [Fact]
    public void AddExchange_OnServer_CreatesDefaultVirtualHostAndAddsExchange()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        var exchange = server.AddExchange("myexchange");

        Assert.Single(server.Resource.VirtualHosts);
        var vhost = server.Resource.VirtualHosts[0];
        Assert.Equal("/", vhost.VirtualHostName);

        Assert.Single(vhost.Exchanges);
        Assert.Equal(exchange.Resource, vhost.Exchanges[0]);
    }

    [Fact]
    public void WithBinding_AddsBindingToExchange()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var exchange = vhost.AddExchange("myexchange");
        var queue = vhost.AddQueue("myqueue");

        exchange.WithBinding(queue, "myroutingkey");

        Assert.Single(exchange.Resource.Bindings);
        var binding = exchange.Resource.Bindings[0];
        Assert.Equal(queue.Resource, binding.Destination);
        Assert.Equal("myroutingkey", binding.RoutingKey);
    }

    [Fact]
    public void WithBinding_CrossVirtualHost_ThrowsException()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost1 = server.AddVirtualHost("vhost1");
        var vhost2 = server.AddVirtualHost("vhost2");
        var exchange = vhost1.AddExchange("myexchange");
        var queue = vhost2.AddQueue("myqueue");

        var ex = Assert.Throws<DistributedApplicationException>(() => exchange.WithBinding(queue, "myroutingkey"));
        Assert.Contains("different virtual hosts", ex.Message);
    }

    [Fact]
    public void AddShovel_OnVirtualHost_CreatesResourceAndAddsToParent()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost1 = server.AddVirtualHost("vhost1");
        var vhost2 = server.AddVirtualHost("vhost2");
        var queue1 = vhost1.AddQueue("queue1");
        var queue2 = vhost2.AddQueue("queue2");

        var shovel = vhost1.AddShovel("myshovel", queue1, queue2);

        Assert.Single(vhost1.Resource.Shovels);
        Assert.Equal(shovel.Resource, vhost1.Resource.Shovels[0]);
        Assert.Equal("myshovel", shovel.Resource.Name);
        Assert.Equal("myshovel", shovel.Resource.ShovelName);
        Assert.Equal(vhost1.Resource, shovel.Resource.Parent);
        Assert.Equal(queue1.Resource, shovel.Resource.Source.Target);
        Assert.Equal(queue2.Resource, shovel.Resource.Destination.Target);
    }

    [Fact]
    public void AddShovel_SourceInDifferentVirtualHost_ThrowsException()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost1 = server.AddVirtualHost("vhost1");
        var vhost2 = server.AddVirtualHost("vhost2");
        var queue1 = vhost1.AddQueue("queue1");
        var queue2 = vhost2.AddQueue("queue2");

        var ex = Assert.Throws<DistributedApplicationException>(() => vhost2.AddShovel("myshovel", queue1, queue2));
        Assert.Contains("different virtual host", ex.Message);
    }

    [Fact]
    public void AddShovel_EnablesRequiredPlugins()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var queue1 = server.AddQueue("queue1");
        var queue2 = server.AddQueue("queue2");

        server.AddShovel("myshovel", queue1, queue2);

        var endpoints = server.Resource.Annotations.OfType<EndpointAnnotation>();
        Assert.Contains(endpoints, e => e.Name == RabbitMQServerResource.ManagementEndpointName);

        var pluginAnnotations = server.Resource.Annotations.OfType<RabbitMQPluginAnnotation>();
        Assert.Contains(pluginAnnotations, a => a.PluginName == "rabbitmq_shovel");
        Assert.Contains(pluginAnnotations, a => a.PluginName == "rabbitmq_shovel_management");
    }

    [Fact]
    public async Task ConnectionStringExpressions_AreCorrect()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("myqueue");
        var exchange = vhost.AddExchange("myexchange");

        var serverCs = await server.Resource.ConnectionStringExpression.GetValueAsync(default);
        var vhostCs = await vhost.Resource.ConnectionStringExpression.GetValueAsync(default);
        var queueCs = await queue.Resource.ConnectionStringExpression.GetValueAsync(default);
        var exchangeCs = await exchange.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.StartsWith("amqp://", serverCs);
        Assert.Equal($"{serverCs}/myvhost", vhostCs);
        Assert.Equal(vhostCs, queueCs);
        Assert.Equal(vhostCs, exchangeCs);
    }

    [Fact]
    public void AddQueue_DuplicateQueueName_ThrowsException()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        vhost.AddQueue("myqueue");

        var ex = Assert.Throws<DistributedApplicationException>(() => vhost.AddQueue("myqueue2", "myqueue"));
        Assert.Contains("myqueue", ex.Message);
    }

    [Fact]
    public void AddExchange_DuplicateExchangeName_ThrowsException()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        vhost.AddExchange("myexchange");

        var ex = Assert.Throws<DistributedApplicationException>(() => vhost.AddExchange("myexchange2", exchangeName: "myexchange"));
        Assert.Contains("myexchange", ex.Message);
    }

    [Fact]
    public void AddShovel_DuplicateShovelName_ThrowsException()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var queue1 = vhost.AddQueue("queue1");
        var queue2 = vhost.AddQueue("queue2");
        vhost.AddShovel("myshovel", queue1, queue2);

        var ex = Assert.Throws<DistributedApplicationException>(() => vhost.AddShovel("myshovel", queue1, queue2));
        Assert.Contains("myshovel", ex.Message);
    }
}
