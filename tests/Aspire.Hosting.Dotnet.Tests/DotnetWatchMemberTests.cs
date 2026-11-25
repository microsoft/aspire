// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // Watch policy uses experimental core hooks.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Dashboard.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchMemberTests
{
    [Fact]
    public void SingleApplicationObservationNeverUsesProxyIdentityOrCleanProxyExit()
    {
        var resource = new DotnetProjectResource("service", ".");
        var member = Create(resource, unambiguous: true);
        var starting = member.ProjectSnapshot("service", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(KnownResourceStates.Starting, starting.State?.Text);
        Assert.Null(starting.ExitCode);
        Assert.Equal(1234, Property(starting, DotnetWatchMember.TransportPidProperty));
        Assert.Equal(["dotnet-watch.transport.pid"], starting.Properties
            .Where(property => property.Name.EndsWith(".pid", StringComparison.Ordinal)).Select(property => property.Name));

        member.Observe(DotnetWatchStatusTypes.ProcessStarted, null, null, "process_started");
        var running = member.ProjectSnapshot("service", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(KnownResourceStates.Running, running.State?.Text);
        member.Observe(DotnetWatchStatusTypes.ProcessExited, null, 9, "process_exited");
        var crashed = member.ProjectSnapshot("service", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(DotnetWatchCoordinator.CrashedState, crashed.State?.Text);
        Assert.Equal(9, crashed.ExitCode);

        var proxyExit = member.ProjectSnapshot("service", Raw(KnownResourceStates.Finished, null, 0));
        Assert.Equal(KnownResourceStates.FailedToStart, proxyExit.State?.Text);
        Assert.Equal(0, Property(proxyExit, DotnetWatchMember.TransportExitProperty));
        Assert.Null(proxyExit.StartTimeStamp);
        Assert.Null(proxyExit.StopTimeStamp);
    }

    [Fact]
    public void ReplicaEventsRemainGroupObservations()
    {
        var member = Create(new DotnetProjectResource("service", "."), unambiguous: false);
        member.Observe(DotnetWatchStatusTypes.ProcessStarted, null, null, "process_started");
        member.Observe(DotnetWatchStatusTypes.ProcessExited, null, 17, "process_exited");

        foreach (var id in new[] { "service-a", "service-b" })
        {
            var snapshot = member.ProjectSnapshot(id, Raw(KnownResourceStates.Running, 1234, null) with
            {
                HealthReports = [new("endpoint", HealthStatus.Healthy, "Endpoint is healthy.", null)]
            });
            Assert.Equal(KnownResourceStates.Running, snapshot.State?.Text);
            Assert.Null(snapshot.ExitCode);
            Assert.Equal("project-group", Property(snapshot, "dotnet-watch.scope"));
            Assert.Equal("process_exited", Property(snapshot, "dotnet-watch.last-event"));
            Assert.Equal(HealthStatus.Healthy, snapshot.HealthStatus);
        }
    }

    [Fact]
    public async Task PreviousPidStrippingProjectionCannotHideLaterRawTransitions()
    {
        var resource = new DotnetProjectResource("service", ".");
        resource.Annotations.Add(new ResourceSnapshotProjectionAnnotation((_, snapshot) => snapshot with
        {
            State = "Existing projection",
            Properties = [.. snapshot.Properties.Where(property => property.Name != "executable.pid"), new("existing", true)]
        }));
        Create(resource, unambiguous: false).Configure();
        var notifications = ResourceNotificationServiceTestHelpers.Create();

        await notifications.PublishUpdateAsync(resource, "instance", _ => Raw(KnownResourceStates.Waiting, null, null));
        Assert.True(notifications.TryGetCurrentState("instance", out var waiting));
        Assert.Equal(KnownResourceStates.Waiting, waiting.Snapshot.State?.Text);
        await notifications.PublishUpdateAsync(resource, "instance", _ => Raw(KnownResourceStates.Running, 1234, null));
        await notifications.PublishUpdateAsync(resource, "instance", snapshot => snapshot with
        {
            HealthReports = [new("endpoint", HealthStatus.Healthy, "Endpoint is healthy.", null)]
        });
        Assert.True(notifications.TryGetCurrentState("instance", out var running));
        Assert.Equal(KnownResourceStates.Running, running.Snapshot.State?.Text);
        Assert.Equal(1234, Property(running.Snapshot, DotnetWatchMember.TransportPidProperty));
        Assert.Equal(HealthStatus.Healthy, running.Snapshot.HealthStatus);

        await notifications.PublishUpdateAsync(resource, "instance", _ => Raw(KnownResourceStates.Finished, null, 0));
        Assert.True(notifications.TryGetCurrentState("instance", out var stopped));
        Assert.Equal(KnownResourceStates.FailedToStart, stopped.Snapshot.State?.Text);
        Assert.Null(stopped.Snapshot.ExitCode);
        Assert.Equal(true, Property(stopped.Snapshot, "existing"));
    }

    [Fact]
    public async Task AspireStatePublicationsRemainAuthoritativeAfterTransportSnapshots()
    {
        var resource = new DotnetProjectResource("service", ".");
        Create(resource, unambiguous: true).Configure();
        var notifications = ResourceNotificationServiceTestHelpers.Create();

        await notifications.PublishUpdateAsync(resource, "service", _ => Raw(KnownResourceStates.Running, 1234, null));
        await notifications.PublishUpdateAsync(resource, "service", snapshot => snapshot with
        {
            State = KnownResourceStates.FailedToStart
        });
        Assert.True(notifications.TryGetCurrentState("service", out var failed));
        Assert.Equal(KnownResourceStates.FailedToStart, failed.Snapshot.State?.Text);

        await notifications.PublishUpdateAsync(resource, "service", snapshot => snapshot with
        {
            State = KnownResourceStates.Waiting,
            Properties =
            [
                .. snapshot.Properties,
                new(KnownProperties.Resource.WaitingFor, new[] { "dependency" })
            ]
        });
        Assert.True(notifications.TryGetCurrentState("service", out var waiting));
        Assert.Equal(KnownResourceStates.Waiting, waiting.Snapshot.State?.Text);
        Assert.Equal(
            new[] { "dependency" },
            waiting.Snapshot.Properties.Single(property => property.Name == KnownProperties.Resource.WaitingFor).Value);
    }

    [Fact]
    public async Task CoordinatorRefreshProjectsWaitingThenStarting()
    {
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var services = new ServiceCollection()
            .AddSingleton(notifications)
            .AddSingleton(new ResourceLoggerService())
            .BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var member = Create(resource, unambiguous: true);
        member.Configure();
        _ = Override(resource, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        string? stateAtStart = null;
        _ = Override(resource, KnownResourceCommands.StartCommand, context =>
        {
            Assert.True(notifications.TryGetCurrentState(context.ResourceName, out var current));
            stateAtStart = current.Snapshot.State?.Text;
            return Task.FromResult(CommandResults.Success());
        });
        await notifications.PublishUpdateAsync(resource, "service", _ => Raw(KnownResourceStates.Running, 1234, null));

        Assert.True((await member.StopOwnedAsync("service", services, CancellationToken.None)).Success);
        Assert.True(member.IsCoordinatorStopping("service"));
        await notifications.PublishUpdateAsync(resource, "service", _ => Raw(KnownResourceStates.Finished, null, 0));
        Assert.True(notifications.TryGetCurrentState("service", out var stopped));
        Assert.Equal(KnownResourceStates.Waiting, stopped.Snapshot.State?.Text);

        Assert.True((await member.StartOwnedAsync("service", services, CancellationToken.None)).Success);
        Assert.False(member.IsCoordinatorStopping("service"));
        Assert.True(member.IsCoordinatorStarting("service"));
        Assert.Equal(KnownResourceStates.Starting, stateAtStart);
        Assert.True(notifications.TryGetCurrentState("service", out var plannedStart));
        Assert.Equal(KnownResourceStates.Starting, plannedStart.Snapshot.State?.Text);

        await notifications.PublishUpdateAsync(resource, "service", _ => Raw(KnownResourceStates.Finished, null, 0));
        Assert.True(member.IsCoordinatorStarting("service"));
        Assert.True(notifications.TryGetCurrentState("service", out var oldGenerationStopped));
        Assert.Equal(KnownResourceStates.Starting, oldGenerationStopped.Snapshot.State?.Text);

        await notifications.PublishUpdateAsync(resource, "service", _ => Raw(KnownResourceStates.Starting, 5678, null));
        Assert.False(member.IsCoordinatorStarting("service"));
        Assert.True(notifications.TryGetCurrentState("service", out var starting));
        Assert.Equal(KnownResourceStates.Starting, starting.Snapshot.State?.Text);
    }

    [Fact]
    public async Task ManualStopIsInstanceSpecificAndSurvivesRefresh()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service");
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var stops = new List<string>();
        var starts = new List<string>();
        var stop = Override(resource, KnownResourceCommands.StopCommand, context =>
        {
            stops.Add(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        var start = Override(resource, KnownResourceCommands.StartCommand, context =>
        {
            starts.Add(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        // DCP launched the instance's proxy, so a Stop of it is a manual stop.
        member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Running, 1234, null));

        Assert.True((await stop.ExecuteCommand(Context("service-a", services))).Success);
        member.BeginRefresh();
        Assert.True(member.IsManuallyStopped("service-a"));
        Assert.True((await start.ExecuteCommand(Context("service-a", services))).Success);
        Assert.True((await start.ExecuteCommand(Context("service-b", services))).Success);
        Assert.Equal(["service-a"], stops);
        Assert.Equal(["service-b"], starts);
        Assert.Equal(["start service-a"], group.Calls);
        Assert.False(member.IsManuallyStopped("service-b"));
    }

    [Fact]
    public async Task RebuildReloadsTheWatchGroupInsteadOfTheOrdinaryRebuild()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service") { IsReady = false };
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var called = false;
        var stop = Override(resource, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        var rebuild = Override(resource, KnownResourceCommands.RebuildCommand, _ =>
        {
            called = true;
            return Task.FromResult(CommandResults.Success());
        });
        member.ProjectSnapshot("service-b", Raw(KnownResourceStates.Running, 1234, null));
        Assert.True((await stop.ExecuteCommand(Context("service-b", services))).Success);

        foreach (var id in new[] { "service-a", "service-b" })
        {
            var snapshot = member.ProjectSnapshot(id, Raw(KnownResourceStates.Running, 1234, null));
            Assert.Equal(ResourceCommandState.Enabled, rebuild.UpdateState(StateContext(snapshot, services)));
            Assert.True((await rebuild.ExecuteCommand(Context(id, services))).Success);
        }

        Assert.False(called);
        Assert.Equal(["rebuild service-a", "rebuild service-b"], group.Calls);
        Assert.True(member.IsManuallyStopped("service-b"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GroupFailureRecoverabilityDecidesWhetherCommandsRetryWithAFreshWatcher(bool recoverable)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service") { Failure = new("Watch group 'dotnet-watch' failed.", recoverable) };
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        member.Observe(DotnetWatchStatusTypes.ProcessStarted, null, null, "process_started");
        var called = new List<string>();
        var commands = new[] { KnownResourceCommands.RebuildCommand, KnownResourceCommands.StartCommand, KnownResourceCommands.RestartCommand }
            .ToDictionary(name => name, name => Override(resource, name, _ =>
            {
                called.Add(name);
                return Task.FromResult(CommandResults.Success());
            }));
        var snapshot = member.ProjectSnapshot("service", Raw(KnownResourceStates.Finished, null, 0));

        Assert.Equal(KnownResourceStates.FailedToStart, snapshot.State?.Text);
        Assert.Equal("failed", Property(snapshot, "resource.watch.state"));
        Assert.Equal(recoverable
            ? "Watch infrastructure failed. Use Rebuild to restart the watch group with a fresh watcher."
            : "Watch infrastructure failed. Fix the cause and restart the complete Aspire run.",
            Property(snapshot, "resource.watch.reason"));
        var expectedState = recoverable ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
        Assert.Equal(expectedState, commands[KnownResourceCommands.RebuildCommand].UpdateState(StateContext(snapshot, services)));
        Assert.Equal(expectedState, commands[KnownResourceCommands.StartCommand].UpdateState(StateContext(snapshot, services)));
        Assert.Equal(ResourceCommandState.Disabled, commands[KnownResourceCommands.RestartCommand].UpdateState(StateContext(snapshot, services)));

        var rebuild = await commands[KnownResourceCommands.RebuildCommand].ExecuteCommand(Context("service", services));
        var start = await commands[KnownResourceCommands.StartCommand].ExecuteCommand(Context("service", services));
        var restart = await commands[KnownResourceCommands.RestartCommand].ExecuteCommand(Context("service", services));

        Assert.Equal(recoverable, rebuild.Success);
        Assert.Equal(recoverable, start.Success);
        Assert.False(restart.Success);
        Assert.Equal("Watch group 'dotnet-watch' failed.", restart.Message);
        Assert.Equal(recoverable ? ["rebuild service", "start service"] : [], group.Calls);
        Assert.Empty(called);
    }

    [Fact]
    public async Task RestartRelaunchesTheProjectOnceTheWatcherLaunchedIt()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service");
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var called = false;
        var restart = Override(resource, KnownResourceCommands.RestartCommand, _ =>
        {
            called = true;
            return Task.FromResult(CommandResults.Success());
        });

        var launching = member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(ResourceCommandState.Disabled, restart.UpdateState(StateContext(launching, services)));
        member.Observe(DotnetWatchStatusTypes.ProcessStarted, null, null, "process_started");
        var running = member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(ResourceCommandState.Enabled, restart.UpdateState(StateContext(running, services)));
        group.IsFenced = true;
        Assert.Equal(ResourceCommandState.Disabled, restart.UpdateState(StateContext(running, services)));
        group.IsFenced = false;

        Assert.True((await restart.ExecuteCommand(Context("service-a", services))).Success);
        Assert.Equal(["restart service-a"], group.Calls);
        Assert.False(called);
    }

    [Fact]
    public async Task RestartRejectsCrashedAndStoppedApplications()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service") { SingleInstance = true };
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var stop = Override(resource, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        var restart = Override(resource, KnownResourceCommands.RestartCommand, _ => Task.FromResult(CommandResults.Success()));
        member.Observe(DotnetWatchStatusTypes.ProcessStarted, null, null, "process_started");
        member.Observe(DotnetWatchStatusTypes.ProcessExited, null, 3, "process_exited");

        var crashed = member.ProjectSnapshot("service", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(DotnetWatchCoordinator.CrashedState, crashed.State?.Text);
        Assert.Equal(ResourceCommandState.Disabled, restart.UpdateState(StateContext(crashed, services)));
        var crashedResult = await restart.ExecuteCommand(Context("service", services));
        Assert.False(crashedResult.Success);
        Assert.Equal(DotnetWatchMember.CrashedRestartExplanation, crashedResult.Message);

        Assert.True((await stop.ExecuteCommand(Context("service", services))).Success);
        var stoppedResult = await restart.ExecuteCommand(Context("service", services));
        Assert.False(stoppedResult.Success);
        Assert.Equal("'service' was stopped, and Restart only relaunches running instances. " +
            "Use Start, which restarts its watch group with a fresh build.", stoppedResult.Message);
        Assert.Empty(group.Calls);
    }

    [Fact]
    public async Task StartWhileTheWatchGroupChangesIsRejected()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service") { IsFenced = true };
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var called = false;
        var start = Override(resource, KnownResourceCommands.StartCommand, _ =>
        {
            called = true;
            return Task.FromResult(CommandResults.Success());
        });
        var snapshot = member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Finished, null, 0));

        Assert.Equal(ResourceCommandState.Disabled, start.UpdateState(StateContext(snapshot, services)));
        var result = await start.ExecuteCommand(Context("service-a", services));

        Assert.False(result.Success);
        Assert.Equal("Watch group 'dotnet-watch' is changing, and 'service-a' starts when it's done. " +
            "Use Rebuild to restart the group with a fresh build.", result.Message);
        Assert.False(called);
        Assert.Empty(group.Calls);
    }

    [Fact]
    public async Task RequestedStartIsProjectedAsWaitingUntilTheGroupStartsIt()
    {
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("service-a", "a", 0)]));
        var member = new DotnetWatchMember(resource, new FakeWatchGroup("service"));
        member.Configure();
        var stop = Override(resource, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        await notifications.PublishUpdateAsync(resource, "service-a", _ => Raw(KnownResourceStates.Running, 1234, null));
        Assert.True((await stop.ExecuteCommand(Context("service-a", services))).Success);
        await notifications.PublishUpdateAsync(resource, "service-a", _ => Raw(KnownResourceStates.Finished, null, 0));
        Assert.Equal(KnownResourceStates.Exited, CurrentState(notifications, "service-a"));
        Assert.Empty(member.GetIntendedInstances(notifications));

        member.RequestStart("service-a");
        await member.PublishAsync(notifications);
        Assert.Equal(KnownResourceStates.Waiting, CurrentState(notifications, "service-a"));
        Assert.Equal(["service-a"], member.GetIntendedInstances(notifications));

        member.RevertRequestedStart("service-a");
        await member.PublishAsync(notifications);
        Assert.Equal(KnownResourceStates.Exited, CurrentState(notifications, "service-a"));
        Assert.True(member.IsManuallyStopped("service-a"));
    }

    [Fact]
    public async Task IntendedInstancesExcludeStoppedAndNeverLaunchedInstances()
    {
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var member = new DotnetWatchMember(resource, new FakeWatchGroup("service"));
        member.Configure();
        var stop = Override(resource, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        await notifications.PublishUpdateAsync(resource, "service-a", _ => new CustomResourceSnapshot
        {
            ResourceType = "Executable",
            State = KnownResourceStates.Waiting,
            Properties = []
        });
        await notifications.PublishUpdateAsync(resource, "service-b", _ => Raw(KnownResourceStates.Running, 1234, null));
        await notifications.PublishUpdateAsync(resource, "service-c", _ => Raw(KnownResourceStates.Finished, null, 1));
        await notifications.PublishUpdateAsync(resource, "service-d", _ => Raw(KnownResourceStates.Running, 5678, null));
        Assert.True((await stop.ExecuteCommand(Context("service-d", services))).Success);

        Assert.Equal(["service-b", "service-c"], member.GetIntendedInstances(notifications).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACanceledStartShowsNotStartedUntilTheNextStartAndKeepsAnEarlierLaunch()
    {
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        var resource = new DotnetProjectResource("service", ".");
        var member = new DotnetWatchMember(resource, new FakeWatchGroup("service"));
        member.Configure();
        await notifications.PublishUpdateAsync(resource, "service-a", _ => Raw(null, null, null));
        await notifications.PublishUpdateAsync(resource, "service-a", snapshot => snapshot with { State = KnownResourceStates.Starting });
        Assert.True(member.IsStartPending("service-a"));

        // Aspire publishes NotStarted when a start is canceled before DCP launched the proxy. Later updates of the
        // instance, such as the group's own, keep showing it.
        await notifications.PublishUpdateAsync(resource, "service-a", snapshot => snapshot with { State = KnownResourceStates.NotStarted });
        await member.PublishAsync(notifications);

        Assert.False(member.IsStartPending("service-a"));
        Assert.False(member.WaitedForDependencies("service-a"));
        Assert.True(notifications.TryGetCurrentState("service-a", out var canceled));
        Assert.Equal(KnownResourceStates.NotStarted, canceled.Snapshot.State?.Text);

        await notifications.PublishUpdateAsync(resource, "service-a", snapshot => snapshot with { State = KnownResourceStates.Starting });

        Assert.True(member.IsStartPending("service-a"));
        Assert.True(notifications.TryGetCurrentState("service-a", out var restarting));
        Assert.Null(restarting.Snapshot.State);

        // DCP launched this proxy, which failed before it ran, so its dependency waits were satisfied. A later start
        // that was canceled doesn't change that: a relaunch of it mustn't wait for them again.
        await notifications.PublishUpdateAsync(resource, "service-b", _ => Raw(KnownResourceStates.FailedToStart, null, null));
        await notifications.PublishUpdateAsync(resource, "service-b", snapshot => snapshot with { State = KnownResourceStates.NotStarted });

        Assert.True(member.WaitedForDependencies("service-b"));
    }

    [Fact]
    public async Task StartOfAnInstanceThatLaunchedUsesAFreshWatcherUnlessItIsRunning()
    {
        // Aspire's Start would give an instance that launched in this run a new proxy connection to the running watcher,
        // which isn't safe. The dashboard hides Start for a running instance, but the CLI and MCP don't check command
        // state. A running instance is left as it is; one whose application crashed starts with a fresh watcher.
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service") { SingleInstance = true };
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var starts = new List<string>();
        var start = Override(resource, KnownResourceCommands.StartCommand, context =>
        {
            starts.Add(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Running, 1234, null));
        member.Observe(DotnetWatchStatusTypes.ProcessStarted, success: null, exitCode: null, "process_started");

        var running = await start.ExecuteCommand(Context("service-a", services));
        Assert.True(running.Success);
        Assert.Equal("'service-a' is already running. Use Restart to relaunch it, or Rebuild to restart watch group 'dotnet-watch' with a fresh build.",
            running.Message);
        Assert.Empty(group.Calls);

        member.Observe(DotnetWatchStatusTypes.ProcessExited, success: null, exitCode: 3, "process_exited");
        Assert.True((await start.ExecuteCommand(Context("service-a", services))).Success);
        Assert.Equal(["start service-a"], group.Calls);
        Assert.Empty(starts);
    }

    [Fact]
    public async Task StartOfAnInstanceWhoseProxyExitedUsesAFreshWatcherAndANewInstanceStartsAsAspireDoes()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service");
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var starts = new List<string>();
        var start = Override(resource, KnownResourceCommands.StartCommand, context =>
        {
            starts.Add(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        // The proxy exited although neither the user nor the coordinator stopped it.
        member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Running, 1234, null));
        member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Finished, null, 0));

        Assert.True((await start.ExecuteCommand(Context("service-a", services))).Success);
        Assert.True((await start.ExecuteCommand(Context("service-b", services))).Success);

        Assert.Equal(["start service-a"], group.Calls);
        Assert.Equal(["service-b"], starts);
    }

    [Fact]
    public async Task StartOfAnInstanceThatAFreshWatcherIsAboutToStartDoesNotRestartTheGroupAgain()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var group = new FakeWatchGroup("service");
        var member = new DotnetWatchMember(resource, group);
        member.Configure();
        var start = Override(resource, KnownResourceCommands.StartCommand, _ => Task.FromResult(CommandResults.Success()));
        member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Running, 1234, null));
        member.ProjectSnapshot("service-a", Raw(KnownResourceStates.Exited, null, 0));
        // A Start of the stopped instance requested it from the group's fresh watcher, which hasn't started it yet.
        member.RequestStart("service-a");

        var again = await start.ExecuteCommand(Context("service-a", services));

        Assert.True(again.Success);
        Assert.Equal("'service-a' is already starting. Use Restart to relaunch it, or Rebuild to restart watch group 'dotnet-watch' with a fresh build.",
            again.Message);
        Assert.Empty(group.Calls);
    }

    [Fact]
    public async Task AStartDuringAnotherStartCommandsFirstStartLeavesTheStartToItUnlessItWaits()
    {
        // Aspire's Start would restart an instance whose first start is in progress, replacing the proxy that the first
        // start creates with a new connection to the running watcher. Another Start leaves the start to the first one,
        // but starts it at once while it waits for the instance's dependencies, as Aspire's Start does.
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var services = new ServiceCollection().AddSingleton(notifications).BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var member = new DotnetWatchMember(resource, new FakeWatchGroup("service"));
        member.Configure();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new List<string>();
        var start = Override(resource, KnownResourceCommands.StartCommand, async context =>
        {
            starts.Add(context.ResourceName);
            await notifications.PublishUpdateAsync(resource, context.ResourceName, snapshot => snapshot with { State = KnownResourceStates.Starting });
            started.SetResult();
            await release.Task;
            return CommandResults.Success();
        });
        await notifications.PublishUpdateAsync(resource, "service-a", _ => Raw(null, null, null));

        var first = start.ExecuteCommand(Context("service-a", services));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await start.ExecuteCommand(Context("service-a", services)).WaitAsync(TimeSpan.FromSeconds(10));
        await notifications.PublishUpdateAsync(resource, "service-a", snapshot => snapshot with { State = KnownResourceStates.Waiting });
        var third = await start.ExecuteCommand(Context("service-a", services)).WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();

        Assert.True(second.Success);
        Assert.Equal("'service-a' is already starting.", second.Message);
        Assert.True(third.Success);
        Assert.Equal("Successfully started 'service-a'.", third.Message);
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Success);
        Assert.Equal(["service-a"], starts);
    }

    [Fact]
    public async Task StartDuringAspiresFirstStartReleasesItsDependencyWaitOrLeavesTheStartToIt()
    {
        // Aspire's own first start of the instance, for example at startup, is in progress. A Start that finds it waiting
        // for its dependencies starts it at once, as Aspire's Start does; past that wait, the start continues on its own.
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var services = new ServiceCollection().AddSingleton(notifications).BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var member = new DotnetWatchMember(resource, new FakeWatchGroup("service"));
        member.Configure();
        var starts = new List<string>();
        var start = Override(resource, KnownResourceCommands.StartCommand, context =>
        {
            starts.Add(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        await notifications.PublishUpdateAsync(resource, "service-a", _ => Raw(null, null, null));
        await notifications.PublishUpdateAsync(resource, "service-a", snapshot => snapshot with { State = KnownResourceStates.Starting });
        await notifications.PublishUpdateAsync(resource, "service-a", snapshot => snapshot with { State = KnownResourceStates.Waiting });

        var released = await start.ExecuteCommand(Context("service-a", services));

        Assert.True(released.Success);
        Assert.Equal("Successfully started 'service-a'.", released.Message);
        Assert.True(notifications.TryGetCurrentState("service-a", out var current));
        Assert.Null(current.Snapshot.State);

        var again = await start.ExecuteCommand(Context("service-a", services));

        Assert.True(again.Success);
        Assert.Equal("'service-a' is already starting.", again.Message);
        Assert.Empty(starts);
    }

    [Fact]
    public void CommandTextsNameTheWatchGroupAndTheServicesItRestarts()
    {
        var resource = new DotnetProjectResource("api", ".");
        new DotnetWatchMember(resource, new FakeWatchGroup("api", "worker")).Configure();
        var single = new DotnetProjectResource("solo", ".");
        new DotnetWatchMember(single, new FakeWatchGroup("solo")).Configure();
        static Task<ExecuteCommandResult> Execute(ExecuteCommandContext _) => Task.FromResult(CommandResults.Success());

        var rebuild = Override(resource, KnownResourceCommands.RebuildCommand, Execute);
        Assert.Equal("Replace the watcher of watch group 'dotnet-watch' with a fresh one that builds from source, and restart the group's services. " +
            "Use after a crash, a change that hot reload can't apply, or a missed change, or when Restart doesn't help. This also restarts 'worker'.",
            rebuild.DisplayDescription);
        Assert.Equal("Restart watch group 'dotnet-watch' with a fresh build? This also restarts 'worker'. " +
            "If the code doesn't build, the group's services stay down until the errors are fixed and saved.", rebuild.ConfirmationMessage);
        Assert.Equal("Restart watch group 'dotnet-watch' with a fresh build? " +
            "If the code doesn't build, the group's services stay down until the errors are fixed and saved.",
            Override(single, KnownResourceCommands.RebuildCommand, Execute).ConfirmationMessage);

        var start = Override(resource, KnownResourceCommands.StartCommand, Execute);
        Assert.Equal("Start the resource. A watched service that stopped, crashed or failed after it ran, or whose watch group failed, " +
            "starts with a fresh build of watch group 'dotnet-watch', which also restarts 'worker'. A running service is left as it is.",
            start.DisplayDescription);
        Assert.Equal("Start 'api'? A watched service that stopped, crashed or failed after it ran, or whose watch group failed, " +
            "starts with a fresh build of watch group 'dotnet-watch', which also restarts 'worker'. " +
            "If the code doesn't build, the group's services stay down until the errors are fixed and saved.", start.ConfirmationMessage);

        var restart = Override(resource, KnownResourceCommands.RestartCommand, Execute);
        Assert.Equal("Restart every running instance of this project without rebuilding; hot reload changes are reapplied. " +
            "Use when a change was applied but had no effect. Rebuild starts a fresh build instead.", restart.DisplayDescription);
        Assert.Null(restart.ConfirmationMessage);
        Assert.Null(Override(resource, KnownResourceCommands.StopCommand, Execute).ConfirmationMessage);
    }

    [Fact]
    public void CommandClonePreservesMetadataAndValidation()
    {
        var arguments = Array.Empty<InteractionInput>();
        Func<InputsDialogValidationContext, Task> validation = _ => Task.CompletedTask;
        var original = new ResourceCommandAnnotation("start", "Start", _ => ResourceCommandState.Enabled,
            _ => Task.FromResult(CommandResults.Success()), "Description", arguments, "Confirm", "Play",
            IconVariant.Filled, true, ResourceCommandVisibility.Api, validation);
        var clone = DotnetWatchMember.CloneCommand(original, _ => ResourceCommandState.Disabled, original.ExecuteCommand);
        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.DisplayName, clone.DisplayName);
        Assert.Equal(original.DisplayDescription, clone.DisplayDescription);
        Assert.Equal(original.ConfirmationMessage, clone.ConfirmationMessage);
        Assert.Equal(original.IconName, clone.IconName);
        Assert.Equal(original.IconVariant, clone.IconVariant);
        Assert.Equal(original.IsHighlighted, clone.IsHighlighted);
        Assert.Equal(original.Visibility, clone.Visibility);
        Assert.Same(arguments, clone.Arguments);
        Assert.Same(validation, clone.ValidateArguments);
        Assert.Same(original.ExecuteCommand, clone.ExecuteCommand);
    }

    private static DotnetWatchMember Create(DotnetProjectResource resource, bool unambiguous) =>
        new(resource, new FakeWatchGroup(resource.Name) { SingleInstance = unambiguous });

    private static ResourceCommandAnnotation Override(
        IResource resource, string name, Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute)
    {
        var original = new ResourceCommandAnnotation(name, name, _ => ResourceCommandState.Enabled,
            execute, null, arguments: null, null, null, null, false);
        return resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Last(annotation => annotation.CommandName == name).Factory(original);
    }

    private static UpdateCommandStateContext StateContext(CustomResourceSnapshot snapshot, IServiceProvider services) => new()
    {
        ResourceSnapshot = snapshot,
        Services = services
    };

    private static string? CurrentState(ResourceNotificationService notifications, string id)
    {
        Assert.True(notifications.TryGetCurrentState(id, out var current));
        return current.Snapshot.State?.Text;
    }

    private static ExecuteCommandContext Context(string id, IServiceProvider services) => new()
    {
        ResourceName = id,
        Services = services,
        CancellationToken = CancellationToken.None,
        Logger = NullLogger.Instance,
        Arguments = new([])
    };

    private static CustomResourceSnapshot Raw(string? state, int? pid, int? exit) => new()
    {
        ResourceType = "Executable",
        State = state,
        ExitCode = exit,
        Properties = [new("executable.pid", pid)]
    };

    private static object? Property(CustomResourceSnapshot snapshot, string name) =>
        snapshot.Properties.Single(property => property.Name == name).Value;

    /// <summary>Records the group operations that the member's commands request.</summary>
    private sealed class FakeWatchGroup(params string[] resourceNames) : IDotnetWatchGroup
    {
        public string Name => "dotnet-watch";
        public bool IsReady { get; set; } = true;
        public bool IsFenced { get; set; }
        public DotnetWatchGroupFailure? Failure { get; set; }
        public IReadOnlyList<string> ResourceNames { get; } = resourceNames;
        public bool SingleInstance { get; set; }
        public List<string> Calls { get; } = [];

        public bool IsSingleInstance(DotnetProjectResource resource) => SingleInstance;

        public Task<ExecuteCommandResult> RestartProjectAsync(DotnetProjectResource resource, ExecuteCommandContext context)
        {
            Calls.Add($"restart {context.ResourceName}");
            return Task.FromResult(CommandResults.Success());
        }

        public Task<ExecuteCommandResult> ReloadAsync(DotnetProjectResource resource, string instanceId, bool start, ExecuteCommandContext context)
        {
            Calls.Add($"{(start ? "start" : "rebuild")} {instanceId}");
            return Task.FromResult(CommandResults.Success());
        }

        public Task<ExecuteCommandResult> StopAsync(
            DotnetProjectResource resource, ResourceCommandAnnotation original, ExecuteCommandContext context) =>
            original.ExecuteCommand(context);
    }
}
