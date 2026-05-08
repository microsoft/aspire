// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
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

#pragma warning disable CS0618 // obsolete constructor is fine for tests
    private static ResourceNotificationService MakeNotifications() =>
        new(NullLogger<ResourceNotificationService>.Instance, new NullHostApplicationLifetime());
#pragma warning restore CS0618

    private static ResourceLoggerService MakeResourceLogger() => new();

    // ── RabbitMQProvisionableHealthCheck ─────────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_SelfPending_ReturnsDegraded()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        // ProvisionedTask is pending — no ApplyAsync called yet.
        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("in progress", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_SelfFaulted_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        // Drive the resource through ApplyAsync with a failing client to fault the TCS.
        var failingClient = new FakeRabbitMQProvisioningClient();
        failingClient.FailVirtualHostNames.Add("myvhost");
        await ((IRabbitMQProvisionable)vhost).ApplyAsync(failingClient, MakeNotifications(), MakeResourceLogger(), default);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("boom", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_DependencyFaulted_ReturnsUnhealthyWithDepName()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("myqueue", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        // Simulate a policy dependency by injecting a pre-faulted stub.
        var dep = new StubProvisionable("mypolicy", Task.FromException(new DistributedApplicationException("policy failed")));

        var check = new RabbitMQProvisionableHealthCheckWithDeps(queue, [dep], client);

        // Drive queue through ApplyAsync to complete its TCS.
        await ((IRabbitMQProvisionable)queue).ApplyAsync(client, MakeNotifications(), MakeResourceLogger(), default);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("mypolicy", result.Description);
        Assert.Contains("policy failed", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_AllCompleteProbeHealthy_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient();
        var check = new RabbitMQProvisionableHealthCheck(vhost, client, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        await ((IRabbitMQProvisionable)vhost).ApplyAsync(client, MakeNotifications(), MakeResourceLogger(), default);

        var result = await check.CheckHealthAsync(MakeContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_AllCompleteProbeUnhealthy_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        // Use a client that fails CanConnectAsync for the probe stage.
        var probeClient = new FakeRabbitMQProvisioningClient { CanConnect = false };
        var check = new RabbitMQProvisionableHealthCheck(vhost, probeClient, NullLogger<RabbitMQProvisionableHealthCheck>.Instance);

        // Use a separate client that succeeds for ApplyAsync.
        var applyClient = new FakeRabbitMQProvisioningClient();
        await ((IRabbitMQProvisionable)vhost).ApplyAsync(applyClient, MakeNotifications(), MakeResourceLogger(), default);

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

        var result = await ((IRabbitMQProvisionable)queue).ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task QueueProbeAsync_QueueMissing_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var queue = new RabbitMQQueueResource("q", "myqueue", vhost);
        var client = new FakeRabbitMQProvisioningClient();
        client.FailQueueNames.Add("myqueue");

        var result = await ((IRabbitMQProvisionable)queue).ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myqueue", result.Description);
    }

    [Fact]
    public async Task ExchangeProbeAsync_ExchangeExists_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("e", "myexchange", vhost);
        var client = new FakeRabbitMQProvisioningClient();

        var result = await ((IRabbitMQProvisionable)exchange).ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task ExchangeProbeAsync_ExchangeMissing_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var exchange = new RabbitMQExchangeResource("e", "myexchange", vhost);
        var client = new FakeRabbitMQProvisioningClient();
        client.FailExchangeNames.Add("myexchange");

        var result = await ((IRabbitMQProvisionable)exchange).ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("myexchange", result.Description);
    }

    [Fact]
    public async Task VhostProbeAsync_CanConnect_ReturnsHealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient { CanConnect = true };

        var result = await ((IRabbitMQProvisionable)vhost).ProbeAsync(client, default);

        Assert.True(result.IsHealthy);
    }

    [Fact]
    public async Task VhostProbeAsync_CannotConnect_ReturnsUnhealthy()
    {
        var (_, vhost) = BuildVhost();
        var client = new FakeRabbitMQProvisioningClient { CanConnect = false };

        var result = await ((IRabbitMQProvisionable)vhost).ProbeAsync(client, default);

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

        var result = await ((IRabbitMQProvisionable)shovel).ProbeAsync(client, default);

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

        var result = await ((IRabbitMQProvisionable)shovel).ProbeAsync(client, default);

        Assert.False(result.IsHealthy);
        Assert.Contains("starting", result.Description);
    }

    // ── Private test helpers ──────────────────────────────────────────────────

    /// <summary>
    /// A minimal <see cref="IRabbitMQProvisionable"/> stub used to inject a pre-completed or faulted
    /// <see cref="IRabbitMQProvisionable.ProvisionedTask"/> into tests without needing a real resource.
    /// </summary>
    private sealed class StubProvisionable(string name, Task provisionedTask) : IRabbitMQProvisionable
    {
        public string Name => name;
        public Task ProvisionedTask => provisionedTask;
        public Task ApplyAsync(IRabbitMQProvisioningClient client, ResourceNotificationService notifications, ResourceLoggerService resourceLogger, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Wraps <see cref="RabbitMQProvisionableHealthCheck"/> but injects explicit dependencies,
    /// allowing tests to verify the dependency-awaiting stage without needing real policy resources.
    /// </summary>
    private sealed class RabbitMQProvisionableHealthCheckWithDeps(
        IRabbitMQProvisionable self,
        IEnumerable<IRabbitMQProvisionable> deps,
        IRabbitMQProvisioningClient client) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await self.ProvisionedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy($"Provisioning of '{self.Name}' failed: {ex.Message}", ex);
            }

            foreach (var dep in deps)
            {
                try
                {
                    await dep.ProvisionedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return HealthCheckResult.Unhealthy(
                        $"Dependent resource '{dep.Name}' failed to provision: {ex.Message}", ex);
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

    private sealed class NullHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
