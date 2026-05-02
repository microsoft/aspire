// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQPolicyTests
{
    // ── AddPolicy wiring ─────────────────────────────────────────────────────

    [Fact]
    public void AddPolicy_AddsToVhostPoliciesList()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        vhost.AddPolicy("ttl-policy", "^orders\\.");

        Assert.Single(vhost.Resource.Policies);
        Assert.Equal("ttl-policy", vhost.Resource.Policies[0].PolicyName);
        Assert.Equal("^orders\\.", vhost.Resource.Policies[0].Pattern);
    }

    [Fact]
    public void AddPolicy_WithCustomPolicyName_UsesWireName()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        vhost.AddPolicy("my-resource", "^orders\\.", policyName: "my-wire-policy");

        Assert.Equal("my-wire-policy", vhost.Resource.Policies[0].PolicyName);
        Assert.Equal("my-resource", vhost.Resource.Policies[0].Name);
    }

    [Fact]
    public void AddPolicy_DuplicatePolicyName_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        vhost.AddPolicy("ttl-policy", "^orders\\.");

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            vhost.AddPolicy("ttl-policy-2", "^orders\\.", policyName: "ttl-policy"));
        Assert.Contains("ttl-policy", ex.Message);
    }

    [Fact]
    public void AddPolicy_OnServer_AddsToDefaultVhost()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");

        server.AddPolicy("ttl-policy", "^orders\\.");

        var defaultVhost = server.Resource.VirtualHosts.Single(v => v.VirtualHostName == "/");
        Assert.Single(defaultVhost.Policies);
        Assert.Equal("ttl-policy", defaultVhost.Policies[0].PolicyName);
    }

    [Fact]
    public void AddPolicy_WithProperties_SetsDefinition()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        vhost.AddPolicy("ttl-policy", "^orders\\.")
             .WithProperties(p =>
             {
                 p.Definition["message-ttl"] = 60_000;
                 p.Definition["dead-letter-exchange"] = "dlx";
                 p.Priority = 10;
             });

        var policy = vhost.Resource.Policies[0];
        Assert.Equal(60_000, policy.Definition["message-ttl"]);
        Assert.Equal("dlx", policy.Definition["dead-letter-exchange"]);
        Assert.Equal(10, policy.Priority);
    }

    // ── BeforeStartEvent matching (via ResolveAndApplyPolicyMatches) ──────────
    //
    // The production code resolves matches in a BeforeStartEvent handler.
    // Tests call RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches directly
    // to avoid triggering the full BeforeStartEvent pipeline (which requires DCP).

    [Fact]
    public void AddPolicy_MatchesQueueAddedBeforePolicy()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var queue = vhost.AddQueue("orders-queue", "orders");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        // Simulate BeforeStartEvent resolution
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        Assert.Single(queue.Resource.AppliedPolicies);
        Assert.Equal("ttl-policy", queue.Resource.AppliedPolicies[0].PolicyName);
    }

    [Fact]
    public void AddPolicy_MatchesQueueAddedAfterPolicy()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        // Policy added BEFORE queue — lazy resolution means order doesn't matter
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");
        var queue = vhost.AddQueue("orders-queue", "orders");

        // Simulate BeforeStartEvent resolution (after all entities are registered)
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        Assert.Single(queue.Resource.AppliedPolicies);
        Assert.Equal("ttl-policy", queue.Resource.AppliedPolicies[0].PolicyName);
    }

    [Fact]
    public void AddPolicy_NonMatchingQueueUnaffected()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var matchingQueue = vhost.AddQueue("orders-queue", "orders");
        var nonMatchingQueue = vhost.AddQueue("payments-queue", "payments");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        Assert.Single(matchingQueue.Resource.AppliedPolicies);
        Assert.Empty(nonMatchingQueue.Resource.AppliedPolicies);
    }

    [Fact]
    public void AddPolicy_MatchesExchangeWhenApplyToExchanges()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var exchange = vhost.AddExchange("orders-exchange", exchangeName: "orders");
        var queue = vhost.AddQueue("orders-queue", "orders");
        var policyBuilder = vhost.AddPolicy("exchange-policy", "^orders", RabbitMQPolicyApplyTo.Exchanges);

        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        Assert.Single(exchange.Resource.AppliedPolicies);
        Assert.Empty(queue.Resource.AppliedPolicies); // Queues not matched when ApplyTo=Exchanges
    }

    [Fact]
    public void AddPolicy_MatchesBothWhenApplyToAll()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var exchange = vhost.AddExchange("orders-exchange", exchangeName: "orders");
        var queue = vhost.AddQueue("orders-queue", "orders");
        var policyBuilder = vhost.AddPolicy("all-policy", "^orders", RabbitMQPolicyApplyTo.All);

        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        Assert.Single(exchange.Resource.AppliedPolicies);
        Assert.Single(queue.Resource.AppliedPolicies);
    }

    [Fact]
    public void AddPolicy_MultiplePoliciesCanMatchSameQueue()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        var queue = vhost.AddQueue("orders-queue", "orders");
        var pb1 = vhost.AddPolicy("ttl-policy", "^orders");
        var pb2 = vhost.AddPolicy("dlx-policy", "^orders");

        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(pb1.Resource, vhost.Resource, pb1);
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(pb2.Resource, vhost.Resource, pb2);

        Assert.Equal(2, queue.Resource.AppliedPolicies.Count);
    }

    // ── Provisioner ordering ──────────────────────────────────────────────────

    [Fact]
    public async Task ProvisionTopologyAsync_PoliciesAppliedBeforeEntities()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("orders-queue", "orders");
        vhost.AddPolicy("ttl-policy", "^orders");

        var fakeClient = new FakeRabbitMQProvisioningClient();
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();
        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        var policyIndex = fakeClient.Calls.FindIndex(c => c.StartsWith("PutPolicyAsync(myvhost, ttl-policy,"));
        var queueIndex = fakeClient.Calls.FindIndex(c => c.StartsWith("DeclareQueueAsync(myvhost, orders,"));

        Assert.True(policyIndex >= 0, "PutPolicyAsync should have been called");
        Assert.True(queueIndex >= 0, "DeclareQueueAsync should have been called");
        Assert.True(policyIndex < queueIndex, "Policy must be applied before queue declaration");
    }

    [Fact]
    public async Task ProvisionTopologyAsync_PolicyFails_PolicyTcsFaulted_QueueTcsUnaffected()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("orders-queue", "orders");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        var fakeClient = new FakeRabbitMQProvisioningClient();
        fakeClient.FailPolicyNames.Add("ttl-policy");
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();
        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        Assert.True(policyBuilder.Resource.ProvisioningComplete.Task.IsFaulted, "Policy TCS should be faulted");
        Assert.True(queue.Resource.ProvisioningComplete.Task.IsCompletedSuccessfully, "Queue TCS should succeed independently");
    }

    [Fact]
    public async Task ProvisionTopologyAsync_VhostFails_PolicyTcsCascadeFaulted()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        var failingClient = new FailingFakeRabbitMQProvisioningClient();
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, failingClient);

        using var app = builder.Build();
        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        Assert.True(vhost.Resource.ProvisioningComplete.Task.IsFaulted);
        Assert.True(policyBuilder.Resource.ProvisioningComplete.Task.IsFaulted, "Policy TCS should cascade-fault when vhost fails");
    }

    // ── Health dependency ─────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_PolicyFails_MatchingQueueUnhealthy()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("orders-queue", "orders");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        // Simulate BeforeStartEvent resolution
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        var fakeClient = new FakeRabbitMQProvisioningClient();
        fakeClient.FailPolicyNames.Add("ttl-policy");
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();
        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        // Queue itself provisioned OK, but its policy failed
        Assert.True(queue.Resource.ProvisioningComplete.Task.IsCompletedSuccessfully);
        Assert.True(policyBuilder.Resource.ProvisioningComplete.Task.IsFaulted);

        // The queue's HealthDependencies should include the policy
        Assert.Single(queue.Resource.AppliedPolicies);

        // Simulate the health check: queue's own TCS is OK, but dependency (policy) is faulted
        var check = new RabbitMQProvisionableHealthCheck(queue.Resource, fakeClient);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };
        var result = await check.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("ttl-policy", result.Description);
    }

    [Fact]
    public async Task HealthCheck_PolicyFails_NonMatchingQueueHealthy()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit")
            .WithEndpoint(RabbitMQServerResource.PrimaryEndpointName, e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5672));
        var vhost = server.AddVirtualHost("myvhost");
        var matchingQueue = vhost.AddQueue("orders-queue", "orders");
        var nonMatchingQueue = vhost.AddQueue("payments-queue", "payments");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        // Simulate BeforeStartEvent resolution
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        // Non-matching queue has no policy dependency
        Assert.Empty(nonMatchingQueue.Resource.AppliedPolicies);

        var fakeClient = new FakeRabbitMQProvisioningClient();
        fakeClient.FailPolicyNames.Add("ttl-policy");
        builder.Services.AddKeyedSingleton<IRabbitMQProvisioningClient>(server.Resource.Name, fakeClient);

        using var app = builder.Build();
        await RabbitMQTopologyProvisioner.ProvisionTopologyAsync(server.Resource, app.Services, default);

        var check = new RabbitMQProvisionableHealthCheck(nonMatchingQueue.Resource, fakeClient);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };
        var result = await check.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ── RabbitMQPolicyResource.AppliesTo ─────────────────────────────────────

    [Fact]
    public void AppliesTo_MatchingQueueName_ReturnsTrue()
    {
        var server = new RabbitMQServerResource("rabbit", userName: null,
            password: new ParameterResource("pw", _ => "pw", secret: true));
        var vhost = new RabbitMQVirtualHostResource("myvhost", "myvhost", server);
        var policy = new RabbitMQPolicyResource("p", "p", "^orders", vhost);

        Assert.True(policy.AppliesTo("orders", RabbitMQDestinationKind.Queue));
        Assert.True(policy.AppliesTo("orders-dlq", RabbitMQDestinationKind.Queue));
    }

    [Fact]
    public void AppliesTo_NonMatchingQueueName_ReturnsFalse()
    {
        var server = new RabbitMQServerResource("rabbit", userName: null,
            password: new ParameterResource("pw", _ => "pw", secret: true));
        var vhost = new RabbitMQVirtualHostResource("myvhost", "myvhost", server);
        var policy = new RabbitMQPolicyResource("p", "p", "^orders", vhost);

        Assert.False(policy.AppliesTo("payments", RabbitMQDestinationKind.Queue));
    }

    [Fact]
    public void AppliesTo_ExchangeWhenApplyToQueues_ReturnsFalse()
    {
        var server = new RabbitMQServerResource("rabbit", userName: null,
            password: new ParameterResource("pw", _ => "pw", secret: true));
        var vhost = new RabbitMQVirtualHostResource("myvhost", "myvhost", server);
        var policy = new RabbitMQPolicyResource("p", "p", "^orders", vhost) { ApplyTo = RabbitMQPolicyApplyTo.Queues };

        Assert.False(policy.AppliesTo("orders", RabbitMQDestinationKind.Exchange));
    }

    [Fact]
    public void AppliesTo_QueueWhenApplyToExchanges_ReturnsFalse()
    {
        var server = new RabbitMQServerResource("rabbit", userName: null,
            password: new ParameterResource("pw", _ => "pw", secret: true));
        var vhost = new RabbitMQVirtualHostResource("myvhost", "myvhost", server);
        var policy = new RabbitMQPolicyResource("p", "p", "^orders", vhost) { ApplyTo = RabbitMQPolicyApplyTo.Exchanges };

        Assert.False(policy.AppliesTo("orders", RabbitMQDestinationKind.Queue));
    }
}
