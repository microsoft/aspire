// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // Watch policy uses experimental core hooks.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// The state and operations of a watch group (one watcher and the projects it runs) that the lifecycle commands of
/// its watched projects use.
/// </summary>
internal interface IDotnetWatchGroup
{
    /// <summary>The hidden watch server resource. Its console logs hold the watcher's build output.</summary>
    string Name { get; }

    /// <summary>Whether the current watcher finished a successful build.</summary>
    bool IsReady { get; }

    /// <summary>Whether a restart, reload, or shutdown is changing the group.</summary>
    bool IsFenced { get; }

    /// <summary>The failure that stopped the group, or <see langword="null"/>.</summary>
    DotnetWatchGroupFailure? Failure { get; }

    /// <summary>The project resources that the group's watcher runs, in model order.</summary>
    IReadOnlyList<string> ResourceNames { get; }

    /// <summary>
    /// Whether <paramref name="resource"/>'s project has exactly one instance in the group. Only then do the watcher's
    /// project-level events describe a single application.
    /// </summary>
    bool IsSingleInstance(DotnetProjectResource resource);

    /// <summary>Relaunches every running instance of <paramref name="resource"/>'s project without a build.</summary>
    Task<ExecuteCommandResult> RestartProjectAsync(DotnetProjectResource resource, ExecuteCommandContext context);

    /// <summary>
    /// Replaces the group's watcher with a fresh one that builds from source, and restarts the intended instances.
    /// With <paramref name="start"/>, the manually stopped <paramref name="instanceId"/> becomes intended again.
    /// </summary>
    Task<ExecuteCommandResult> ReloadAsync(DotnetProjectResource resource, string instanceId, bool start, ExecuteCommandContext context);

    /// <summary>Stops a watched instance at the user's request.</summary>
    Task<ExecuteCommandResult> StopAsync(DotnetProjectResource resource, ResourceCommandAnnotation original, ExecuteCommandContext context);
}

/// <summary>Why a watch group stopped.</summary>
/// <param name="Summary">User-facing description that names the remedy.</param>
/// <param name="Recoverable">
/// Whether a fresh watcher can recover the group in this run. Ownership or graph changes that need a new partition of
/// the watched projects, and watcher processes whose cleanup wasn't confirmed, are not recoverable.
/// </param>
internal sealed record DotnetWatchGroupFailure(string Summary, bool Recoverable);

/// <summary>
/// Keeps application observations and per-instance user intent separate from DCP proxy transport.
/// </summary>
internal sealed class DotnetWatchMember(DotnetProjectResource resource, IDotnetWatchGroup group)
{
    /// <summary>Why a watched project's hidden rebuilder resource must not run.</summary>
    internal const string WatchedRebuilderExplanation =
        "The watcher builds this watched project. Use the project's Rebuild command, which restarts its watch group with a fresh build.";
    internal const string InstanceProperty = "dotnet-watch.instance";
    internal const string InternalUpdateProperty = "dotnet-watch.internal-update";
    internal const string TransportStateProperty = "dotnet-watch.transport.state";
    internal const string TransportPidProperty = "dotnet-watch.transport.pid";
    internal const string TransportExitProperty = "dotnet-watch.transport.exit-code";

    private readonly ConcurrentDictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    /// <summary>Instances that the coordinator is relaunching after they already ran in this run.</summary>
    private readonly ConcurrentDictionary<string, byte> _relaunching = new(StringComparer.Ordinal);
    /// <summary>First starts of instances that haven't launched in this run, each claimed by the Start command that runs it.</summary>
    private readonly ConcurrentDictionary<string, FirstStart> _firstStarts = new(StringComparer.Ordinal);
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

                // DCP adds the lifecycle commands after preflight has settled the watch groups, so the
                // sibling names in these texts are final.
                var (description, confirmation) = DescribeCommand(original);
                return CloneCommand(original, context => UpdateCommand(original, context),
                    context => ExecuteCommandAsync(original, context), description, confirmation);
            }));
        }

        // A relaunch by the coordinator (after it replaced the watcher, or a Start after a manual Stop) continues an
        // instance whose dependencies were satisfied when it was first launched in this run. Waiting for them again can
        // block forever: a dependency that ran to completion, such as a database migration, doesn't run again, and the
        // coordinator holds its watch group while it starts the instance.
        resource.Annotations.Add(new ResourceDependencyWaitFilterAnnotation(id => !_relaunching.ContainsKey(id)));
    }

    internal static ResourceCommandAnnotation CloneCommand(
        ResourceCommandAnnotation original,
        Func<UpdateCommandStateContext, ResourceCommandState> update,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute) =>
        CloneCommand(original, update, execute, original.DisplayDescription, original.ConfirmationMessage);

    internal static ResourceCommandAnnotation CloneCommand(
        ResourceCommandAnnotation original,
        Func<UpdateCommandStateContext, ResourceCommandState> update,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute,
        string? description,
        string? confirmation) =>
        new(original.Name, original.DisplayName, update, execute, description, original.Arguments,
            confirmation, original.IconName, original.IconVariant, original.IsHighlighted,
            original.Visibility, original.ValidateArguments);

    internal const string CrashedRestartExplanation =
        "The application exited, and Restart only relaunches running applications. Use Rebuild to start it with a fresh build, or save a source change to relaunch it.";

    internal static string FormatNames(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));

    /// <summary>
    /// Descriptions are what agents read (for example from <c>list_resources</c>), so they state each command's scope and
    /// when to use it. Commands that replace the group's watcher confirm that in the dashboard first.
    /// </summary>
    private (string? Description, string? Confirmation) DescribeCommand(ResourceCommandAnnotation original)
    {
        var siblings = group.ResourceNames.Where(name => !string.Equals(name, resource.Name, StringComparison.Ordinal)).ToArray();
        var alsoRestarts = siblings.Length == 0 ? "" : $" This also restarts {FormatNames(siblings)}.";
        const string staysDown = "If the code doesn't build, the group's services stay down until the errors are fixed and saved.";
        if (original.Name == KnownResourceCommands.RebuildCommand)
        {
            return ($"Replace the watcher of watch group '{group.Name}' with a fresh one that builds from source, and restart the group's services. " +
                "Use after a crash, a change that hot reload can't apply, or a missed change, or when Restart doesn't help." + alsoRestarts,
                $"Restart watch group '{group.Name}' with a fresh build?{alsoRestarts} {staysDown}");
        }

        if (original.Name == KnownResourceCommands.StartCommand)
        {
            // The text is fixed when the command is created, while only some starts replace the watcher (a service that
            // stopped, crashed or failed after it ran, or one whose group failed), so it says when that happens.
            var whichAlsoRestarts = siblings.Length == 0 ? "" : $", which also restarts {FormatNames(siblings)}";
            const string whenFresh = "A watched service that stopped, crashed or failed after it ran, or whose watch group failed,";
            return ($"Start the resource. {whenFresh} starts with a fresh build of watch group '{group.Name}'{whichAlsoRestarts}. " +
                "A running service is left as it is.",
                $"Start '{resource.Name}'? {whenFresh} starts with a fresh build of watch group '{group.Name}'{whichAlsoRestarts}. {staysDown}");
        }

        if (original.Name == KnownResourceCommands.RestartCommand)
        {
            return ("Restart every running instance of this project without rebuilding; hot reload changes are reapplied. " +
                "Use when a change was applied but had no effect. Rebuild starts a fresh build instead.",
                original.ConfirmationMessage);
        }

        return (original.DisplayDescription, original.ConfirmationMessage);
    }

    private ResourceCommandState UpdateCommand(ResourceCommandAnnotation original, UpdateCommandStateContext context)
    {
        var id = context.ResourceSnapshot.Properties.FirstOrDefault(property => property.Name == InstanceProperty)?.Value as string;
        var stopped = id is not null && IsManuallyStopped(id);
        var failure = group.Failure;
        if (original.Name == KnownResourceCommands.RebuildCommand)
        {
            // A fresh watcher replaces crashed or stopped applications, failed builds, and recoverable
            // infrastructure failures. Only failures that need a new partition of the projects can't use it.
            return failure is { Recoverable: false } ? ResourceCommandState.Disabled : ResourceCommandState.Enabled;
        }

        if (original.Name == KnownResourceCommands.StartCommand)
        {
            return failure is { Recoverable: false } ? ResourceCommandState.Disabled
                : stopped ? ResourceCommandState.Enabled
                : failure is not null ? original.UpdateState(context)
                : group.IsFenced ? ResourceCommandState.Disabled
                : original.UpdateState(context);
        }

        if (original.Name == KnownResourceCommands.RestartCommand)
        {
            var observed = Volatile.Read(ref _observed);
            return stopped || failure is not null || group.IsFenced || !group.IsReady || observed.Generation == 0 ||
                (group.IsSingleInstance(resource) && observed.State != KnownResourceStates.Running)
                ? ResourceCommandState.Disabled
                : original.UpdateState(context);
        }

        return original.UpdateState(context);
    }

    private async Task<ExecuteCommandResult> ExecuteCommandAsync(ResourceCommandAnnotation original, ExecuteCommandContext context)
    {
        if (original.Name == KnownResourceCommands.StopCommand)
        {
            // An instance that hasn't launched in this run has no proxy that the group's watcher works with, and its
            // start, if any, still waits for the build or its dependencies. Cancel that start without the group's lock.
            // Recording a manual stop instead would make its next Start restart the group, and the restart would then
            // wait for the instance's dependencies while it holds the group.
            if (!WaitedForDependencies(context.ResourceName))
            {
                // A Start command can hold the instance's first start before it hands it to Aspire, for example while the
                // instance's watch group recovers. Cancel it there; Aspire cancels a start that it already has.
                var canceledClaim = _firstStarts.TryGetValue(context.ResourceName, out var claim) && claim.TryCancel();
                var commands = context.Services.GetRequiredService<ResourceCommandService>();
                switch (await commands.TryCancelPendingStartAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false))
                {
                    case ResourceStartCancellationResult.Canceled:
                        return CommandResults.Success($"Stopped '{context.ResourceName}' before it started.");
                    case ResourceStartCancellationResult.NotStarted when canceledClaim:
                        // Like a start that Aspire canceled, the instance shows that it didn't start.
                        await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                            resource, context.ResourceName,
                            snapshot => snapshot with { State = KnownResourceStates.NotStarted, ExitCode = null }).ConfigureAwait(false);
                        return CommandResults.Success($"Stopped '{context.ResourceName}' before it started.");
                    case ResourceStartCancellationResult.NotStarted:
                        return CommandResults.Success($"'{context.ResourceName}' isn't running.");
                }

                // Its start already reached DCP, so its dependencies were satisfied and DCP is launching its proxy: stop
                // it like any launched instance. A later Start then relaunches it without waiting for them again.
                _instances.AddOrUpdate(context.ResourceName,
                    _ => new(null, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false, Launched: true),
                    (_, instance) => instance with { Launched = true });
            }

            // Record intent before DCP publishes Stopping/Exited; internal refresh calls the
            // captured callback directly and therefore never records a user's manual Stop.
            _instances.AddOrUpdate(context.ResourceName,
                _ => new(null, null, null, Stopped: true, CoordinatorStopping: false, CoordinatorStarting: false),
                (_, instance) => instance with { Stopped = true, CoordinatorStopping = false, CoordinatorStarting = false });
            return await group.StopAsync(resource, original, context).ConfigureAwait(false);
        }

        // ResourceCommandService doesn't check command state before executing, so each path guards itself.
        // Rebuild and Restart run once per replica in parallel; the group coalesces those calls.
        var failure = group.Failure;
        if (original.Name == KnownResourceCommands.RebuildCommand)
        {
            return failure is { Recoverable: false }
                ? CommandResults.Failure(failure.Summary)
                : await group.ReloadAsync(resource, context.ResourceName, start: false, context).ConfigureAwait(false);
        }

        if (original.Name == KnownResourceCommands.StartCommand)
        {
            if (failure is { Recoverable: false })
            {
                return CommandResults.Failure(failure.Summary);
            }

            if (!WaitedForDependencies(context.ResourceName))
            {
                return await StartNeverLaunchedAsync(context, failure).ConfigureAwait(false);
            }

            if (IsManuallyStopped(context.ResourceName) || failure is not null)
            {
                // A new connection to the running watcher can't safely replace an application that the watcher
                // relaunched earlier: the watcher keeps relaunch callbacks bound to the closed connection. Starting
                // an instance again therefore uses a fresh watcher for the whole group.
                return await group.ReloadAsync(resource, context.ResourceName, start: true, context).ConfigureAwait(false);
            }

            if (group.IsFenced)
            {
                // The group's restart starts the instances that were running.
                return CommandResults.Failure(
                    $"Watch group '{group.Name}' is changing, and '{context.ResourceName}' starts when it's done. Use Rebuild to restart the group with a fresh build.");
            }

            // Aspire's Start would replace the proxy of an instance that launched in this run, and a new proxy connection
            // to the running watcher isn't safe: the watcher can later relaunch the application for the closed
            // connection, without a proxy, holding the service's port. A fresh watcher starts it instead. The dashboard
            // hides Start for a running instance, but the CLI and MCP don't check command state.
            if (IsDownAfterLaunch(context.ResourceName))
            {
                return await group.ReloadAsync(resource, context.ResourceName, start: true, context).ConfigureAwait(false);
            }

            return CommandResults.Success(
                $"'{context.ResourceName}' is already {(IsProxyRunning(context.ResourceName) ? "running" : "starting")}. " +
                $"Use Restart to relaunch it, or Rebuild to restart watch group '{group.Name}' with a fresh build.");
        }

        if (original.Name == KnownResourceCommands.RestartCommand)
        {
            if (failure is not null)
            {
                return CommandResults.Failure(failure.Summary);
            }

            if (IsManuallyStopped(context.ResourceName))
            {
                return CommandResults.Failure(
                    $"'{context.ResourceName}' was stopped, and Restart only relaunches running instances. Use Start, which restarts its watch group with a fresh build.");
            }

            if (group.IsSingleInstance(resource) && Volatile.Read(ref _observed).State == DotnetWatchCoordinator.CrashedState)
            {
                return CommandResults.Failure(CrashedRestartExplanation);
            }

            return await group.RestartProjectAsync(resource, context).ConfigureAwait(false);
        }

        return await original.ExecuteCommand(context).ConfigureAwait(false);
    }

    internal bool IsManuallyStopped(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.Stopped;

    /// <summary>Whether the user stopped the instance and its proxy exited, so no proxy connection can own a launch of it.</summary>
    internal bool IsStoppedAndDisconnected(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.Stopped && HasNoProxy(instance.TransportState);

    /// <summary>Whether the transport state says that the instance has no running proxy: it exited, or never started.</summary>
    private static bool HasNoProxy(string? transportState) =>
        transportState is not null &&
        (KnownResourceStates.TerminalStates.Contains(transportState) || transportState == KnownResourceStates.NotStarted);

    internal bool IsCoordinatorStopping(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.CoordinatorStopping;

    internal bool IsCoordinatorStarting(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.CoordinatorStarting;

    /// <summary>Whether Aspire began starting the instance, and it hasn't run or failed to start since.</summary>
    internal bool IsStartPending(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.StartPending;

    /// <summary>Whether the instance's proxy ran in this run.</summary>
    internal bool HasLaunched(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.Launched;

    /// <summary>
    /// Whether the instance launched in this run, and its proxy has exited since, or its application crashed. A manually
    /// stopped instance is handled before this is asked. An instance that the coordinator is stopping or starting, for
    /// example to resume it on a fresh watcher, is about to run again, so it isn't down.
    /// </summary>
    private bool IsDownAfterLaunch(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && !instance.CoordinatorStopping && !instance.CoordinatorStarting &&
        (HasNoProxy(instance.TransportState) || TryGetExit(out _));

    private bool IsProxyRunning(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && instance.TransportState == KnownResourceStates.Running &&
        !instance.CoordinatorStopping && !instance.CoordinatorStarting;

    /// <summary>
    /// Whether the instance's proxy stopped although neither the user nor the coordinator stopped it. The intent and the
    /// transport state come from one record, which the projection updates as each snapshot is published.
    /// </summary>
    internal bool HasUnexpectedTransportExit(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) &&
        !instance.Stopped && !instance.CoordinatorStopping && !instance.CoordinatorStarting &&
        instance.TransportState is { } state && KnownResourceStates.TerminalStates.Contains(state);

    /// <summary>
    /// Makes a manually stopped instance intended again before a fresh watcher replaces the group's watcher. Until the
    /// group resumes its instances, the instance is projected as waiting for that planned start.
    /// </summary>
    internal void RequestStart(string id) =>
        _instances.AddOrUpdate(id,
            _ => new(null, null, null, Stopped: false, CoordinatorStopping: true, CoordinatorStarting: false),
            (_, instance) => instance with { Stopped = false, CoordinatorStopping = true, CoordinatorStarting = false });

    /// <summary>Restores the manual stop of a requested start that no fresh watcher will perform.</summary>
    internal void RevertRequestedStart(string id) =>
        _instances.AddOrUpdate(id,
            _ => new(null, null, null, Stopped: true, CoordinatorStopping: false, CoordinatorStarting: false),
            (_, instance) => instance with { Stopped = true, CoordinatorStopping = false, CoordinatorStarting = false });

    /// <summary>
    /// Every current instance that the user hasn't stopped and that launched in this run, whatever its proxy state now,
    /// including one whose later relaunch was canceled before DCP launched its proxy. Instances that haven't launched
    /// get their first start from Aspire or from a Start command, outside the group.
    /// </summary>
    internal string[] GetIntendedInstances(ResourceNotificationService notifications) =>
        [.. notifications.GetCurrentStates(resource).Select(update => update.ResourceId).Where(id =>
            _instances.TryGetValue(id, out var instance) && !instance.Stopped && (instance.Launched || WasLaunchedByDcp(instance)))];

    /// <summary>
    /// Whether a start of the instance completed its dependency waits in this run. Aspire waits for an instance's
    /// dependencies before DCP launches its proxy, so a proxy that DCP launched means they were satisfied, even if the
    /// proxy failed before it ran.
    /// </summary>
    internal bool WaitedForDependencies(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance) && (instance.Launched || WasLaunchedByDcp(instance));

    /// <summary>Whether DCP reported the instance's proxy. Only DCP observations set the transport state.</summary>
    private static bool WasLaunchedByDcp(Instance instance) =>
        instance.TransportState is { } state && state != KnownResourceStates.NotStarted && state != KnownResourceStates.Waiting;

    /// <summary>Instances whose proxy is running and that no command or refresh is stopping or starting.</summary>
    internal int CountLiveInstances(ResourceNotificationService notifications) =>
        notifications.GetCurrentStates(resource).Count(update =>
            _instances.TryGetValue(update.ResourceId, out var instance) &&
            instance.TransportState == KnownResourceStates.Running &&
            !instance.Stopped && !instance.CoordinatorStopping && !instance.CoordinatorStarting);

    /// <summary>
    /// Whether the instance's proxy is running and the watcher launched the project since the group's last refresh.
    /// The watcher reports launches per project, so for replicas this can't tell which instance it launched.
    /// </summary>
    internal bool IsRunning(string id)
    {
        var observed = Volatile.Read(ref _observed);
        return _instances.TryGetValue(id, out var instance) &&
            instance.TransportState == KnownResourceStates.Running && !instance.CoordinatorStarting &&
            observed.Generation > 0 &&
            (!group.IsSingleInstance(resource) || observed.State == KnownResourceStates.Running);
    }

    /// <summary>Whether the project's single application exited after the watcher launched it.</summary>
    internal bool TryGetExit(out int? exitCode)
    {
        var observed = Volatile.Read(ref _observed);
        exitCode = observed.ExitCode;
        return group.IsSingleInstance(resource) && observed.Generation > 0 && observed.State == DotnetWatchCoordinator.CrashedState;
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

    /// <summary>Whether a transport state ends a start: the proxy runs, or it stopped or failed.</summary>
    private static bool EndsStart(string? state) =>
        state == KnownResourceStates.Running || state is not null && KnownResourceStates.TerminalStates.Contains(state);

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
            return CommandResults.Failure($"'{id}' was stopped manually, so it stays stopped.");
        }

        if (_start is null)
        {
            return CommandResults.Failure($"The original Start callback for '{resource.Name}' is unavailable.");
        }

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        // Stop clears CoordinatorStarting before it waits for the group's lock, which this start holds. Keep the
        // dependency-wait exemption on a marker that only this start clears; otherwise a Stop that arrives during the
        // relaunch makes it wait, under that lock, for a dependency that may never run again.
        var relaunch = WaitedForDependencies(id);
        if (relaunch)
        {
            _relaunching[id] = 0;
        }
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
        finally
        {
            if (relaunch)
            {
                _relaunching.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Starts an instance that hasn't launched in this run. The command claims the instance's first start, and a second
    /// Start leaves the start to it: Aspire's Start would restart the instance, replacing the proxy that the first start
    /// creates with a new connection to the running watcher, which isn't safe. A Stop cancels the claimed start until the
    /// command hands it to Aspire, for example while the group recovers.
    /// </summary>
    private async Task<ExecuteCommandResult> StartNeverLaunchedAsync(ExecuteCommandContext context, DotnetWatchGroupFailure? failure)
    {
        var id = context.ResourceName;
        var claim = new FirstStart(context);
        if (!TryClaimFirstStart(id, claim))
        {
            // Another Start command runs the instance's first start. Waiting for the instance's dependencies, it starts at
            // once, as Aspire's Start does; otherwise it's left to that command.
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
            return await TryStartWithoutWaitingAsync(notifications, id).ConfigureAwait(false)
                ? CommandResults.Success($"Successfully started '{id}'.")
                : CommandResults.Success($"'{id}' is already starting.");
        }

        try
        {
            if (failure is not null)
            {
                // The fresh watcher that recovers the group only relaunches instances, so the group runs this instance's
                // first start after the recovery, outside its restart lock (StartFirstAsync).
                return await group.ReloadAsync(resource, id, start: true, context).ConfigureAwait(false);
            }

            if (group.IsFenced)
            {
                // The group's restart continues a first start that waits for the build. Other instances, such as an
                // explicit-start one or one whose start was canceled, stay as they are.
                return CommandResults.Failure(IsStartPending(id)
                    ? $"Watch group '{group.Name}' is changing, and '{id}' starts when it's done. Use Rebuild to restart the group with a fresh build."
                    : $"Watch group '{group.Name}' is changing. Start '{id}' again when it's done.");
            }

            return (await StartFirstAsync(context).ConfigureAwait(false)).Result;
        }
        finally
        {
            _firstStarts.TryRemove(KeyValuePair.Create(id, claim));
        }
    }

    /// <summary>
    /// Claims the first start of an instance for a Start command, unless another command's claim is in progress. A
    /// claim that a Stop canceled no longer starts the instance, so a later Start replaces it.
    /// </summary>
    private bool TryClaimFirstStart(string id, FirstStart claim)
    {
        while (!_firstStarts.TryAdd(id, claim))
        {
            if (_firstStarts.TryGetValue(id, out var current))
            {
                if (!current.IsCanceled)
                {
                    return false;
                }

                if (_firstStarts.TryUpdate(id, claim, current))
                {
                    return true;
                }
            }
        }

        return true;
    }

    /// <summary>Whether the Start command claimed the first start of its instance (see <see cref="StartNeverLaunchedAsync"/>).</summary>
    internal bool HasFirstStartClaim(ExecuteCommandContext context) => TryGetOwnClaim(context, out _);

    /// <summary>
    /// Whether a Stop canceled the first start that the Start command claimed. A later Start may have claimed the
    /// instance since, and the instance's state then reflects that command, not this one.
    /// </summary>
    internal bool IsFirstStartCanceled(ExecuteCommandContext context) => !TryGetOwnClaim(context, out var claim) || claim.IsCanceled;

    private bool TryGetOwnClaim(ExecuteCommandContext context, [NotNullWhen(true)] out FirstStart? claim) =>
        _firstStarts.TryGetValue(context.ResourceName, out claim) && ReferenceEquals(claim.Command, context);

    /// <summary>
    /// Runs the first start of an instance that hasn't launched in this run, for the Start command that claimed it. The
    /// caller must not hold the group's restart lock: the start waits for the instance's dependencies.
    /// </summary>
    internal async Task<(FirstStartOutcome Outcome, ExecuteCommandResult Result)> StartFirstAsync(ExecuteCommandContext context)
    {
        var id = context.ResourceName;
        // A Stop canceled the command's claim, and a later Start may have claimed the instance since.
        if (!TryGetOwnClaim(context, out var claim) || claim.IsCanceled)
        {
            return (FirstStartOutcome.Canceled, StoppedBeforeItStarted(id));
        }

        if (IsStartPending(id))
        {
            // Aspire's Start would restart a start that is in progress, replacing the proxy that it creates. A start that
            // waits for the instance's dependencies starts at once instead, as Aspire's Start does.
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
            return await TryStartWithoutWaitingAsync(notifications, id).ConfigureAwait(false)
                ? (FirstStartOutcome.Started, CommandResults.Success($"Successfully started '{id}'."))
                : (FirstStartOutcome.AlreadyStarting, CommandResults.Success($"'{id}' is already starting."));
        }

        if (!claim.TryHandOff())
        {
            return (FirstStartOutcome.Canceled, StoppedBeforeItStarted(id));
        }

        return _start is null
            ? (FirstStartOutcome.Started, CommandResults.Failure($"The original Start callback for '{resource.Name}' is unavailable."))
            : (FirstStartOutcome.Started, await _start.ExecuteCommand(context).ConfigureAwait(false));

        static ExecuteCommandResult StoppedBeforeItStarted(string id) =>
            new() { Success = false, Canceled = true, Message = $"'{id}' was stopped before it started." };
    }

    /// <summary>
    /// Releases an instance's start from waiting for its dependencies, as Aspire's Start does for a waiting resource:
    /// moving it out of Waiting ends the wait. Checking and moving it in one update keeps a start whose dependencies
    /// just became ready from being restarted.
    /// </summary>
    private async Task<bool> TryStartWithoutWaitingAsync(ResourceNotificationService notifications, string id)
    {
        if (!notifications.TryGetCurrentState(id, out var current) || current.Snapshot.State?.Text != KnownResourceStates.Waiting)
        {
            return false;
        }

        var released = false;
        await notifications.PublishUpdateAsync(resource, id, snapshot =>
        {
            if (snapshot.State?.Text != KnownResourceStates.Waiting)
            {
                return snapshot;
            }

            released = true;
            return snapshot with { State = KnownResourceStates.Starting };
        }).ConfigureAwait(false);
        return released;
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
        // Aspire/model publications use these states to express startup failure, dependency waiting, or a start that
        // was canceled before DCP launched the proxy, independently of the proxy transport. Other logical updates (for
        // example endpoint health) reuse the projected snapshot and must not replace transport state.
        var snapshotState = snapshot.State?.Text;
        modelPublication = !raw && !internalUpdate &&
            (snapshotState == KnownResourceStates.FailedToStart || snapshotState == KnownResourceStates.Waiting ||
             snapshotState == KnownResourceStates.NotStarted);
        var previous = _instances.GetValueOrDefault(id);
        var instance = previous ?? new(snapshot.State?.Text, null, null, Stopped: false, CoordinatorStopping: false, CoordinatorStarting: false);
        if (raw && !internalUpdate)
        {
            instance = instance with
            {
                TransportState = snapshot.State?.Text,
                TransportPid = snapshot.Properties.First(property => property.Name == "executable.pid").Value,
                TransportExitCode = snapshot.ExitCode,
                CoordinatorStarting = instance.CoordinatorStarting && !IsRunningTransport(snapshot.State?.Text),
                // A running proxy means that Aspire started the instance, so its dependency waits were satisfied.
                Launched = instance.Launched || snapshot.State?.Text == KnownResourceStates.Running,
                StartPending = instance.StartPending && !EndsStart(snapshot.State?.Text)
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
        if (!raw && !internalUpdate && snapshotState == KnownResourceStates.Starting && !instance.Launched)
        {
            // Aspire publishes Starting for an instance when it begins to start it, before the start waits for the
            // watcher's build and the instance's dependencies, and before DCP launches the proxy. A first start that
            // waits for a failed build continues when a fresh watcher's build succeeds.
            instance = instance with { StartPending = true };
        }
        if (modelPublication && snapshotState == KnownResourceStates.FailedToStart)
        {
            // Aspire reports a start that failed before the proxy ran, for example because a dependency failed.
            instance = instance with { StartPending = false };
        }
        if (modelPublication && snapshotState == KnownResourceStates.NotStarted)
        {
            // Aspire reports an instance that it hasn't started, or whose start was canceled before DCP launched the
            // proxy. There's no proxy, so that's the instance's transport, which later updates of the instance project.
            // An instance that DCP launched earlier in this run keeps that fact: its dependency waits were satisfied.
            instance = instance with
            {
                Launched = instance.Launched || WasLaunchedByDcp(instance),
                TransportState = KnownResourceStates.NotStarted,
                TransportPid = null,
                TransportExitCode = null,
                StartPending = false
            };
        }
        else if (!raw && !internalUpdate && snapshotState == KnownResourceStates.Starting &&
            instance.TransportState == KnownResourceStates.NotStarted)
        {
            // A new start begins; DCP reports the proxy once the start reaches it.
            instance = instance with { TransportState = null };
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
        var applicationScope = group.IsSingleInstance(resource);
        var groupFailure = group.Failure;
        var failed = groupFailure?.Summary;
        if (failed is not null)
        {
            state = KnownResourceStates.FailedToStart;
        }
        else if (instance.Stopped)
        {
            state = HasNoProxy(state) ? KnownResourceStates.Exited : KnownResourceStates.Stopping;
        }
        else if (instance.CoordinatorStopping)
        {
            state = HasNoProxy(state) ? KnownResourceStates.Waiting : KnownResourceStates.Stopping;
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
                group.IsReady ? ResourceWatch.Watching : ResourceWatch.Pending,
            groupFailure is { Recoverable: true } ? "Watch infrastructure failed. Use Rebuild to restart the watch group with a fresh watcher." :
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
        bool CoordinatorStarting,
        bool Launched = false,
        bool StartPending = false);
    private sealed record ObservedState(string State, int? ExitCode, string Build, long Generation, string? LastEvent);

    /// <summary>How the first start that a Start command claimed ended.</summary>
    internal enum FirstStartOutcome
    {
        /// <summary>Aspire started the instance, or released its start from waiting; the result says whether it succeeded.</summary>
        Started,

        /// <summary>A start of the instance was already in progress, and it continues.</summary>
        AlreadyStarting,

        /// <summary>A Stop canceled the start before the command handed it to Aspire.</summary>
        Canceled,
    }

    /// <summary>
    /// A Start command's claim on the first start of an instance that hasn't launched in this run. A Stop cancels the
    /// start until the command hands it to Aspire; from then on, Aspire's start cancellation handles a Stop.
    /// </summary>
    private sealed class FirstStart(ExecuteCommandContext command)
    {
        private const int Claimed = 0;
        private const int HandedOff = 1;
        private const int Canceled = 2;
        private int _state;

        /// <summary>The Start command that claimed the start.</summary>
        public ExecuteCommandContext Command { get; } = command;

        public bool IsCanceled => Volatile.Read(ref _state) == Canceled;

        /// <summary>Hands the start to Aspire, unless a Stop canceled it first.</summary>
        public bool TryHandOff() => Interlocked.CompareExchange(ref _state, HandedOff, Claimed) == Claimed;

        /// <summary>Cancels the start, unless the command already handed it to Aspire.</summary>
        public bool TryCancel() => Interlocked.CompareExchange(ref _state, Canceled, Claimed) == Claimed;
    }
}
