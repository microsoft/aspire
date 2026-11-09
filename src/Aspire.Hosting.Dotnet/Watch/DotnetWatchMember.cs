// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // Watch policy uses experimental core hooks.

using System.Collections.Concurrent;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// Keeps application observations and per-instance user intent separate from DCP proxy transport.
/// </summary>
internal sealed class DotnetWatchMember(
    DotnetProjectResource resource,
    Func<bool> ready,
    Func<bool> fenced,
    Func<string?> failure,
    Func<bool> unambiguous,
    Func<ExecuteCommandContext, Task<ExecuteCommandResult>> restart,
    Func<ResourceCommandAnnotation, ExecuteCommandContext, Task<ExecuteCommandResult>> stop)
{
    internal const string RebuildExplanation =
        "Source edits own automatic builds for this watched resource. Restart the watch run for a fresh build.";
    internal const string RestartRunExplanation =
        "A manually stopped watched instance cannot be started safely with this protocol. Restart the watch run.";
    internal const string AmbiguousRestartExplanation =
        "The watcher restarts a whole project, not one replica or duplicate launch. Restart the watch run instead.";
    internal const string InstanceProperty = "dotnet-watch.instance";
    internal const string InternalUpdateProperty = "dotnet-watch.internal-update";
    internal const string TransportStateProperty = "dotnet-watch.transport.state";
    internal const string TransportPidProperty = "dotnet-watch.transport.pid";
    internal const string TransportExitProperty = "dotnet-watch.transport.exit-code";

    private readonly ConcurrentDictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private ObservedState _observed = new(KnownResourceStates.Starting, null, "initializing", 0, null);
    private ResourceCommandAnnotation? _start;
    private ResourceCommandAnnotation? _stop;

    internal void Configure()
    {
        // Compose any existing producer deliberately. This projection is outermost so another
        // projection cannot accidentally restore a proxy PID as the application identity.
        var previous = resource.Annotations.OfType<ResourceSnapshotProjectionAnnotation>().LastOrDefault();
        resource.Annotations.Add(new ResourceSnapshotProjectionAnnotation((id, snapshot) =>
        {
            var transport = CaptureTransport(id, snapshot, out var modelPublication);
            var projected = previous is null ? snapshot : previous.Projection(id, snapshot);
            return ProjectSnapshot(id, projected, transport, modelPublication);
        }));

        foreach (var name in new[]
        {
            KnownResourceCommands.StartCommand, KnownResourceCommands.StopCommand,
            KnownResourceCommands.RestartCommand, KnownResourceCommands.RebuildCommand
        })
        {
            resource.Annotations.Add(new ResourceLifecycleCommandOverrideAnnotation(name, original =>
            {
                if (original.Name == KnownResourceCommands.StartCommand)
                {
                    _start = original;
                }
                else if (original.Name == KnownResourceCommands.StopCommand)
                {
                    _stop = original;
                }

                return CloneCommand(original, context => UpdateCommand(original, context),
                    context => ExecuteCommandAsync(original, context));
            }));
        }
    }

    internal static ResourceCommandAnnotation CloneCommand(
        ResourceCommandAnnotation original,
        Func<UpdateCommandStateContext, ResourceCommandState> update,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute) =>
        new(original.Name, original.DisplayName, update, execute, original.DisplayDescription, original.Arguments,
            original.ConfirmationMessage, original.IconName, original.IconVariant, original.IsHighlighted,
            original.Visibility, original.ValidateArguments);

    private ResourceCommandState UpdateCommand(ResourceCommandAnnotation original, UpdateCommandStateContext context)
    {
        var id = context.ResourceSnapshot.Properties.FirstOrDefault(property => property.Name == InstanceProperty)?.Value as string;
        var stopped = id is not null && IsManuallyStopped(id);
        var observed = Volatile.Read(ref _observed);
        return original.Name switch
        {
            var name when name == KnownResourceCommands.RebuildCommand => ResourceCommandState.Disabled,
            var name when name == KnownResourceCommands.StartCommand && (stopped || fenced() || failure() is not null) => ResourceCommandState.Disabled,
            var name when name == KnownResourceCommands.RestartCommand && (stopped || !unambiguous() || !ready() || fenced() ||
                failure() is not null || observed.Generation == 0 || observed.State != KnownResourceStates.Running) => ResourceCommandState.Disabled,
            _ => original.UpdateState(context)
        };
    }

    private async Task<ExecuteCommandResult> ExecuteCommandAsync(ResourceCommandAnnotation original, ExecuteCommandContext context)
    {
        if (original.Name == KnownResourceCommands.RebuildCommand)
        {
            return CommandResults.Failure(RebuildExplanation);
        }

        if (original.Name == KnownResourceCommands.StopCommand)
        {
            // Record intent before DCP publishes Stopping/Exited; internal refresh calls the
            // captured callback directly and therefore never records a user's manual Stop.
            _instances.AddOrUpdate(context.ResourceName,
                _ => new(null, null, null, Stopped: true, CoordinatorStopping: false, CoordinatorStarting: false),
                (_, instance) => instance with { Stopped = true, CoordinatorStopping = false, CoordinatorStarting = false });
            return await stop(original, context).ConfigureAwait(false);
        }

        if (failure() is { } failed)
        {
            return CommandResults.Failure(failed);
        }

        if (IsManuallyStopped(context.ResourceName))
        {
            return CommandResults.Failure(RestartRunExplanation);
        }

        if (fenced())
        {
            return CommandResults.Failure("The watch group is changing or stopping. Wait for it, or restart the watch run.");
        }

        if (original.Name == KnownResourceCommands.RestartCommand)
        {
            if (!unambiguous())
            {
                return CommandResults.Failure(AmbiguousRestartExplanation);
            }

            var observed = Volatile.Read(ref _observed);
            if (!ready() || observed.Generation == 0 || observed.State != KnownResourceStates.Running)
            {
                return CommandResults.Failure("The watcher has no last-observed running application to restart. Save a source change or restart the watch run.");
            }

            return await restart(context).ConfigureAwait(false);
        }

        return await original.ExecuteCommand(context).ConfigureAwait(false);
    }

    internal bool IsManuallyStopped(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.Stopped;

    internal bool IsCoordinatorStopping(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.CoordinatorStopping;

    internal bool IsCoordinatorStarting(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.CoordinatorStarting;

    internal void ObserveTransport(string id, CustomResourceSnapshot snapshot)
    {
        var state = snapshot.Properties.FirstOrDefault(property => property.Name == TransportStateProperty)?.Value as string;
        var pid = snapshot.Properties.FirstOrDefault(property => property.Name == TransportPidProperty)?.Value;
        var exit = snapshot.Properties.FirstOrDefault(property => property.Name == TransportExitProperty)?.Value as int?;
        _instances.AddOrUpdate(id, _ => new(state, pid, exit, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false),
            (_, instance) => instance with
            {
                TransportState = state,
                TransportPid = pid,
                TransportExitCode = exit,
                CoordinatorStarting = instance.CoordinatorStarting && !IsRunningTransport(state)
            });
    }

    internal string[] GetTransportInstances(ResourceNotificationService notifications, bool intendedOnly) =>
        [.. notifications.GetCurrentStates(resource).Where(update =>
            (!intendedOnly || !IsManuallyStopped(update.ResourceId)) &&
            update.Snapshot.Properties.FirstOrDefault(property => property.Name == TransportStateProperty)?.Value is string state &&
            (intendedOnly ? IsRunningTransport(state) : IsActiveTransport(state)))
            .Select(update => update.ResourceId)];

    private static bool IsRunningTransport(string? state) =>
        state == KnownResourceStates.Running || state == KnownResourceStates.Starting;

    private static bool IsActiveTransport(string? state) =>
        IsRunningTransport(state) || state == KnownResourceStates.Stopping;

    internal async Task<ExecuteCommandResult> StopOwnedAsync(string id, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (_stop is null)
        {
            return CommandResults.Failure($"The original Stop callback for '{resource.Name}' is unavailable.");
        }

        _instances.AddOrUpdate(id,
            _ => new(null, null, null, Stopped: false, CoordinatorStopping: true, CoordinatorStarting: false),
            (_, instance) => instance with { CoordinatorStopping = true, CoordinatorStarting = false });
        var result = await _stop.ExecuteCommand(new ExecuteCommandContext
        {
            ResourceName = id,
            Services = services,
            CancellationToken = cancellationToken,
            Logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(resource),
            Arguments = new([])
        }).ConfigureAwait(false);
        if (!result.Success)
        {
            _instances.AddOrUpdate(id,
                _ => new(null, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false),
                (_, instance) => instance with { CoordinatorStopping = false, CoordinatorStarting = false });
        }

        return result;
    }

    internal async Task<ExecuteCommandResult> StartOwnedAsync(string id, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (IsManuallyStopped(id))
        {
            return CommandResults.Failure(RestartRunExplanation);
        }

        if (_start is null)
        {
            return CommandResults.Failure($"The original Start callback for '{resource.Name}' is unavailable.");
        }

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        _instances.AddOrUpdate(id,
            _ => new(null, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: true),
            (_, instance) => instance with { CoordinatorStopping = false, CoordinatorStarting = true });
        try
        {
            // The planned stop is projected as Waiting so dependencies retain their reason. Publish
            // an explicit planned-start transition before invoking Aspire's generic Start callback;
            // otherwise it treats Waiting as an already-pending DCP start and intentionally no-ops.
            await PublishAsync(notifications).ConfigureAwait(false);
            var result = await _start.ExecuteCommand(new ExecuteCommandContext
            {
                ResourceName = id,
                Services = services,
                CancellationToken = cancellationToken,
                Logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(resource),
                Arguments = new([])
            }).ConfigureAwait(false);
            if (!result.Success)
            {
                _instances.AddOrUpdate(id,
                    _ => new(null, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false),
                    (_, instance) => instance with { CoordinatorStarting = false });
                await PublishAsync(notifications).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            _instances.AddOrUpdate(id,
                _ => new(null, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false),
                (_, instance) => instance with { CoordinatorStarting = false });
            await PublishAsync(notifications).ConfigureAwait(false);
            throw;
        }
    }

    internal void Observe(string type, bool? success, int? exitCode, string lastEvent)
    {
        var previous = Volatile.Read(ref _observed);
        var next = type switch
        {
            DotnetWatchStatusTypes.Building => previous with { Build = "building", LastEvent = lastEvent },
            DotnetWatchStatusTypes.BuildComplete => previous with { Build = success == true ? "idle" : "failed", LastEvent = lastEvent },
            DotnetWatchStatusTypes.Restarting => previous with { State = KnownResourceStates.Starting, ExitCode = null, LastEvent = lastEvent },
            DotnetWatchStatusTypes.ProcessStarted => previous with
            {
                State = KnownResourceStates.Running, ExitCode = null, Generation = previous.Generation + 1, LastEvent = lastEvent
            },
            DotnetWatchStatusTypes.ProcessExited when exitCode is not null and not 0 => previous with
            {
                State = DotnetWatchCoordinator.CrashedState, ExitCode = exitCode, LastEvent = lastEvent
            },
            _ => previous with { LastEvent = lastEvent }
        };
        Volatile.Write(ref _observed, next);
    }

    internal void BeginRefresh()
    {
        Volatile.Write(ref _observed, new(KnownResourceStates.Starting, null, "initializing", 0, null));
    }

    internal Task PublishAsync(ResourceNotificationService notifications) =>
        notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                Properties = [.. snapshot.Properties.Where(property => property.Name != InternalUpdateProperty),
                    new(InternalUpdateProperty, true)]
            });

    internal CustomResourceSnapshot ProjectSnapshot(string id, CustomResourceSnapshot snapshot) =>
        ProjectSnapshot(id, snapshot, CaptureTransport(id, snapshot, out var modelPublication), modelPublication);

    internal static CustomResourceSnapshot WithWatchStatus(
        CustomResourceSnapshot snapshot, string state, string? reason, string? logResource) =>
        snapshot with
        {
            Properties =
            [
                .. snapshot.Properties.Where(property => property.Name is not ResourceWatch.StateProperty and
                    not ResourceWatch.ReasonProperty and not ResourceWatch.LogResourceProperty),
                new(ResourceWatch.StateProperty, state),
                new(ResourceWatch.ReasonProperty, reason),
                new(ResourceWatch.LogResourceProperty, logResource)
            ]
        };

    private Instance CaptureTransport(string id, CustomResourceSnapshot snapshot, out bool modelPublication)
    {
        var internalUpdate = snapshot.Properties.Any(property => property.Name == InternalUpdateProperty);
        // Raw DCP executable snapshots include executable.pid, even when it is null.
        // Health/URL publications reuse already-projected properties and are not new
        // process observations. Retain transport separately across those logical updates.
        var raw = snapshot.Properties.Any(property => property.Name == "executable.pid");
        // Aspire/model publications use these states to express startup failure or dependency
        // waiting independently of the proxy transport. Other logical updates (for example
        // endpoint health) reuse the projected snapshot and must not replace transport state.
        var snapshotState = snapshot.State?.Text;
        modelPublication = !raw && !internalUpdate &&
            (snapshotState == KnownResourceStates.FailedToStart || snapshotState == KnownResourceStates.Waiting);
        var previous = _instances.GetValueOrDefault(id);
        var instance = previous ?? new(snapshot.State?.Text, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false);
        if (raw && !internalUpdate)
        {
            instance = instance with
            {
                TransportState = snapshot.State?.Text,
                TransportPid = snapshot.Properties.First(property => property.Name == "executable.pid").Value,
                TransportExitCode = snapshot.ExitCode,
                CoordinatorStarting = instance.CoordinatorStarting && !IsRunningTransport(snapshot.State?.Text)
            };
        }
        else if (snapshot.Properties.Any(property => property.Name == TransportStateProperty))
        {
            instance = instance with
            {
                TransportState = snapshot.Properties.First(property => property.Name == TransportStateProperty).Value as string,
                TransportPid = snapshot.Properties.FirstOrDefault(property => property.Name == TransportPidProperty)?.Value,
                TransportExitCode = snapshot.Properties.FirstOrDefault(property => property.Name == TransportExitProperty)?.Value as int?
            };
        }

        _instances[id] = instance;
        return instance;
    }

    private CustomResourceSnapshot ProjectSnapshot(
        string id,
        CustomResourceSnapshot snapshot,
        Instance instance,
        bool modelPublication)
    {
        var observed = Volatile.Read(ref _observed);
        var state = instance.TransportState;
        ResourceStateSnapshot? modelState = null;
        var applicationScope = unambiguous();
        var failed = failure();
        if (failed is not null)
        {
            state = KnownResourceStates.FailedToStart;
        }
        else if (instance.Stopped)
        {
            state = state is not null && KnownResourceStates.TerminalStates.Contains(state)
                ? KnownResourceStates.Exited : KnownResourceStates.Stopping;
        }
        else if (instance.CoordinatorStopping)
        {
            state = state is not null && KnownResourceStates.TerminalStates.Contains(state)
                ? KnownResourceStates.Waiting : KnownResourceStates.Stopping;
        }
        else if (instance.CoordinatorStarting)
        {
            state = KnownResourceStates.Starting;
        }
        else if (modelPublication && snapshot.State is not null)
        {
            modelState = snapshot.State;
            state = snapshot.State.Text;
        }
        else if (state is not null && KnownResourceStates.TerminalStates.Contains(state))
        {
            // A successful proxy exit is neither successful application completion nor
            // acknowledgement that the upstream application has finished cleanup.
            state = KnownResourceStates.FailedToStart;
        }
        else if (applicationScope && state == KnownResourceStates.Running)
        {
            state = observed.State;
        }

        var projected = snapshot with
        {
            State = modelState ?? (state is null ? null : new ResourceStateSnapshot(state,
                failed is not null || state == DotnetWatchCoordinator.CrashedState || state == KnownResourceStates.FailedToStart
                    ? KnownResourceStateStyles.Error : null)),
            ExitCode = modelPublication && modelState is not null
                ? snapshot.ExitCode
                : applicationScope && !instance.Stopped && !instance.CoordinatorStopping && failed is null &&
                    !KnownResourceStates.TerminalStates.Contains(instance.TransportState) ? observed.ExitCode : null,
            StartTimeStamp = null,
            StopTimeStamp = null,
            Properties =
            [
                .. snapshot.Properties.Where(property => property.Name != "executable.pid" &&
                    property.Name != InternalUpdateProperty && !property.Name.StartsWith("dotnet-watch.", StringComparison.Ordinal)),
                new(InstanceProperty, id),
                new(TransportStateProperty, instance.TransportState),
                new(TransportPidProperty, instance.TransportPid),
                new(TransportExitProperty, instance.TransportExitCode),
                new("dotnet-watch.enabled", true),
                new("dotnet-watch.scope", applicationScope ? "application" : "project-group"),
                new("dotnet-watch.coverage", failed ?? (applicationScope
                    ? "Last-observed application state; application PID and successful completion are not reported."
                    : "Project/group events are not per-instance readiness, PID, or completion. Instance transport and endpoint health remain separate.")),
                new("dotnet-watch.build", observed.Build),
                new("dotnet-watch.generation", observed.Generation),
                new("dotnet-watch.last-event", observed.LastEvent)
            ]
        };

        // Coverage describes watch participation, not per-instance application health or successful hot reload.
        return WithWatchStatus(projected,
            failed is not null ? ResourceWatch.Failed :
                observed.Build == "failed" ? ResourceWatch.BuildFailed :
                ready() ? ResourceWatch.Watching : ResourceWatch.Pending,
            failed is not null ? "Watch infrastructure failed. Fix the cause and restart the complete Aspire run." :
                observed.Build == "failed" ? "The build failed. Fix the code and save." : null,
            resource.Annotations.OfType<WaitAnnotation>().LastOrDefault(wait => wait.Resource is DotnetWatchServerResource)?.Resource.Name);
    }

    private sealed record Instance(
        string? TransportState,
        object? TransportPid,
        int? TransportExitCode,
        bool Stopped,
        bool CoordinatorStopping,
        bool CoordinatorStarting);
    private sealed record ObservedState(string State, int? ExitCode, string Build, long Generation, string? LastEvent);
}
