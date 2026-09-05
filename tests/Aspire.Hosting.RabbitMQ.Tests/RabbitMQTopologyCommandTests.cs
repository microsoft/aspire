// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.RabbitMQ.Tests.TestServices;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using static Aspire.Hosting.RabbitMQ.Tests.TestServices.RabbitMQTopologyTestFactory;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class RabbitMQTopologyCommandTests
{
    private static (RabbitMQServerResource server, RabbitMQVirtualHostResource vhost, RabbitMQQueueResource queue) BuildModel()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("vh");
        var queue = vhost.AddQueue("q");
        return (server.Resource, vhost.Resource, queue.Resource);
    }

    private static ResourceCommandAnnotation GetCommand(IResource resource, string commandName)
        => resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == commandName);

    private static UpdateCommandStateContext StartStateContext(string childState, bool parentRunning, out ResourceNotificationService notifications)
    {
        notifications = ResourceNotificationServiceTestHelpers.Create();
        if (parentRunning)
        {
            var (_, vhostResource) = BuildVhost("vh");
            notifications.PublishUpdateAsync(vhostResource, s => s with { State = KnownResourceStates.Running }).GetAwaiter().GetResult();
        }

        var services = new ServiceCollection();
        services.AddSingleton(notifications);
        return new()
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "RabbitMQQueueResource",
                Properties = [],
                State = new ResourceStateSnapshot(childState, null),
            },
            Services = services.BuildServiceProvider(),
        };
    }

    private static UpdateCommandStateContext StateContext(string state)
        => new()
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "RabbitMQQueueResource",
                Properties = [],
                State = new ResourceStateSnapshot(state, null),
            },
            Services = new ServiceCollection().BuildServiceProvider(),
        };

    private static ExecuteCommandContext ExecuteContext(IServiceProvider services, string resourceName, ILogger logger)
        => new()
        {
            Services = services,
            ResourceName = resourceName,
            CancellationToken = default,
            Logger = logger,
            Arguments = new InteractionInputCollection([]),
        };

    [Fact]
    public void Command_TopologyChild_HasStartStopRestartCommands()
    {
        var (_, _, queue) = BuildModel();

        var start = GetCommand(queue, KnownResourceCommands.StartCommand);
        var stop = GetCommand(queue, KnownResourceCommands.StopCommand);
        var restart = GetCommand(queue, KnownResourceCommands.RestartCommand);

        Assert.Equal(KnownResourceCommands.StartCommand, start.Name);
        Assert.Equal(KnownResourceCommands.StopCommand, stop.Name);
        Assert.Equal(KnownResourceCommands.RestartCommand, restart.Name);
    }

    [Theory]
    [InlineData("Exited", ResourceCommandState.Enabled)]
    [InlineData("Finished", ResourceCommandState.Enabled)]
    [InlineData("FailedToStart", ResourceCommandState.Enabled)]
    [InlineData("NotStarted", ResourceCommandState.Enabled)]
    [InlineData("Running", ResourceCommandState.Disabled)]
    public void Command_StartUpdateState_ParentRunning_EnabledOnlyWhenChildStopped(string state, ResourceCommandState expected)
    {
        var (_, _, queue) = BuildModel();
        var start = GetCommand(queue, KnownResourceCommands.StartCommand);

        Assert.Equal(expected, start.UpdateState(StartStateContext(state, parentRunning: true, out _)));
    }

    [Theory]
    [InlineData("Exited")]
    [InlineData("Finished")]
    [InlineData("FailedToStart")]
    [InlineData("NotStarted")]
    public void Command_StartUpdateState_ParentNotRunning_AlwaysDisabled(string childState)
    {
        var (_, _, queue) = BuildModel();
        var start = GetCommand(queue, KnownResourceCommands.StartCommand);

        Assert.Equal(ResourceCommandState.Disabled, start.UpdateState(StartStateContext(childState, parentRunning: false, out _)));
    }

    [Theory]
    [InlineData("Running", ResourceCommandState.Enabled)]
    [InlineData("Exited", ResourceCommandState.Disabled)]
    [InlineData("NotStarted", ResourceCommandState.Disabled)]
    [InlineData("FailedToStart", ResourceCommandState.Disabled)]
    public void Command_StopUpdateState_EnabledOnlyWhenRunning(string state, ResourceCommandState expected)
    {
        var (_, _, queue) = BuildModel();
        var stop = GetCommand(queue, KnownResourceCommands.StopCommand);

        Assert.Equal(expected, stop.UpdateState(StateContext(state)));
    }

    [Theory]
    [InlineData("Running", ResourceCommandState.Enabled)]
    [InlineData("Exited", ResourceCommandState.Disabled)]
    [InlineData("NotStarted", ResourceCommandState.Disabled)]
    [InlineData("FailedToStart", ResourceCommandState.Disabled)]
    public void Command_RestartUpdateState_EnabledOnlyWhenRunning(string state, ResourceCommandState expected)
    {
        var (_, _, queue) = BuildModel();
        var restart = GetCommand(queue, KnownResourceCommands.RestartCommand);

        Assert.Equal(expected, restart.UpdateState(StateContext(state)));
    }

    [Fact]
    public async Task Command_Stop_WhileServerRunning_DeletesEntityAndTransitionsToExited()
    {
        var (server, _, queue) = BuildModel();
        var client = new FakeRabbitMQProvisioningClient();
        var host = RabbitMQTopologyTestHost.Create(server.Name, client);

        await host.Notifications.PublishUpdateAsync(server, s => s with { State = KnownResourceStates.Running });
        await host.Notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Running });

        var stop = GetCommand(queue, KnownResourceCommands.StopCommand);
        var result = await stop.ExecuteCommand(ExecuteContext(host.Services, queue.Name, host.Logger));

        Assert.True(result.Success);
        Assert.Contains("DeleteQueueAsync(vh, q)", client.Calls);
        Assert.True(host.Notifications.TryGetCurrentState(queue.Name, out var state));
        Assert.Equal(KnownResourceStates.Exited, state!.Snapshot.State?.Text);
    }

    [Fact]
    public async Task Command_Stop_WhileServerNotRunning_SkipsBrokerDeleteButStillExits()
    {
        var (server, _, queue) = BuildModel();
        var client = new FakeRabbitMQProvisioningClient();
        var host = RabbitMQTopologyTestHost.Create(server.Name, client);

        await host.Notifications.PublishUpdateAsync(server, s => s with { State = KnownResourceStates.Exited });
        await host.Notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Running });

        var stop = GetCommand(queue, KnownResourceCommands.StopCommand);
        var result = await stop.ExecuteCommand(ExecuteContext(host.Services, queue.Name, host.Logger));

        Assert.True(result.Success);
        Assert.Empty(client.Calls);
        Assert.True(host.Notifications.TryGetCurrentState(queue.Name, out var state));
        Assert.Equal(KnownResourceStates.Exited, state!.Snapshot.State?.Text);
    }

    [Fact]
    public async Task Command_Start_RecreatesEntityAndTransitionsToRunning()
    {
        var (server, _, queue) = BuildModel();
        var client = new FakeRabbitMQProvisioningClient();
        var host = RabbitMQTopologyTestHost.Create(server.Name, client);

        await host.Notifications.PublishUpdateAsync(server, s => s with { State = KnownResourceStates.Running });
        await host.Notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Exited });

        var start = GetCommand(queue, KnownResourceCommands.StartCommand);
        var result = await start.ExecuteCommand(ExecuteContext(host.Services, queue.Name, host.Logger));

        Assert.True(result.Success);
        Assert.Contains("DeclareQueueAsync(vh, q, True, False, False)", client.Calls);
        Assert.True(host.Notifications.TryGetCurrentState(queue.Name, out var state));
        Assert.Equal(KnownResourceStates.Running, state!.Snapshot.State?.Text);
    }

    [Fact]
    public async Task Command_Restart_WhileServerRunning_DeletesThenRecreatesAndEndsRunning()
    {
        var (server, _, queue) = BuildModel();
        var client = new FakeRabbitMQProvisioningClient();
        var host = RabbitMQTopologyTestHost.Create(server.Name, client);

        await host.Notifications.PublishUpdateAsync(server, s => s with { State = KnownResourceStates.Running });
        await host.Notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Running });

        var restart = GetCommand(queue, KnownResourceCommands.RestartCommand);
        var result = await restart.ExecuteCommand(ExecuteContext(host.Services, queue.Name, host.Logger));

        Assert.True(result.Success);

        var deleteIndex = client.Calls.IndexOf("DeleteQueueAsync(vh, q)");
        var declareIndex = client.Calls.IndexOf("DeclareQueueAsync(vh, q, True, False, False)");
        Assert.True(deleteIndex >= 0, "Expected a delete call during restart.");
        Assert.True(declareIndex >= 0, "Expected a declare call during restart.");
        Assert.True(deleteIndex < declareIndex, "Expected delete to occur before declare during restart.");

        Assert.True(host.Notifications.TryGetCurrentState(queue.Name, out var state));
        Assert.Equal(KnownResourceStates.Running, state!.Snapshot.State?.Text);
    }

    [Fact]
    public async Task Command_StoppedChild_DoesNotDriftProbe()
    {
        var (server, _, queue) = BuildModel();
        var client = new FakeRabbitMQProvisioningClient();
        var host = RabbitMQTopologyTestHost.Create(server.Name, client);

        await host.Notifications.PublishUpdateAsync(server, s => s with { State = KnownResourceStates.Running });
        await host.Notifications.PublishUpdateAsync(queue, s => s with { State = KnownResourceStates.Running });
        var stop = GetCommand(queue, KnownResourceCommands.StopCommand);
        await stop.ExecuteCommand(ExecuteContext(host.Services, queue.Name, host.Logger));

        var callsBeforeHealthCheck = client.Calls.Count;

        var check = new RabbitMQProvisionableHealthCheck(queue, server.Name, client, host.Notifications,
            NullLogger<RabbitMQProvisionableHealthCheck>.Instance);
        var health = await check.CheckHealthAsync(MakeHealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Contains("not yet Running", health.Description);
        Assert.Equal(callsBeforeHealthCheck, client.Calls.Count);
    }
}
