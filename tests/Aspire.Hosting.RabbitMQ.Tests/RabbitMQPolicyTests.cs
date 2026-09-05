// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using static Aspire.Hosting.RabbitMQ.Tests.TestServices.RabbitMQTopologyTestFactory;

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
    public void AddPolicy_WithProperties_SetsArguments()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");

        vhost.AddPolicy("ttl-policy", "^orders\\.", priority: 10)
             .WithQueueArguments(a =>
             {
                 a.MessageTtl = TimeSpan.FromMilliseconds(60_000);
                 a.AdditionalArguments["ha-mode"] = "all";
             });

        var policy = vhost.Resource.Policies[0];
        Assert.Equal(TimeSpan.FromMilliseconds(60_000), policy.QueueArguments.MessageTtl);
        Assert.Equal("all", policy.QueueArguments.AdditionalArguments["ha-mode"]);
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

    // ── Health dependency ─────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_PolicyNotHealthy_MatchingQueueUnhealthy()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("orders-queue", "orders");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        // Simulate BeforeStartEvent resolution
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        // The queue's HealthDependencies should include the policy
        Assert.Single(queue.Resource.AppliedPolicies);

        // Simulate: queue is Running+Healthy, policy is Running but its own health check is Unhealthy.
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(queue.Resource, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("queue_check", HealthStatus.Healthy, null, null)]));
        // Policy is Running but health check reports Unhealthy — HealthStatus will not be Healthy.
        await notifications.PublishUpdateAsync(policyBuilder.Resource, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("policy_check", HealthStatus.Unhealthy, "probe failed", null)]));
        // Server must be Running so the health check's Stage 1 broker-liveness gate passes.
        await notifications.PublishUpdateAsync(server.Resource, s => s with { State = KnownResourceStates.Running });

        var fakeClient = new FakeRabbitMQProvisioningClient();
        var check = new RabbitMQProvisionableHealthCheck(queue.Resource, server.Resource.Name, fakeClient, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", _ => null!, null, null)
        };
        var result = await check.CheckHealthAsync(context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("ttl-policy", result.Description);
        Assert.Contains("not yet healthy", result.Description);
    }

    [Fact]
    public async Task HealthCheck_PolicyNotHealthy_NonMatchingQueueHealthy()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var nonMatchingQueue = vhost.AddQueue("payments-queue", "payments");
        var policyBuilder = vhost.AddPolicy("ttl-policy", "^orders");

        // Simulate BeforeStartEvent resolution
        RabbitMQBuilderExtensions.ResolveAndApplyPolicyMatches(policyBuilder.Resource, vhost.Resource, policyBuilder);

        // Non-matching queue has no policy dependency
        Assert.Empty(nonMatchingQueue.Resource.AppliedPolicies);

        // Simulate: non-matching queue is Running+Healthy, policy is unhealthy (irrelevant to this queue).
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(nonMatchingQueue.Resource, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("queue_check", HealthStatus.Healthy, null, null)]));
        await notifications.PublishUpdateAsync(policyBuilder.Resource, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("policy_check", HealthStatus.Unhealthy, "probe failed", null)]));
        // Server must be Running so the health check's Stage 1 broker-liveness gate passes.
        await notifications.PublishUpdateAsync(server.Resource, s => s with { State = KnownResourceStates.Running });

        var fakeClient = new FakeRabbitMQProvisioningClient();
        // ProbeAsync now compares declared fields against a live read-back, so the queue must actually be
        // reconciled into the client first for the probe stage to report Healthy.
        await nonMatchingQueue.Resource.ReconcileAsync(fakeClient, default);
        var check = new RabbitMQProvisionableHealthCheck(nonMatchingQueue.Resource, server.Resource.Name, fakeClient, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);
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
        var policy = MakePolicy("^orders");

        Assert.True(policy.AppliesTo("orders", RabbitMQDestinationKind.Queue));
        Assert.True(policy.AppliesTo("orders-dlq", RabbitMQDestinationKind.Queue));
    }

    [Fact]
    public void AppliesTo_NonMatchingQueueName_ReturnsFalse()
    {
        var policy = MakePolicy("^orders");

        Assert.False(policy.AppliesTo("payments", RabbitMQDestinationKind.Queue));
    }

    [Fact]
    public void AppliesTo_ExchangeWhenApplyToQueues_ReturnsFalse()
    {
        var policy = MakePolicy("^orders", RabbitMQPolicyApplyTo.Queues);

        Assert.False(policy.AppliesTo("orders", RabbitMQDestinationKind.Exchange));
    }

    [Fact]
    public void AppliesTo_QueueWhenApplyToExchanges_ReturnsFalse()
    {
        var policy = MakePolicy("^orders", RabbitMQPolicyApplyTo.Exchanges);

        Assert.False(policy.AppliesTo("orders", RabbitMQDestinationKind.Queue));
    }

    private static RabbitMQPolicyResource MakePolicy(string pattern, RabbitMQPolicyApplyTo applyTo = RabbitMQPolicyApplyTo.All)
    {
        var (_, vhost) = BuildVhost();
        return new RabbitMQPolicyResource("p", "p", pattern, vhost, applyTo);
    }
}
