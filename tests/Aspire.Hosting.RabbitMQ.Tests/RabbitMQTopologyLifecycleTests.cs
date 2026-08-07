// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.RabbitMQ.Tests;

/// <summary>
/// Lifecycle tests for the parent-driven topology model. The cascade is two-level:
/// <list type="bullet">
///   <item><description>
///     <see cref="RabbitMQVirtualHostResource"/> subscribes to the <strong>server's</strong>
///     <see cref="ResourceReadyEvent"/>/<see cref="ResourceStoppedEvent"/>.
///   </description></item>
///   <item><description>
///     Queues, exchanges, policies, and shovels subscribe to their <strong>vhost's</strong>
///     <see cref="ResourceReadyEvent"/>/<see cref="ResourceStoppedEvent"/>.
///   </description></item>
/// </list>
/// This ordering guarantees that queues/exchanges/etc. only reconcile after their vhost is Running,
/// not racing off the server event simultaneously.
/// </summary>
/// <remarks>
/// The <c>WithRabbitMQParentLifecycle</c> handler resolves everything it needs from
/// <see cref="ResourceReadyEvent.Services"/>: a keyed <see cref="Provisioning.IRabbitMQProvisioningClient"/>
/// (key = server name), a <see cref="ResourceNotificationService"/>, and a <see cref="ResourceLoggerService"/>.
/// <see cref="RabbitMQTopologyTestHost"/> builds that self-consistent service graph and publishes events
/// through the builder's eventing pipeline (subscriptions are registered at <c>AddVirtualHost</c>/<c>AddQueue</c>
/// time, so no app host / containers are started).
/// </remarks>
public class RabbitMQTopologyLifecycleTests
{
    [Fact]
    public async Task Lifecycle_ParentReadyEvent_RunsChildReconcile()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        await host.PublishReadyAsync(builder, server.Resource);

        Assert.Contains("CreateVirtualHostAsync(vh)", host.Client.Calls);
        Assert.Contains("DeclareQueueAsync(vh, q, True, False, False)", host.Client.Calls);
        Assert.Equal(KnownResourceStates.Running, host.CurrentState(vhost.Resource.Name));
        Assert.Equal(KnownResourceStates.Running, host.CurrentState(queue.Resource.Name));
    }

    [Fact]
    public async Task Lifecycle_ParentReadyEventReFires_ReRunsChildReconcile()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        vhost.AddQueue("q");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        // Each server-ready session cascades all the way down (server → vhost → queue) via StartCore's
        // auto-fired child ready events. Firing the server ready event twice simulates a restart.
        await host.PublishReadyAsync(builder, server.Resource);
        await host.PublishReadyAsync(builder, server.Resource);

        Assert.Equal(2, host.Client.Calls.Count(c => c == "DeclareQueueAsync(vh, q, True, False, False)"));
        Assert.Equal(2, host.Client.Calls.Count(c => c == "CreateVirtualHostAsync(vh)"));
    }

    [Fact]
    public async Task Lifecycle_ParentStoppedEvent_CancelsInFlightReconcile()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");

        // Rendezvous: block DeclareQueueAsync until the reconcile token is cancelled — deterministic, no delays.
        var reconcileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeRabbitMQProvisioningClient
        {
            OnDeclareQueue = async ct =>
            {
                reconcileStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    observedCancellation.TrySetResult(true);
                    throw;
                }
            }
        };
        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name, client);

        // Don't await — the cascade blocks inside OnDeclareQueue until cancelled.
        var readyTask = builder.Eventing.PublishAsync(new ResourceReadyEvent(server.Resource, host.Services));

        await reconcileStarted.Task.DefaultTimeout();
        await host.PublishStoppedAsync(builder, server.Resource, "RabbitMQServerResource");

        Assert.True(await observedCancellation.Task.DefaultTimeout());
        await readyTask.DefaultTimeout();
        Assert.NotEqual(KnownResourceStates.Running, host.CurrentState(queue.Resource.Name));
    }

    [Fact]
    public async Task Lifecycle_NoInitializeResourceSubscription_ChildDoesNotReconcileUntilParentReady()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        Assert.Empty(host.Client.Calls);

        var initialState = queue.Resource.Annotations.OfType<ResourceSnapshotAnnotation>().Single();
        Assert.Equal(KnownResourceStates.NotStarted, initialState.InitialSnapshot.State?.Text);
        Assert.Null(host.CurrentState(queue.Resource.Name));

        await host.PublishReadyAsync(builder, server.Resource);

        Assert.Contains("CreateVirtualHostAsync(vh)", host.Client.Calls);
        Assert.Contains("DeclareQueueAsync(vh, q, True, False, False)", host.Client.Calls);
    }

    [Fact]
    public async Task Lifecycle_ServerStopped_CascadesAllDescendantsToExited()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");
        var exchange = vhost.AddExchange("ex");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        await host.PublishReadyAsync(builder, server.Resource);
        Assert.Equal(KnownResourceStates.Running, host.CurrentState(queue.Resource.Name));

        await host.PublishStoppedAsync(builder, server.Resource, "RabbitMQServerResource");

        Assert.Equal(KnownResourceStates.Exited, host.CurrentState(vhost.Resource.Name));
        Assert.Equal(KnownResourceStates.Exited, host.CurrentState(queue.Resource.Name));
        Assert.Equal(KnownResourceStates.Exited, host.CurrentState(exchange.Resource.Name));
    }

    [Fact]
    public async Task Lifecycle_ServerStopped_SkipsBrokerDeletesForAllDescendants()
    {
        // Broker-down fast stop (Defect 4): when the server stops, the broker is dead, so StopCore must NOT
        // issue any delete for the vhost OR its children at any depth. The delete gate reads the SERVER's
        // Aspire state (not Running) independently at every cascade level, so no threading of a flag is needed.
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        vhost.AddQueue("q");
        vhost.AddExchange("ex");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        await host.PublishReadyAsync(builder, server.Resource);

        // No server state is ever published as Running via notifications, so the IsResourceRunning(server)
        // gate in StopCore evaluates false at every level and all deletes are skipped.
        var deletesBefore = host.DeleteCallCount;
        await host.PublishStoppedAsync(builder, server.Resource, "RabbitMQServerResource");

        Assert.Equal(deletesBefore, host.DeleteCallCount);
    }

    [Fact]
    public async Task Lifecycle_VhostStoppedWhileServerRunning_DeletesChildFromBroker()
    {
        // Scenario 2/3/4 delete-verify: stopping a vhost while the broker is alive DELETES its children from
        // the broker. The vhost's stopped handler cascades to the queue, and because the server is Running the
        // StopCore delete gate passes, so the queue IS deleted (contrast with the server-down skip above).
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        await host.PublishReadyAsync(builder, server.Resource);
        await host.PublishRunningAsync(server.Resource);
        await host.PublishStoppedAsync(builder, vhost.Resource, "RabbitMQVirtualHostResource");

        Assert.Contains("DeleteQueueAsync(vh, q)", host.Client.Calls);
        Assert.Equal(KnownResourceStates.Exited, host.CurrentState(queue.Resource.Name));
    }

    [Fact]
    public async Task Lifecycle_ServerRestartAfterStop_ReReconcilesFullCascadeToRunning()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        await host.PublishReadyAsync(builder, server.Resource);
        await host.PublishStoppedAsync(builder, server.Resource, "RabbitMQServerResource");
        Assert.Equal(KnownResourceStates.Exited, host.CurrentState(queue.Resource.Name));

        await host.PublishReadyAsync(builder, server.Resource);

        Assert.Equal(KnownResourceStates.Running, host.CurrentState(vhost.Resource.Name));
        Assert.Equal(KnownResourceStates.Running, host.CurrentState(queue.Resource.Name));
        Assert.Equal(2, host.Client.Calls.Count(c => c == "DeclareQueueAsync(vh, q, True, False, False)"));
    }

    [Fact]
    public async Task Lifecycle_ExchangeRestart_ReAppliesBindings()
    {
        // Regression: when an exchange is stopped and restarted via the Start command, its bindings
        // must be re-applied. The binding reconciler subscribes to the exchange's own ResourceReadyEvent
        // (published by StartCore after setting state to Running, independent of health checks) so it
        // fires on every start/restart, not just the initial vhost cascade.
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");
        var exchange = vhost.AddExchange("ex").WithBinding(queue);

        var host = RabbitMQTopologyTestHost.Create(server.Resource.Name);

        // Pre-publish the queue as Running so WaitForResourceAsync(queue, Running) in the binding
        // reconciler completes immediately when the exchange's ResourceReadyEvent fires.
        await host.Notifications.PublishUpdateAsync(queue.Resource, s => s with { State = KnownResourceStates.Running });
        await host.PublishReadyAsync(builder, server.Resource);

        Assert.Contains("BindQueueAsync(vh, ex, q, )", host.Client.Calls);

        host.Client.Calls.Clear();

        // Simulate exchange restart: stop then fire the exchange's own ResourceReadyEvent (which
        // StartCore publishes after setting state to Running on every start/restart).
        await host.PublishStoppedAsync(builder, exchange.Resource, "RabbitMQExchangeResource");
        await host.PublishReadyAsync(builder, exchange.Resource);

        // Binding must be re-applied after restart.
        Assert.Contains("BindQueueAsync(vh, ex, q, )", host.Client.Calls);
    }
}
