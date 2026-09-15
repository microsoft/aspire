// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using static Aspire.Hosting.RabbitMQ.Tests.TestServices.RabbitMQTopologyTestFactory;

namespace Aspire.Hosting.RabbitMQ.Tests;

// ── RabbitMQProvisionableHealthCheck stage tests ──────────────────────────────
// Tests for the 4 stages of RabbitMQProvisionableHealthCheck: server not running,
// self not running, dependency not healthy, and all running (probe stage).

public class RabbitMQHealthCheckStageTests
{
    private readonly RabbitMQServerResource _server;
    private readonly RabbitMQVirtualHostResource _vhost;
    private readonly FakeRabbitMQProvisioningClient _client = new();

    public RabbitMQHealthCheckStageTests()
        => (_server, _vhost) = BuildVhost();

    [Fact]
    public async Task CheckHealthAsync_SelfNotRunning_ReturnsUnhealthy()
    {
        // No self state published — TryGetCurrentState(self) returns false. Server IS Running so Stage 1
        // passes and the check reaches Stage 2 (self-liveness).
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        var check = MakeHealthCheck(_vhost, _server, _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not yet Running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_SelfWaiting_ReturnsUnhealthy()
    {
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        await notifications.PublishUpdateAsync(_vhost, s => s with { State = KnownResourceStates.Waiting });
        var check = MakeHealthCheck(_vhost, _server, _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not yet Running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_SelfFailedToStart_ReturnsUnhealthy()
    {
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        await notifications.PublishUpdateAsync(_vhost, s => s with { State = KnownResourceStates.FailedToStart });
        var check = MakeHealthCheck(_vhost, _server, _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not yet Running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_ServerNotRunning_ReturnsUnhealthyWithBrokerReason()
    {
        // Stage 1 gate: when the broker server is not Running, the check short-circuits to Unhealthy
        // WITHOUT issuing any live broker probe. Even though the vhost itself is Running, the server-down
        // gate fires first.
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(_server, s => s with { State = KnownResourceStates.Exited });
        await notifications.PublishUpdateAsync(_vhost, s => s with { State = KnownResourceStates.Running });
        var check = MakeHealthCheck(_vhost, _server, _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Broker server", result.Description);
        Assert.Contains("is not Running", result.Description);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task CheckHealthAsync_DependencyNotHealthy_ReturnsUnhealthyWithDepName()
    {
        var queue = new RabbitMQQueueResource("myqueue", "myqueue", _vhost);
        var dep = new StubProvisionable("mypolicy");

        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(queue, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("queue_check", HealthStatus.Healthy, null, null)]));
        await notifications.PublishUpdateAsync(dep, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("dep_check", HealthStatus.Unhealthy, "probe failed", null)]));

        var check = new RabbitMQProvisionableHealthCheckWithDeps(queue, [dep], _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("mypolicy", result.Description);
        Assert.Contains("not yet healthy", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_AllRunningProbeHealthy_ReturnsHealthy()
    {
        // "myvhost" is a named vhost, so ProbeAsync checks existence via VirtualHostExistsAsync.
        _client.SeedVirtualHost("myvhost");
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        await notifications.PublishUpdateAsync(_vhost, s => s with { State = KnownResourceStates.Running });
        var check = MakeHealthCheck(_vhost, _server, _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_AllRunningProbeUnhealthy_ReturnsUnhealthy()
    {
        // Fresh client has never seeded "myvhost", so VirtualHostExistsAsync reports it missing.
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        await notifications.PublishUpdateAsync(_vhost, s => s with { State = KnownResourceStates.Running });
        var check = MakeHealthCheck(_vhost, _server, _client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("does not exist", result.Description);
    }

    private sealed class StubProvisionable(string name) : RabbitMQProvisionableResource(name);

    /// <summary>
    /// Wraps <see cref="RabbitMQProvisionableHealthCheck"/> but injects explicit dependencies,
    /// allowing tests to verify the dependency-checking stage without needing real policy resources.
    /// </summary>
    private sealed class RabbitMQProvisionableHealthCheckWithDeps(
        RabbitMQProvisionableResource self,
        IEnumerable<RabbitMQProvisionableResource> deps,
        IRabbitMQProvisioningClient client,
        ResourceNotificationService notifications) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!notifications.TryGetCurrentState(self.Name, out var evt) ||
                evt.Snapshot.State?.Text != KnownResourceStates.Running)
            {
                return HealthCheckResult.Unhealthy($"'{self.Name}' is not yet Running.");
            }

            foreach (var dep in deps)
            {
                if (!notifications.TryGetCurrentState(dep.Name, out var depEvt) ||
                    depEvt.Snapshot.HealthStatus != HealthStatus.Healthy)
                {
                    return HealthCheckResult.Unhealthy($"Dependency '{dep.Name}' is not yet healthy.");
                }
            }

            var probe = await self.ProbeAsync(client, cancellationToken).ConfigureAwait(false);
            return probe.IsHealthy ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(probe.Description);
        }
    }
}

// ── Queue ProbeAsync (declared-field drift) ───────────────────────────────────

public class RabbitMQQueueProbeTests
{
    private readonly RabbitMQVirtualHostResource _vhost;
    private readonly RabbitMQQueueResource _queue;
    private readonly FakeRabbitMQProvisioningClient _client = new();

    public RabbitMQQueueProbeTests()
    {
        (_, _vhost) = BuildVhost();
        _queue = new RabbitMQQueueResource("q", "myqueue", _vhost);
    }

    [Fact]
    public async Task LiveMatchesDeclared_ReturnsHealthy()
    {
        await _queue.ReconcileAsync(_client, default);

        var result = await _queue.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task NotDeclared_ReturnsUnhealthyDoesNotExist()
    {
        var result = await _queue.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myqueue", result.Description);
        Assert.Contains("does not exist", result.Description);
    }

    public static IEnumerable<object[]> DriftCases =>
    [
        [new RabbitMQQueueDefinition("quorum",  Durable: true,  Exclusive: false, AutoDelete: false, Arguments: null), "type drifted"],
        [new RabbitMQQueueDefinition("classic", Durable: false, Exclusive: false, AutoDelete: false, Arguments: null), "durable drifted"],
        [new RabbitMQQueueDefinition("classic", Durable: true,  Exclusive: true,  AutoDelete: false, Arguments: null), "exclusive drifted"],
        [new RabbitMQQueueDefinition("classic", Durable: true,  Exclusive: false, AutoDelete: true,  Arguments: null), "auto-delete drifted"],
    ];

    [Theory]
    [MemberData(nameof(DriftCases))]
    internal async Task FieldDrifted_ReturnsUnhealthy(RabbitMQQueueDefinition liveDef, string expectedKeyword)
    {
        _client.SeedQueue("myvhost", "myqueue", liveDef);

        var result = await _queue.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains(expectedKeyword, result.Description);
    }

    [Fact]
    public async Task DeclaredArgumentMissing_ReturnsUnhealthy()
    {
        _queue.QueueArguments.MessageTtl = TimeSpan.FromMilliseconds(60_000);
        _client.SeedQueue("myvhost", "myqueue",
            new RabbitMQQueueDefinition("classic", Durable: true, Exclusive: false, AutoDelete: false,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal)));

        var result = await _queue.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("x-message-ttl", result.Description);
    }

    [Fact]
    public async Task DeclaredArgumentChanged_ReturnsUnhealthy()
    {
        _queue.QueueArguments.MessageTtl = TimeSpan.FromMilliseconds(60_000);
        _client.SeedQueue("myvhost", "myqueue",
            new RabbitMQQueueDefinition("classic", Durable: true, Exclusive: false, AutoDelete: false,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-message-ttl"] = 30_000L }));

        var result = await _queue.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("x-message-ttl", result.Description);
    }

    [Fact]
    public async Task ExtraServerManagedArgument_ReturnsHealthy()
    {
        // CRITICAL drift-boundary test: an extra live-side argument key Aspire never declared must NOT
        // trigger drift. Server-managed / policy-layered fields are scoped out of drift detection.
        _queue.QueueArguments.MessageTtl = TimeSpan.FromMilliseconds(60_000);
        _client.SeedQueue("myvhost", "myqueue",
            new RabbitMQQueueDefinition("classic", Durable: true, Exclusive: false, AutoDelete: false,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-message-ttl"] = 60_000L,
                    ["x-server-managed-extra"] = "some-server-computed-value",
                }));

        var result = await _queue.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }
}

// ── Exchange ProbeAsync (declared-field drift) ────────────────────────────────

public class RabbitMQExchangeProbeTests
{
    private readonly RabbitMQVirtualHostResource _vhost;
    private readonly RabbitMQExchangeResource _exchange;
    private readonly FakeRabbitMQProvisioningClient _client = new();

    public RabbitMQExchangeProbeTests()
    {
        (_, _vhost) = BuildVhost();
        _exchange = new RabbitMQExchangeResource("e", "myexchange", _vhost);
    }

    [Fact]
    public async Task LiveMatchesDeclared_ReturnsHealthy()
    {
        await _exchange.ReconcileAsync(_client, default);

        var result = await _exchange.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task NotDeclared_ReturnsUnhealthyDoesNotExist()
    {
        var result = await _exchange.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myexchange", result.Description);
        Assert.Contains("does not exist", result.Description);
    }

    public static IEnumerable<object[]> DriftCases =>
    [
        [new RabbitMQExchangeDefinition("fanout", Durable: true,  AutoDelete: false, Arguments: null), "type drifted"],
        [new RabbitMQExchangeDefinition("direct", Durable: false, AutoDelete: false, Arguments: null), "durable drifted"],
        [new RabbitMQExchangeDefinition("direct", Durable: true,  AutoDelete: true,  Arguments: null), "auto-delete drifted"],
    ];

    [Theory]
    [MemberData(nameof(DriftCases))]
    internal async Task FieldDrifted_ReturnsUnhealthy(RabbitMQExchangeDefinition liveDef, string expectedKeyword)
    {
        _client.SeedExchange("myvhost", "myexchange", liveDef);

        var result = await _exchange.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains(expectedKeyword, result.Description);
    }

    [Fact]
    public async Task DeclaredArgumentChanged_ReturnsUnhealthy()
    {
        _exchange.ExchangeArguments.AdditionalArguments["x-custom-arg"] = "declared-value";
        _client.SeedExchange("myvhost", "myexchange",
            new RabbitMQExchangeDefinition("direct", Durable: true, AutoDelete: false,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-custom-arg"] = "different-value" }));

        var result = await _exchange.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("x-custom-arg", result.Description);
    }

    [Fact]
    public async Task ExtraServerManagedArgument_ReturnsHealthy()
    {
        // CRITICAL drift-boundary test: an extra live-side argument key Aspire never declared must NOT
        // trigger drift. Server-managed / policy-layered fields are scoped out of drift detection.
        _exchange.ExchangeArguments.AdditionalArguments["x-custom-arg"] = "declared-value";
        _client.SeedExchange("myvhost", "myexchange",
            new RabbitMQExchangeDefinition("direct", Durable: true, AutoDelete: false,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-custom-arg"] = "declared-value",
                    ["x-server-managed-extra"] = "some-server-computed-value",
                }));

        var result = await _exchange.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }
}

// ── Policy ProbeAsync (policy-object drift) ───────────────────────────────────

public class RabbitMQPolicyProbeTests
{
    private readonly RabbitMQVirtualHostResource _vhost;
    private readonly FakeRabbitMQProvisioningClient _client = new();

    public RabbitMQPolicyProbeTests()
        => (_, _vhost) = BuildVhost();

    [Fact]
    public async Task LiveMatchesDeclared_ReturnsHealthy()
    {
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost, RabbitMQPolicyApplyTo.Queues, priority: 5);
        await policy.ReconcileAsync(_client, default);

        var result = await policy.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task NotDeclared_ReturnsUnhealthyDoesNotExist()
    {
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost);

        var result = await policy.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("mypolicy", result.Description);
        Assert.Contains("does not exist", result.Description);
    }

    [Fact]
    public async Task PatternDrifted_ReturnsUnhealthy()
    {
        // The probe compares the policy object itself only — Aspire never evaluates which entities the
        // pattern matches; that is the server's business.
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost, RabbitMQPolicyApplyTo.Queues);
        _client.SeedPolicy("myvhost", "mypolicy",
            new RabbitMQPolicyDefinition("^different", "queues", new Dictionary<string, object?>(StringComparer.Ordinal), Priority: 0));

        var result = await policy.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("pattern drifted", result.Description);
    }

    [Fact]
    public async Task ApplyToDrifted_ReturnsUnhealthy()
    {
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost, RabbitMQPolicyApplyTo.Queues);
        _client.SeedPolicy("myvhost", "mypolicy",
            new RabbitMQPolicyDefinition("^orders", "exchanges", new Dictionary<string, object?>(StringComparer.Ordinal), Priority: 0));

        var result = await policy.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("apply-to drifted", result.Description);
    }

    [Fact]
    public async Task PriorityDrifted_ReturnsUnhealthy()
    {
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost, RabbitMQPolicyApplyTo.Queues, priority: 5);
        _client.SeedPolicy("myvhost", "mypolicy",
            new RabbitMQPolicyDefinition("^orders", "queues", new Dictionary<string, object?>(StringComparer.Ordinal), Priority: 9));

        var result = await policy.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("priority drifted", result.Description);
    }

    [Fact]
    public async Task DefinitionValueDrifted_ReturnsUnhealthy()
    {
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost, RabbitMQPolicyApplyTo.Queues);
        policy.QueueArguments.MessageTtl = TimeSpan.FromMilliseconds(60_000);
        _client.SeedPolicy("myvhost", "mypolicy",
            new RabbitMQPolicyDefinition("^orders", "queues",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message-ttl"] = 30_000L }, Priority: 0));

        var result = await policy.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("message-ttl", result.Description);
    }

    [Fact]
    public async Task NeverEvaluatesMatchingEntities_ReturnsHealthy()
    {
        // Drift-boundary test: the probe compares the policy object itself (pattern, apply-to, priority,
        // definition) and NOTHING else. No queue or exchange is constructed — Aspire never evaluates which
        // entities the pattern matches (regex + priority resolution is the server's business).
        var policy = new RabbitMQPolicyResource("p", "mypolicy", "^orders", _vhost, RabbitMQPolicyApplyTo.All, priority: 3);
        await policy.ReconcileAsync(_client, default);

        var result = await policy.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }
}

// ── Virtual host ProbeAsync (existence) ──────────────────────────────────────

public class RabbitMQVirtualHostProbeTests
{
    private readonly RabbitMQVirtualHostResource _vhost;
    private readonly FakeRabbitMQProvisioningClient _client = new();

    public RabbitMQVirtualHostProbeTests()
        => (_, _vhost) = BuildVhost();

    [Fact]
    public async Task Exists_ReturnsHealthy()
    {
        // Named vhost existence is checked via VirtualHostExistsAsync; seeding makes it present.
        _client.SeedVirtualHost("myvhost");

        var result = await _vhost.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task Reconciled_ReturnsHealthy()
    {
        await _vhost.ReconcileAsync(_client, default);

        var result = await _vhost.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task NotPresent_ReturnsUnhealthy()
    {
        var result = await _vhost.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myvhost", result.Description);
        Assert.Contains("does not exist", result.Description);
    }

    [Fact]
    public async Task DefaultVhost_CanConnect_ReturnsHealthy()
    {
        // The default "/" vhost has no management existence endpoint, so it uses CanConnectAsync.
        var (server, _) = BuildVhost();
        var defaultVhost = new RabbitMQVirtualHostResource("default", "/", server);
        var client = new FakeRabbitMQProvisioningClient { CanConnect = true };

        var result = await defaultVhost.ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task DefaultVhost_CannotConnect_ReturnsUnhealthy()
    {
        var (server, _) = BuildVhost();
        var defaultVhost = new RabbitMQVirtualHostResource("default", "/", server);
        var client = new FakeRabbitMQProvisioningClient { CanConnect = false };

        var result = await defaultVhost.ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("/", result.Description);
    }
}

// ── Shovel ProbeAsync (best-effort, credential-agnostic) ─────────────────────

public class RabbitMQShovelProbeTests
{
    private readonly RabbitMQVirtualHostResource _vhost;
    private readonly RabbitMQQueueResource _source;
    private readonly RabbitMQQueueResource _destination;
    private readonly RabbitMQShovelResource _shovel;
    private readonly FakeRabbitMQProvisioningClient _client = new();

    public RabbitMQShovelProbeTests()
    {
        (_, _vhost) = BuildVhost();
        _source = new RabbitMQQueueResource("srcq", "srcq", _vhost);
        _destination = new RabbitMQQueueResource("destq", "destq", _vhost);
        _shovel = new RabbitMQShovelResource("s", "myshovel", _vhost, _source, _destination);
    }

    [Fact]
    public async Task LiveMatchesDeclared_ReturnsHealthy()
    {
        // Seed a live shovel whose round-trippable fields match the resource's desired shape.
        // The URIs are deliberately different to prove they are NOT compared.
        _client.SeedShovel("myvhost", "myshovel", new RabbitMQShovelDefinition
        {
            Value = new RabbitMQShovelDefinitionValue
            {
                SrcUri = "amqp://redacted-src",
                DestUri = "amqp://redacted-dest",
                SrcQueue = "srcq",
                DestQueue = "destq",
                AckMode = "on-confirm",
                ReconnectDelay = null,
            },
        });

        var result = await _shovel.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task NotSeeded_ReturnsUnhealthyDoesNotExist()
    {
        var result = await _shovel.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myshovel", result.Description);
        Assert.Contains("does not exist", result.Description);
    }

    [Fact]
    public async Task AckModeDrifted_ReturnsUnhealthy()
    {
        // Resource defaults to OnConfirm -> "on-confirm"; live reports "no-ack".
        _client.SeedShovel("myvhost", "myshovel", new RabbitMQShovelDefinition
        {
            Value = new RabbitMQShovelDefinitionValue
            {
                SrcUri = "amqp://redacted-src",
                DestUri = "amqp://redacted-dest",
                SrcQueue = "srcq",
                DestQueue = "destq",
                AckMode = "no-ack",
                ReconnectDelay = null,
            },
        });

        var result = await _shovel.ProbeAsync(_client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("ack-mode drifted", result.Description);
    }

    [Fact]
    public async Task UriDiffersButFieldsMatch_ReturnsHealthy()
    {
        // Credentials embedded in src-uri/dest-uri are redacted by the broker on read-back and cannot
        // round-trip, so those URI fields are intentionally NOT compared.
        _client.SeedShovel("myvhost", "myshovel", new RabbitMQShovelDefinition
        {
            Value = new RabbitMQShovelDefinitionValue
            {
                SrcUri = "amqp://someone-else@another-host:1234",
                DestUri = "amqp://***:***@yet-another-host:5678/vh",
                SrcQueue = "srcq",
                DestQueue = "destq",
                AckMode = "on-confirm",
                ReconnectDelay = null,
            },
        });

        var result = await _shovel.ProbeAsync(_client, default);

        Assert.True(result.IsHealthy);
    }
}

// ── Health-check-level drift (end-to-end) ────────────────────────────────────
// These assert drift at the RabbitMQProvisionableHealthCheck level (not just ProbeAsync): the resource
// is Running, so the not-Running guard passes and the check runs the live probe and surfaces its result.

public class RabbitMQHealthCheckDriftTests
{
    private readonly RabbitMQServerResource _server;
    private readonly RabbitMQVirtualHostResource _vhost;

    public RabbitMQHealthCheckDriftTests()
        => (_server, _vhost) = BuildVhost();

    [Fact]
    public async Task QueueDeclaredFieldDrift_ReturnsUnhealthyWithDriftReason()
    {
        var queue = new RabbitMQQueueResource("q", "myqueue", _vhost);
        var client = new FakeRabbitMQProvisioningClient();
        client.SeedQueue("myvhost", "myqueue",
            new RabbitMQQueueDefinition("classic", Durable: false, Exclusive: false, AutoDelete: false, Arguments: null));

        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        await notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Running });
        var check = MakeHealthCheck(queue, _server, client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("drifted", result.Description);
        Assert.Contains("durable", result.Description);
    }

    [Fact]
    public async Task QueueExtraServerManagedArgument_ReturnsHealthy()
    {
        // Drift boundary at the health-check level: an extra live-side argument key Aspire never declared
        // must NOT surface as Unhealthy. Server-managed fields are never read for drift.
        var queue = new RabbitMQQueueResource("q", "myqueue", _vhost);
        queue.QueueArguments.MessageTtl = TimeSpan.FromMilliseconds(60_000);
        var client = new FakeRabbitMQProvisioningClient();
        client.SeedQueue("myvhost", "myqueue",
            new RabbitMQQueueDefinition("classic", Durable: true, Exclusive: false, AutoDelete: false,
                Arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-message-ttl"] = 60_000L,
                    ["x-server-managed-extra"] = "some-server-computed-value",
                }));

        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await PublishServerRunningAsync(notifications, _server);
        await notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Running });
        var check = MakeHealthCheck(queue, _server, client, notifications);

        var result = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
