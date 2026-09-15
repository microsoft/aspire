// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Components.Common.TestUtilities;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using RabbitMQ.Client;
using Xunit.Sdk;

namespace Aspire.Hosting.RabbitMQ.Tests;

/// <summary>
/// Fixture that starts a single plain <c>rabbitmq</c> container and provisions the full
/// default-vhost topology (queue, exchange→queue binding, exchange→exchange binding) once.
/// All three tests in <see cref="RabbitMQPlainImageTopologyTests"/> share this container.
/// </summary>
public sealed class RabbitMQPlainImageFixture(IMessageSink diagnosticMessageSink) : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? _builder;
    private DistributedApplication? _app;
    private IConnection? _connection;

    // Resource builders exposed so test methods can call WaitForHealthyAsync if needed.
    public IResourceBuilder<RabbitMQQueueResource>? HelloQueue { get; private set; }
    public IResourceBuilder<RabbitMQExchangeResource>? FanoutExchange { get; private set; }
    public IResourceBuilder<RabbitMQExchangeResource>? UpstreamExchange { get; private set; }
    public IResourceBuilder<RabbitMQExchangeResource>? DownstreamExchange { get; private set; }

    /// <summary>Gets the shared AMQP connection. Each test should open its own channel from this connection.</summary>
    public IConnection Connection => _connection ?? throw new InvalidOperationException("Fixture not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (!RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            return;
        }

        var output = new TestOutputWrapper(diagnosticMessageSink);

        _builder = TestDistributedApplicationBuilder.Create(
            o => o.ContainerRegistryOverride = ComponentTestConstants.AspireTestContainerRegistry,
            output);

        var server = _builder.AddRabbitMQ("rabbitMQ");

        // Queue test: a plain queue on the default vhost.
        HelloQueue = server.AddQueue("hello");

        // Exchange→Queue binding test: fanout exchange bound to a queue.
        var boundQueue = server.AddQueue("bound-q");
        FanoutExchange = server.AddExchange("fanout-ex", type: RabbitMQExchangeType.Fanout);
        FanoutExchange.WithBinding(boundQueue, routingKey: "");

        // Exchange→Exchange binding test: upstream-ex → downstream-ex → final-q.
        var finalQueue = server.AddQueue("final-q");
        DownstreamExchange = server.AddExchange("downstream-ex", type: RabbitMQExchangeType.Fanout);
        UpstreamExchange = server.AddExchange("upstream-ex", type: RabbitMQExchangeType.Fanout);
        DownstreamExchange.WithBinding(finalQueue, routingKey: "");
        UpstreamExchange.WithBinding(DownstreamExchange, routingKey: "");

        _app = _builder.Build();
        await _app.StartAsync();

        // Wait for the server and all topology resources to be healthy before any test runs.
        // Use ExtraLongTimeoutTimeSpan to accommodate container pull + startup time.
        await _app.WaitForHealthyAsync(server).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(HelloQueue).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(FanoutExchange).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(UpstreamExchange).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(DownstreamExchange).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        var connectionString = await server.Resource.ConnectionStringExpression.GetValueAsync(default);
        var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
        _connection = await factory.CreateConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _builder?.Dispose();
    }

    /// <summary>
    /// Wraps <see cref="IMessageSink"/> as <see cref="ITestOutputHelper"/> so it can be passed to
    /// <see cref="TestDistributedApplicationBuilder"/> logging helpers.
    /// </summary>
    private sealed class TestOutputWrapper(IMessageSink messageSink) : ITestOutputHelper
    {
        public string Output => string.Empty;

        public void Write(string message) =>
            messageSink.OnMessage(new DiagnosticMessage(message));

        public void Write(string format, params object[] args) =>
            messageSink.OnMessage(new DiagnosticMessage(string.Format(CultureInfo.CurrentCulture, format, args)));

        public void WriteLine(string message) =>
            messageSink.OnMessage(new DiagnosticMessage(message));

        public void WriteLine(string format, params object[] args) =>
            messageSink.OnMessage(new DiagnosticMessage(string.Format(CultureInfo.CurrentCulture, format, args)));
    }
}

/// <summary>
/// Fixture that starts a single <c>rabbitmq-management</c> container and provisions the full
/// management-image topology (vhost, policy, shovel) once.
/// All three tests in <see cref="RabbitMQManagementImageTopologyTests"/> share this container.
/// </summary>
public sealed class RabbitMQManagementImageFixture(IMessageSink diagnosticMessageSink) : IAsyncLifetime
{
    private IDistributedApplicationTestingBuilder? _builder;
    private DistributedApplication? _app;
    private IConnection? _defaultVhostConnection;
    private IConnection? _ordersVhostConnection;

    // Resource builders exposed so test methods can call WaitForHealthyAsync if needed.
    public IResourceBuilder<RabbitMQVirtualHostResource>? OrdersVhost { get; private set; }
    public IResourceBuilder<RabbitMQQueueResource>? OrdersQueue { get; private set; }
    public IResourceBuilder<RabbitMQQueueResource>? TtlQueue { get; private set; }
    public IResourceBuilder<RabbitMQQueueResource>? SrcQueue { get; private set; }
    public IResourceBuilder<RabbitMQQueueResource>? DestQueue { get; private set; }
    public IResourceBuilder<RabbitMQShovelResource>? Shovel { get; private set; }

    /// <summary>Gets a shared AMQP connection to the default <c>/</c> vhost.</summary>
    public IConnection DefaultVhostConnection => _defaultVhostConnection ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>Gets a shared AMQP connection to the <c>orders</c> vhost.</summary>
    public IConnection OrdersVhostConnection => _ordersVhostConnection ?? throw new InvalidOperationException("Fixture not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (!RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            return;
        }

        var output = new TestOutputWrapper(diagnosticMessageSink);

        _builder = TestDistributedApplicationBuilder.Create(
            o => o.ContainerRegistryOverride = ComponentTestConstants.AspireTestContainerRegistry,
            output);

        var server = _builder.AddRabbitMQ("rabbitMQ");

        // VirtualHost test: non-default vhost with a queue.
        OrdersVhost = server.AddVirtualHost("orders");
        OrdersQueue = OrdersVhost.AddQueue("orders-q");

        // Policy test: TTL policy on the default vhost.
        TtlQueue = server.AddQueue("ttl-q");
        server.AddPolicy("ttl-policy", "^ttl-q", RabbitMQPolicyApplyTo.Queues)
              .WithQueueArguments(a => a.MessageTtl = TimeSpan.FromSeconds(1));

        // Shovel test: shovel from src-q to dest-q on the default vhost.
        SrcQueue = server.AddQueue("src-q");
        DestQueue = server.AddQueue("dest-q");
        Shovel = server.AddShovel("my-shovel", SrcQueue, DestQueue);

        _app = _builder.Build();
        await _app.StartAsync();

        // The management image takes longer to start — use ExtraLongTimeoutTimeSpan.
        await _app.WaitForHealthyAsync(server).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(OrdersQueue).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(TtlQueue).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(SrcQueue).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(DestQueue).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);
        await _app.WaitForHealthyAsync(Shovel).WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        var defaultCs = await server.Resource.ConnectionStringExpression.GetValueAsync(default);
        var ordersCs = await OrdersVhost.Resource.ConnectionStringExpression.GetValueAsync(default);

        var factory = new ConnectionFactory { Uri = new Uri(defaultCs!) };
        _defaultVhostConnection = await factory.CreateConnectionAsync();

        factory = new ConnectionFactory { Uri = new Uri(ordersCs!) };
        _ordersVhostConnection = await factory.CreateConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_defaultVhostConnection is not null)
        {
            await _defaultVhostConnection.DisposeAsync();
        }

        if (_ordersVhostConnection is not null)
        {
            await _ordersVhostConnection.DisposeAsync();
        }

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _builder?.Dispose();
    }

    private sealed class TestOutputWrapper(IMessageSink messageSink) : ITestOutputHelper
    {
        public string Output => string.Empty;

        public void Write(string message) =>
            messageSink.OnMessage(new DiagnosticMessage(message));

        public void Write(string format, params object[] args) =>
            messageSink.OnMessage(new DiagnosticMessage(string.Format(CultureInfo.CurrentCulture, format, args)));

        public void WriteLine(string message) =>
            messageSink.OnMessage(new DiagnosticMessage(message));

        public void WriteLine(string format, params object[] args) =>
            messageSink.OnMessage(new DiagnosticMessage(string.Format(CultureInfo.CurrentCulture, format, args)));
    }
}

/// <summary>
/// Functional tests for RabbitMQ topology APIs against the plain <c>rabbitmq</c> image.
/// All three tests share a single Docker container started by <see cref="RabbitMQPlainImageFixture"/>.
/// </summary>
public class RabbitMQPlainImageTopologyTests(RabbitMQPlainImageFixture fixture) : IClassFixture<RabbitMQPlainImageFixture>
{
    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyQueueProvisioningOnDefaultVhost()
    {
        // The queue was declared by Aspire during fixture setup. Publish to the default exchange
        // with the queue name as routing key and verify the message is delivered.
        await using var channel = await fixture.Connection.CreateChannelAsync();

        const string message = "Hello from topology test!";
        var body = Encoding.UTF8.GetBytes(message);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "hello", body: body);

        var result = await channel.BasicGetAsync("hello", autoAck: true);
        Assert.NotNull(result);
        Assert.Equal(message, Encoding.UTF8.GetString(result.Body.Span));
    }

    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyExchangeToQueueBinding()
    {
        // Publishing to the fanout exchange routes to all bound queues regardless of routing key.
        await using var channel = await fixture.Connection.CreateChannelAsync();

        const string message = "Fanout message";
        var body = Encoding.UTF8.GetBytes(message);
        await channel.BasicPublishAsync(exchange: "fanout-ex", routingKey: string.Empty, body: body);

        var result = await channel.BasicGetAsync("bound-q", autoAck: true);
        Assert.NotNull(result);
        Assert.Equal(message, Encoding.UTF8.GetString(result.Body.Span));
    }

    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyExchangeToExchangeBinding()
    {
        // Publishing to upstream-ex flows through downstream-ex and lands in final-q,
        // proving the exchange→exchange binding chain works end-to-end.
        await using var channel = await fixture.Connection.CreateChannelAsync();

        const string message = "E2E chain message";
        var body = Encoding.UTF8.GetBytes(message);
        await channel.BasicPublishAsync(exchange: "upstream-ex", routingKey: string.Empty, body: body);

        var result = await channel.BasicGetAsync("final-q", autoAck: true);
        Assert.NotNull(result);
        Assert.Equal(message, Encoding.UTF8.GetString(result.Body.Span));
    }
}

/// <summary>
/// Functional tests for RabbitMQ management-image topology APIs (virtual hosts, policies, shovels).
/// All three tests share a single Docker container started by <see cref="RabbitMQManagementImageFixture"/>.
/// </summary>
public class RabbitMQManagementImageTopologyTests(RabbitMQManagementImageFixture fixture) : IClassFixture<RabbitMQManagementImageFixture>
{
    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyVirtualHostAndQueueProvisioning()
    {
        // Connect to the "orders" vhost. QueueDeclarePassiveAsync succeeds only when the queue
        // already exists on the broker — proving the vhost and queue were provisioned.
        await using var channel = await fixture.OrdersVhostConnection.CreateChannelAsync();
        await channel.QueueDeclarePassiveAsync("orders-q");

        const string message = "Order message";
        var body = Encoding.UTF8.GetBytes(message);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "orders-q", body: body);

        var result = await channel.BasicGetAsync("orders-q", autoAck: true);
        Assert.NotNull(result);
        Assert.Equal(message, Encoding.UTF8.GetString(result.Body.Span));
    }

    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyPolicyIsAppliedToBroker()
    {
        // The ttl-policy sets a 1-second message TTL on ttl-q. Publish a message, wait for it
        // to expire, then verify BasicGetAsync returns null — proving the policy is enforced.
        await using var channel = await fixture.DefaultVhostConnection.CreateChannelAsync();

        const string message = "TTL message";
        var body = Encoding.UTF8.GetBytes(message);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "ttl-q", body: body);

        // Wait longer than the 1-second TTL so the broker can expire the message.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var result = await channel.BasicGetAsync("ttl-q", autoAck: true);
        Assert.Null(result);
    }

    [Fact]
    [OuterloopTest]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyShovelMovesMessages()
    {
        // Publish to src-q and poll dest-q — the shovel moves the message asynchronously.
        await using var channel = await fixture.DefaultVhostConnection.CreateChannelAsync();

        const string message = "Shovel message";
        var body = Encoding.UTF8.GetBytes(message);
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: "src-q", body: body);

        BasicGetResult? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (result is null && DateTime.UtcNow < deadline)
        {
            result = await channel.BasicGetAsync("dest-q", autoAck: true);
            if (result is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        Assert.NotNull(result);
        Assert.Equal(message, Encoding.UTF8.GetString(result.Body.Span));
    }
}
