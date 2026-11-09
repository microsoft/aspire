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
        var member = Create(resource, unambiguous: false);
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

        Assert.True((await stop.ExecuteCommand(Context("service-a", services))).Success);
        member.BeginRefresh();
        var blocked = await start.ExecuteCommand(Context("service-a", services));
        Assert.False(blocked.Success);
        Assert.Equal(DotnetWatchMember.RestartRunExplanation, blocked.Message);
        Assert.True((await start.ExecuteCommand(Context("service-b", services))).Success);
        Assert.Equal(["service-a"], stops);
        Assert.Equal(["service-b"], starts);
        Assert.True(member.IsManuallyStopped("service-a"));
        Assert.False(member.IsManuallyStopped("service-b"));
    }

    [Theory]
    [InlineData("rebuild", DotnetWatchMember.RebuildExplanation)]
    [InlineData("restart", DotnetWatchMember.AmbiguousRestartExplanation)]
    public async Task RestrictedCommandsRejectActualExecution(string name, string explanation)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var resource = new DotnetProjectResource("service", ".");
        var member = Create(resource, unambiguous: false);
        member.Configure();
        member.Observe(DotnetWatchStatusTypes.ProcessStarted, null, null, "process_started");
        var called = false;
        var command = Override(resource, name, _ =>
        {
            called = true;
            return Task.FromResult(CommandResults.Success());
        });
        var snapshot = member.ProjectSnapshot("service", Raw(KnownResourceStates.Running, 1234, null));
        Assert.Equal(ResourceCommandState.Disabled, command.UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = snapshot,
            Services = services
        }));
        var result = await command.ExecuteCommand(Context("service", services));
        Assert.False(result.Success);
        Assert.Equal(explanation, result.Message);
        Assert.False(called);
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
        new(resource, () => true, () => false, () => null, () => unambiguous,
            _ => Task.FromResult(CommandResults.Success()), (original, context) => original.ExecuteCommand(context));

    private static ResourceCommandAnnotation Override(
        IResource resource, string name, Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute)
    {
        var original = new ResourceCommandAnnotation(name, name, _ => ResourceCommandState.Enabled,
            execute, null, arguments: null, null, null, null, false);
        return resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Last(annotation => annotation.CommandName == name).Factory(original);
    }

    private static ExecuteCommandContext Context(string id, IServiceProvider services) => new()
    {
        ResourceName = id,
        Services = services,
        CancellationToken = CancellationToken.None,
        Logger = NullLogger.Instance,
        Arguments = new([])
    };

    private static CustomResourceSnapshot Raw(string state, int? pid, int? exit) => new()
    {
        ResourceType = "Executable",
        State = state,
        ExitCode = exit,
        Properties = [new("executable.pid", pid)]
    };

    private static object? Property(CustomResourceSnapshot snapshot, string name) =>
        snapshot.Properties.Single(property => property.Name == name).Value;
}
