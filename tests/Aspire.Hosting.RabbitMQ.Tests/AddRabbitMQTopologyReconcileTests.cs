// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;

using static Aspire.Hosting.RabbitMQ.Tests.TestServices.RabbitMQTopologyTestFactory;

namespace Aspire.Hosting.RabbitMQ.Tests;

/// <summary>
/// Unit tests for each topology child's <c>ReconcileAsync</c> (create) and <c>DeleteAsync</c> (delete)
/// primitives. Resources are constructed directly (no live broker) and driven against the shared
/// in-memory <see cref="FakeRabbitMQProvisioningClient"/>, asserting that the correct client method was
/// invoked with the expected arguments via the recorded <see cref="FakeRabbitMQProvisioningClient.Calls"/>.
/// </summary>
public class AddRabbitMQTopologyReconcileTests
{

    // ── Virtual host ──────────────────────────────────────────────────────────

    [Fact]
    public async Task VirtualHostReconcileAsync_CallsCreateVirtualHost()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();

        await vhost.ReconcileAsync(client, default);

        Assert.Contains("CreateVirtualHostAsync(myvhost)", client.Calls);
    }

    [Fact]
    public async Task VirtualHostDeleteAsync_CallsDeleteVirtualHost()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();

        await vhost.DeleteAsync(client, default);

        Assert.Contains("DeleteVirtualHostAsync(myvhost)", client.Calls);
    }

    [Fact]
    public async Task DefaultVirtualHostReconcileAndDeleteAsync_RecordNoCalls()
    {
        var (server, _) = BuildVhost();
        var vhost = new RabbitMQVirtualHostResource("default", "/", server);
        var client = new FakeRabbitMQProvisioningClient();

        // The default "/" vhost always exists on a fresh broker and is broker-owned, so ReconcileAsync
        // creates nothing and DeleteAsync deletes nothing. Asserting the whole Calls list is empty is a
        // stronger check than asserting the absence of a single call (and avoids Assert.DoesNotContain).
        await vhost.ReconcileAsync(client, default);
        await vhost.DeleteAsync(client, default);

        Assert.Empty(client.Calls);
    }

    // ── Queue ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueueReconcileAsync_CallsDeclareQueueWithDeclaredFields()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("myqueue", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        await queue.ReconcileAsync(client, default);

        // Durable defaults to true; Exclusive and AutoDelete default to false.
        Assert.Contains("DeclareQueueAsync(myvhost, myqueue, True, False, False)", client.Calls);
    }

    [Fact]
    public async Task QueueDeleteAsync_CallsDeleteQueue()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("myqueue", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        await queue.DeleteAsync(client, default);

        Assert.Contains("DeleteQueueAsync(myvhost, myqueue)", client.Calls);
    }

    [Fact]
    public async Task QueueReconcileAsync_QuorumType_PassesXQueueTypeArgument()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("myqueue", "myqueue", vhost, RabbitMQQueueType.Quorum);
        var client = new FakeRabbitMQProvisioningClient();

        await queue.ReconcileAsync(client, default);

        // The Calls entry does not include the arg bag, so assert the "x-queue-type" argument round-tripped
        // by reading the queue back: the fake stores the declared type inside its recorded definition.
        var live = await client.GetQueueAsync("myvhost", "myqueue", default);
        Assert.NotNull(live);
        Assert.Equal("quorum", live!.Type);
        Assert.NotNull(live.Arguments);
        Assert.True(live.Arguments!.TryGetValue("x-queue-type", out var queueTypeArg));
        Assert.Equal("quorum", queueTypeArg);
    }

    // ── Exchange ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeReconcileAsync_CallsDeclareExchangeWithDeclaredFields()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("myexchange", "myexchange", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        await exchange.ReconcileAsync(client, default);

        // Exchange type defaults to Direct -> "direct"; Durable defaults to true; AutoDelete defaults to false.
        Assert.Contains("DeclareExchangeAsync(myvhost, myexchange, direct, True, False)", client.Calls);
    }

    [Fact]
    public async Task ExchangeDeleteAsync_CallsDeleteExchange()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("myexchange", "myexchange", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        await exchange.DeleteAsync(client, default);

        Assert.Contains("DeleteExchangeAsync(myvhost, myexchange)", client.Calls);
    }

    [Fact]
    public async Task ExchangeReconcileAsync_FanoutType_PassesLoweredType()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("myexchange", "myexchange", vhost, RabbitMQExchangeType.Fanout);
        var client = new FakeRabbitMQProvisioningClient();

        await exchange.ReconcileAsync(client, default);

        Assert.Contains("DeclareExchangeAsync(myvhost, myexchange, fanout, True, False)", client.Calls);
    }

    // ── Policy ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PolicyReconcileAsync_CallsPutPolicy()
    {
        var (_, vhost) = BuildVhost();
        var policy = new RabbitMQPolicyResource("mypolicy", "mypolicy", "^orders", vhost, RabbitMQPolicyApplyTo.Queues, priority: 5);
        var client = new FakeRabbitMQProvisioningClient();

        await policy.ReconcileAsync(client, default);

        // The Calls entry records "PutPolicyAsync(vhost, name, pattern, applyTo)".
        Assert.Contains("PutPolicyAsync(myvhost, mypolicy, ^orders, queues)", client.Calls);
    }

    [Fact]
    public async Task PolicyDeleteAsync_CallsDeletePolicy()
    {
        var (_, vhost) = BuildVhost();
        var policy = new RabbitMQPolicyResource("mypolicy", "mypolicy", "^orders", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        await policy.DeleteAsync(client, default);

        Assert.Contains("DeletePolicyAsync(myvhost, mypolicy)", client.Calls);
    }

    // ── Shovel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShovelReconcileAsync_CallsPutShovel()
    {
        var (_, vhost) = BuildVhost();
        var source = new RabbitMQQueueResource("srcq", "srcq", vhost);
        var destination = new RabbitMQQueueResource("destq", "destq", vhost);
        var shovel = new RabbitMQShovelResource("myshovel", "myshovel", vhost, source, destination);
        var client = new FakeRabbitMQProvisioningClient();

        // The shovel's ContainerUriExpression resolves in a unit context because it is built from the
        // password parameter plus the literal "localhost:5672" (no endpoint references), so Reconcile is
        // exercised end-to-end here.
        await shovel.ReconcileAsync(client, default);

        Assert.Contains(client.Calls, c => c.StartsWith("PutShovelAsync(myvhost, myshovel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShovelDeleteAsync_CallsDeleteShovel()
    {
        var (_, vhost) = BuildVhost();
        var source = new RabbitMQQueueResource("srcq", "srcq", vhost);
        var destination = new RabbitMQQueueResource("destq", "destq", vhost);
        var shovel = new RabbitMQShovelResource("myshovel", "myshovel", vhost, source, destination);
        var client = new FakeRabbitMQProvisioningClient();

        // DeleteAsync needs no container URI, so it is always safe in a unit context.
        await shovel.DeleteAsync(client, default);

        Assert.Contains("DeleteShovelAsync(myvhost, myshovel)", client.Calls);
    }
}
