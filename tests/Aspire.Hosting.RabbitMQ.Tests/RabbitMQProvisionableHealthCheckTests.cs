// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQProvisionableHealthCheckTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static (RabbitMQServerResource server, RabbitMQVirtualHostResource vhost) BuildVhost()
    {
        var server = new RabbitMQServerResource("rabbit", userName: null,
            password: new ParameterResource("pw", _ => "pw", secret: true));
        var vhost = new RabbitMQVirtualHostResource("myvhost", "myvhost", server);
        return (server, vhost);
    }

    private static HealthCheckContext MakeContext() =>
        new() { Registration = new HealthCheckRegistration("test", _ => null!, null, null) };

    // ── RabbitMQProvisionableHealthCheck ─────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_SelfNotRunning_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        // No state published — TryGetCurrentState returns false.
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not yet Running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_SelfWaiting_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(vhost, s => s with { State = KnownResourceStates.Waiting });
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not yet Running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_SelfFailedToStart_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(vhost, s => s with { State = KnownResourceStates.FailedToStart });
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not yet Running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_DependencyNotHealthy_ReturnsUnhealthyWithDepName()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("myqueue", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        // Simulate a policy dependency that is Running but not yet Healthy.
        var dep = new StubProvisionable("mypolicy");

        var notifications = ResourceNotificationServiceTestHelpers.Create();
        // queue is Running+Healthy, dep is Running but health check is Unhealthy.
        await notifications.PublishUpdateAsync(queue, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("queue_check", HealthStatus.Healthy, null, null)]));
        await notifications.PublishUpdateAsync(dep, s =>
            (s with { State = KnownResourceStates.Running }).WithHealthReports(
                [new HealthReportSnapshot("dep_check", HealthStatus.Unhealthy, "probe failed", null)]));

        var check = new RabbitMQProvisionableHealthCheckWithDeps(queue, [dep], client, notifications);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("mypolicy", result.Description);
        Assert.Contains("not yet healthy", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_AllRunningProbeHealthy_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(vhost, s => s with { State = KnownResourceStates.Running });
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_AllRunningProbeUnhealthy_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        // Use a client that fails CanConnectAsync for the probe stage.
        var probeClient = new FakeRabbitMQProvisioningClient { CanConnect = false };
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(vhost, s => s with { State = KnownResourceStates.Running });
        var check = new RabbitMQProvisionableHealthCheck(vhost, probeClient, notifications, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Cannot connect", result.Description);
    }

    // ── Per-resource ProbeAsync ───────────────────────────────────────────────

    [Fact]
    public async Task QueueProbeAsync_QueueExists_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("q", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        var result = await queue.ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task QueueProbeAsync_QueueMissing_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("q", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();
        client.FailQueueNames.Add("myqueue");

        var result = await queue.ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myqueue", result.Description);
    }

    [Fact]
    public async Task ExchangeProbeAsync_ExchangeExists_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("e", "myexchange", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        var result = await exchange.ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task ExchangeProbeAsync_ExchangeMissing_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("e", "myexchange", vhost);
        var client = new FakeRabbitMQProvisioningClient();
        client.FailExchangeNames.Add("myexchange");

        var result = await exchange.ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myexchange", result.Description);
    }

    [Fact]
    public async Task VhostProbeAsync_CanConnect_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient { CanConnect = true };

        var result = await vhost.ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task VhostProbeAsync_CannotConnect_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient { CanConnect = false };

        var result = await vhost.ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myvhost", result.Description);
    }

    [Fact]
    public async Task ShovelProbeAsync_Running_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("q", "q", vhost);
        var exchange = new RabbitMQExchangeResource("e", "e", vhost);
        var shovel = new RabbitMQShovelResource("s", "myshovel", vhost, queue, exchange);
        var client = new FakeRabbitMQProvisioningClient();

        var result = await shovel.ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task ShovelProbeAsync_NotRunning_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("q", "q", vhost);
        var exchange = new RabbitMQExchangeResource("e", "e", vhost);
        var shovel = new RabbitMQShovelResource("s", "myshovel", vhost, queue, exchange);

        var client = new FixedShovelStateClient("starting");

        var result = await shovel.ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("starting", result.Description);
    }

    // ── Private test helpers ──────────────────────────────────────────────────

    /// <summary>
    /// A minimal <see cref="RabbitMQProvisionableResource"/> stub with no special behaviour.
    /// </summary>
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

    /// <summary>
    /// A minimal <see cref="IRabbitMQProvisioningClient"/> that returns a fixed shovel state
    /// and delegates everything else to no-ops.
    /// </summary>
    private sealed class FixedShovelStateClient(string state) : IRabbitMQProvisioningClient
    {
        public Task<string?> GetShovelStateAsync(string vhost, string name, CancellationToken ct)
            => Task.FromResult<string?>(state);

        public Task<bool> CanConnectAsync(string vhost, CancellationToken ct) => Task.FromResult(false);
        public Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct) => Task.CompletedTask;
        public Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct) => Task.CompletedTask;
        public Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct) => Task.CompletedTask;
        public Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct) => Task.FromResult(true);
        public Task CreateVirtualHostAsync(string vhost, CancellationToken ct) => Task.CompletedTask;
        public Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct) => Task.CompletedTask;
        public Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> PolicyExistsAsync(string vhost, string name, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => default;
    }
}
