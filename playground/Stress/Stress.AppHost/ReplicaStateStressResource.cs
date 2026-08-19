// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Microsoft.Extensions.DependencyInjection;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

static class ReplicaStateStressResourceExtensions
{
    [AspireExportIgnore(Reason = "Stress playground helper; not part of the supported ATS surface.")]
    public static IResourceBuilder<ReplicaStateStressResource> AddReplicaStateStressResource(this IDistributedApplicationBuilder builder, string name)
    {
        var scenario = new ReplicaStateStressScenario();
        var resource = new ReplicaStateStressResource(name);
        var controls = new ReplicaStateStressControlsResource($"{name}-controls", resource);

        var resourceBuilder = builder.AddResource(resource)
            .WithInitialState(ReplicaStateStressScenario.CreateParentInitialSnapshot())
            .ExcludeFromManifest()
            .OnInitializeResource((r, @event, cancellationToken) => scenario.InitializeAsync(r, @event.Notifications, cancellationToken));

        builder.AddResource(controls)
            .WithInitialState(ReplicaStateStressScenario.CreateControlsInitialSnapshot())
            .WithCommand("reset", "reset", context => scenario.ResetAsync(resource, controls, context))
            .WithCommand("replica-1-running", "replica-1-running", context => scenario.Replica1RunningAsync(resource, controls, context))
            .WithCommand("replica-2-running-unhealthy", "replica-2-running-unhealthy", context => scenario.Replica2RunningUnhealthyAsync(resource, controls, context))
            .WithCommand("running-wins", "running-wins", context => scenario.RunningWinsAsync(resource, controls, context))
            .WithCommand("replica-1-exited", "replica-1-exited", context => scenario.Replica1ExitedAsync(resource, controls, context))
            .WithCommand("replica-1-exit-code-updated", "replica-1-exit-code-updated", context => scenario.Replica1ExitCodeUpdatedAsync(resource, context))
            .WithCommand("ordinary-child-running", "ordinary-child-running", context => scenario.OrdinaryChildRunningAsync(resource, controls, context))
            .ExcludeFromManifest();

        return resourceBuilder;
    }
}

sealed class ReplicaStateStressResource(string name) : Resource(name)
{
}

sealed class ReplicaStateStressControlsResource(string name, IResource parent) : Resource(name), IResourceWithParent
{
    public IResource Parent { get; } = parent;
}

sealed class ReplicaStateStressScenario
{
    private const string Source = "Stress playground";
    private const string HealthCheckName = "replica-state";
    private static readonly ResourceStateSnapshot s_scaledToZeroState = new("Scaled to zero", KnownResourceStateStyles.Info);
    private static readonly ResourceStateSnapshot s_runningState = new(KnownResourceStates.Running, KnownResourceStateStyles.Success);
    private static readonly ResourceStateSnapshot s_startingState = new(KnownResourceStates.Starting, KnownResourceStateStyles.Info);
    private static readonly ResourceStateSnapshot s_exitedState = new(KnownResourceStates.Exited, KnownResourceStateStyles.Error);
    private static readonly DateTime s_creationTimestamp = new(2026, 8, 17, 20, 23, 36, DateTimeKind.Utc);
    private static readonly DateTime s_replica1RunningStartTimestamp = new(2026, 8, 17, 20, 24, 0, DateTimeKind.Utc);
    private static readonly DateTime s_sharedReplicaRunningStartTimestamp = new(2026, 8, 17, 20, 24, 30, DateTimeKind.Utc);
    private static readonly DateTime s_runningWinsStartTimestamp = new(2026, 8, 17, 20, 25, 0, DateTimeKind.Utc);
    private static readonly DateTime s_replica1ExitedStartTimestamp = new(2026, 8, 17, 20, 25, 30, DateTimeKind.Utc);
    private static readonly DateTime s_replica1ExitedStopTimestamp = new(2026, 8, 17, 20, 25, 45, DateTimeKind.Utc);
    private static readonly DateTime s_replica1ExitCodeUpdatedStopTimestamp = new(2026, 8, 17, 20, 25, 55, DateTimeKind.Utc);
    private static readonly DateTime s_controlsRunningStartTimestamp = new(2026, 8, 17, 20, 26, 0, DateTimeKind.Utc);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static CustomResourceSnapshot CreateParentInitialSnapshot() => new()
    {
        ResourceType = "Replica state stress",
        CreationTimeStamp = s_creationTimestamp,
        State = s_scaledToZeroState,
        Properties = CreateSourceProperties()
    };

    public static CustomResourceSnapshot CreateControlsInitialSnapshot() => new()
    {
        ResourceType = "Replica state controls",
        CreationTimeStamp = s_creationTimestamp,
        State = KnownResourceStates.Active,
        Properties = CreateSourceProperties()
    };

    public Task InitializeAsync(ReplicaStateStressResource resource, ResourceNotificationService notifications, CancellationToken cancellationToken) =>
        ExecuteAsync(cancellationToken, () => ResetReplicasAsync(resource, notifications));

    public Task<ExecuteCommandResult> ResetAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            await ResetScenarioAsync(resource, controls, context.Services.GetRequiredService<ResourceNotificationService>()).ConfigureAwait(false);
            return CommandResults.Success();
        });

    public Task<ExecuteCommandResult> Replica1RunningAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

            await ResetScenarioAsync(resource, controls, notifications).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 1), snapshot =>
                snapshot.WithHealthReports(CreateHealthReports(HealthStatus.Healthy)) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = s_runningState,
                    StartTimeStamp = s_replica1RunningStartTimestamp,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);

            return CommandResults.Success();
        });

    public Task<ExecuteCommandResult> Replica2RunningUnhealthyAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

            await ResetScenarioAsync(resource, controls, notifications).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 1), snapshot =>
                snapshot.WithHealthReports(CreateHealthReports(HealthStatus.Healthy)) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = s_runningState,
                    StartTimeStamp = s_sharedReplicaRunningStartTimestamp,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 2), snapshot =>
                snapshot.WithHealthReports(CreateHealthReports(HealthStatus.Unhealthy)) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = s_runningState,
                    StartTimeStamp = s_sharedReplicaRunningStartTimestamp,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);

            return CommandResults.Success();
        });

    public Task<ExecuteCommandResult> RunningWinsAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

            await ResetScenarioAsync(resource, controls, notifications).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 1), snapshot =>
                snapshot.WithHealthReports([]) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = s_startingState,
                    StartTimeStamp = s_runningWinsStartTimestamp,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 2), snapshot =>
                snapshot.WithHealthReports(CreateHealthReports(HealthStatus.Healthy)) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = s_runningState,
                    StartTimeStamp = s_runningWinsStartTimestamp,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);

            return CommandResults.Success();
        });

    public Task<ExecuteCommandResult> Replica1ExitedAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

            await ResetScenarioAsync(resource, controls, notifications).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 1), snapshot =>
                snapshot.WithHealthReports([]) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = s_exitedState,
                    StartTimeStamp = s_replica1ExitedStartTimestamp,
                    StopTimeStamp = s_replica1ExitedStopTimestamp,
                    ExitCode = 137
                }).ConfigureAwait(false);
            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 2), snapshot =>
                snapshot.WithHealthReports([]) with
                {
                    Properties = CreateReplicaProperties(resource.Name),
                    State = null,
                    StartTimeStamp = null,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);

            return CommandResults.Success();
        });

    public Task<ExecuteCommandResult> Replica1ExitCodeUpdatedAsync(ReplicaStateStressResource resource, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

            await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 1), snapshot => snapshot with
            {
                StopTimeStamp = s_replica1ExitCodeUpdatedStopTimestamp,
                ExitCode = 143
            }).ConfigureAwait(false);

            return CommandResults.Success();
        });

    public Task<ExecuteCommandResult> OrdinaryChildRunningAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ExecuteCommandContext context) =>
        ExecuteCommandAsync(context.CancellationToken, async () =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();

            await ResetScenarioAsync(resource, controls, notifications).ConfigureAwait(false);
            await notifications.PublishUpdateAsync(controls, snapshot =>
                snapshot.WithHealthReports([]) with
                {
                    State = s_runningState,
                    StartTimeStamp = s_controlsRunningStartTimestamp,
                    StopTimeStamp = null,
                    ExitCode = null
                }).ConfigureAwait(false);

            return CommandResults.Success();
        });

    private Task ExecuteAsync(CancellationToken cancellationToken, Func<Task> action) =>
        ExecuteAsync<object?>(cancellationToken, async () =>
        {
            await action().ConfigureAwait(false);
            return null;
        });

    private Task<ExecuteCommandResult> ExecuteCommandAsync(CancellationToken cancellationToken, Func<Task<ExecuteCommandResult>> action) =>
        ExecuteAsync(cancellationToken, action);

    private async Task<T> ExecuteAsync<T>(CancellationToken cancellationToken, Func<Task<T>> action)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task ResetScenarioAsync(ReplicaStateStressResource resource, ReplicaStateStressControlsResource controls, ResourceNotificationService notifications)
    {
        await ResetReplicasAsync(resource, notifications).ConfigureAwait(false);
        await notifications.PublishUpdateAsync(controls, snapshot =>
            snapshot.WithHealthReports([]) with
            {
                State = KnownResourceStates.Active,
                StartTimeStamp = null,
                StopTimeStamp = null,
                ExitCode = null
            }).ConfigureAwait(false);
    }

    private static async Task ResetReplicasAsync(ReplicaStateStressResource resource, ResourceNotificationService notifications)
    {
        await PublishParentAsync(notifications, resource, snapshot =>
            snapshot.WithHealthReports([]) with
            {
                Properties = CreateSourceProperties(),
                State = s_scaledToZeroState,
                StartTimeStamp = null,
                StopTimeStamp = null,
                ExitCode = null
            }).ConfigureAwait(false);
        await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 1), snapshot =>
            snapshot.WithHealthReports([]) with
            {
                Properties = CreateReplicaProperties(resource.Name),
                State = null,
                StartTimeStamp = null,
                StopTimeStamp = null,
                ExitCode = null
            }).ConfigureAwait(false);
        await PublishReplicaAsync(notifications, resource, GetReplicaResourceId(resource, 2), snapshot =>
            snapshot.WithHealthReports([]) with
            {
                Properties = CreateReplicaProperties(resource.Name),
                State = null,
                StartTimeStamp = null,
                StopTimeStamp = null,
                ExitCode = null
            }).ConfigureAwait(false);
    }

    private static Task PublishParentAsync(
        ResourceNotificationService notifications,
        ReplicaStateStressResource resource,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> stateFactory) =>
        notifications.PublishUpdateAsync(resource, resource.Name, stateFactory);

    private static Task PublishReplicaAsync(
        ResourceNotificationService notifications,
        ReplicaStateStressResource resource,
        string resourceId,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> stateFactory)
    {
        // Publish multiple resource IDs from the same model resource so the dashboard keeps the
        // parent row and both replica rows on one display name while still treating the replicas
        // as distinct nested children.
        return notifications.PublishUpdateAsync(resource, resourceId, stateFactory);
    }

    private static string GetReplicaResourceId(ReplicaStateStressResource resource, int replicaIndex) =>
        $"{resource.Name}-replica-{replicaIndex}";

    private static ImmutableArray<ResourcePropertySnapshot> CreateSourceProperties() =>
    [
        new(CustomResourceKnownProperties.Source, Source)
    ];

    private static ImmutableArray<ResourcePropertySnapshot> CreateReplicaProperties(string parentName) =>
    [
        new(CustomResourceKnownProperties.Source, Source),
        new(KnownProperties.Resource.ParentName, parentName)
    ];

    private static ImmutableArray<HealthReportSnapshot> CreateHealthReports(HealthStatus status) =>
    [
        new(HealthCheckName, status, null, null)
    ];
}
