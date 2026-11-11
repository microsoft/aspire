// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREWATCH001

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed record DotnetWatchObservedState(string State, int? ExitCode, string Build, long Generation);

internal sealed class DotnetWatchMember(
    DotnetWatchGroup group,
    DotnetWatchRegistration registration,
    IDistributedApplicationBuilder builder,
    IServiceProvider services,
    string cli,
    string[] prefix,
    string pipe)
{
    internal const string RebuildExplanation =
        "Automatic experimental watch builds own this resource. Restart the watch run for a fresh build.";
    private const string RestartRunExplanation =
        "The watch protocol cannot acknowledge completed application cleanup. Restart the watch run to start a manually stopped resource safely.";
    private const string InternalUpdate = "watch.internalUpdate";
    private readonly ILogger _logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(registration.Resource);
    private readonly object _restartLock = new();
    private DotnetWatchObservedState _observed = new(KnownResourceStates.Starting, null, "initializing", 0);
    private TaskCompletionSource _nextStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restartInProgress;
    private IResourceAnnotation? _watchLaunch;
    private ResourceCommandAnnotation? _stop;
    private volatile bool _started;
    private volatile bool _stopped;

    internal DotnetProjectResource Resource => registration.Resource;

    internal void LogWatchMessage(bool error, string message)
    {
        // The resource logger adds its own timestamp; do not nest the server's prefix.
        if (TimestampParser.TryParseConsoleTimestamp(message, out var parsed))
        {
            message = parsed.Value.ModifiedText;
        }
        _logger.Log(error ? LogLevel.Error : LogLevel.Information, "[watch] {Message}", message);
    }

    internal void Configure()
    {
        var resource = builder.CreateResourceBuilder(Resource);
        Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command = cli;
        resource.WithLaunchToolArgs(context =>
        {
            foreach (var argument in prefix)
            {
                context.Args.Add(argument);
            }
            context.Args.Add(DotnetWatchConfiguration.ProxyCommand);
        }, showInCommandLine: false);
        _watchLaunch = Resource.Annotations.Last();
        resource.WithEnvironment(context =>
        {
            if (!group.Enabled)
            {
                return;
            }
            var names = context.EnvironmentVariables.Keys
                .Where(name => name is not (DotnetWatchConfiguration.ResourcePipe or DotnetWatchConfiguration.EntryPoint or
                    DotnetWatchConfiguration.EnvironmentNames or DotnetWatchConfiguration.ExcludeProfile or DotnetWatchConfiguration.LaunchProfile))
                .ToArray();
            context.EnvironmentVariables[DotnetWatchConfiguration.EnvironmentNames] =
                JsonSerializer.Serialize(names, DotnetWatchJsonContext.Default.StringArray);
            context.EnvironmentVariables[DotnetWatchConfiguration.ResourcePipe] = pipe;
            context.EnvironmentVariables[DotnetWatchConfiguration.EntryPoint] = registration.Metadata.ProjectPath;
            // WithProjectDefaults already materialized the selected profile's environment and
            // program arguments. Reapplying it in dotnet run would change precedence.
            context.EnvironmentVariables[DotnetWatchConfiguration.ExcludeProfile] = "true";
        });
        resource.OnBeforeResourceStarted(async (_, _, cancellationToken) =>
        {
            if (!await group.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            if (_stopped || group.Failed)
            {
                throw new DistributedApplicationException(group.Failure ?? RestartRunExplanation);
            }
            _started = true;
            Volatile.Write(ref _observed, new(KnownResourceStates.Starting, null, "idle", _observed.Generation));
            await PublishAsync().ConfigureAwait(false);
        });
        DotnetWatchCoordinator.AddCoverageProjection(Resource, ProjectSnapshot);
        foreach (var command in new[] { "start", "stop", "restart", "rebuild" })
        {
            resource.WithAnnotation(new ResourceLifecycleCommandOverrideAnnotation(command, original =>
            {
                if (original.Name == "stop")
                {
                    _stop = original;
                }
                return DotnetWatchGroup.CloneCommand(original,
                    context => UpdateCommand(original, context),
                    context => ExecuteCommandAsync(original, context),
                    original.Visibility);
            }));
        }
        builder.Eventing.Subscribe<ResourceStoppedEvent>(Resource, async (@event, cancellationToken) =>
        {
            var transport = @event.ResourceEvent.Snapshot.Properties
                .FirstOrDefault(property => property.Name == "watch.transport.state")?.Value as string;
            if (group.Enabled && !group.Stopping && !_stopped &&
                transport is not null && KnownResourceStates.TerminalStates.Contains(transport))
            {
                await group.FailAsync(
                    $"The resource proxy for '{Resource.Name}' disconnected unexpectedly.", cancellationToken).ConfigureAwait(false);
            }
        });
    }

    private ResourceCommandState UpdateCommand(ResourceCommandAnnotation original, UpdateCommandStateContext context)
    {
        if (!group.Enabled)
        {
            return original.UpdateState(context);
        }
        return original.Name switch
        {
            "rebuild" => ResourceCommandState.Disabled,
            "start" when _stopped || _started || group.Failed => ResourceCommandState.Disabled,
            "stop" when _started && !_stopped => ResourceCommandState.Enabled,
            "restart" => _started && !_stopped && !group.Failed && Volatile.Read(ref _restartInProgress) == 0 &&
                Volatile.Read(ref _observed).Generation > 0
                ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
            _ => original.UpdateState(context)
        };
    }

    private async Task<ExecuteCommandResult> ExecuteCommandAsync(ResourceCommandAnnotation original, ExecuteCommandContext context)
    {
        if (!group.Enabled)
        {
            return await original.ExecuteCommand(context).ConfigureAwait(false);
        }
        if (original.Name == "rebuild")
        {
            return new() { Success = false, Message = RebuildExplanation };
        }
        if (original.Name == "start" && (_stopped || _started || group.Failed))
        {
            return new() { Success = false, Message = group.Failure ?? RestartRunExplanation };
        }
        if (original.Name == "stop")
        {
            _stopped = true;
            return await original.ExecuteCommand(context).ConfigureAwait(false);
        }
        if (original.Name == "restart")
        {
            if (!_started || Volatile.Read(ref _observed).Generation == 0 || _stopped || group.Failed)
            {
                return new() { Success = false, Message = group.Failure ?? RestartRunExplanation };
            }
            if (Interlocked.Exchange(ref _restartInProgress, 1) != 0)
            {
                return new() { Success = false, Message = "A watched application restart is already in progress." };
            }
            Task started;
            lock (_restartLock)
            {
                _nextStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
                started = _nextStart.Task;
            }
            try
            {
                await PublishAsync().ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, group.StoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    await group.RestartAsync(registration.Metadata.ProjectPath, timeout.Token).ConfigureAwait(false);
                    await started.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested && group.Stopping)
                {
                    return new() { Success = false, Message = group.Failure ?? "The experimental watch run is stopping." };
                }
                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for the watched application to restart.");
                }
                return new() { Success = true, Message = "The watched application restarted." };
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                _logger.LogError(exception, "Could not restart watched resource '{ResourceName}'.", Resource.Name);
                return new() { Success = false, Message = exception.Message };
            }
            finally
            {
                Volatile.Write(ref _restartInProgress, 0);
                if (!group.Stopping)
                {
                    await PublishAsync().ConfigureAwait(false);
                }
            }
        }
        return await original.ExecuteCommand(context).ConfigureAwait(false);
    }

    internal async Task HandleStatusAsync(DotnetWatchStatus status)
    {
        if (_stopped)
        {
            return;
        }
        var previous = Volatile.Read(ref _observed);
        var observed = status.Type switch
        {
            "building" => previous with { Build = "building" },
            "build_complete" => previous with { Build = status.Success is true ? "idle" : "failed" },
            "hot_reload_applied" => previous with { Build = "idle" },
            "restarting" => previous with { State = KnownResourceStates.Starting, ExitCode = null },
            "process_started" => previous with
            {
                State = KnownResourceStates.Running,
                ExitCode = null,
                Generation = previous.Generation + 1
            },
            "process_exited" when status.ExitCode is not null => previous with
            {
                State = status.ExitCode == 0 ? KnownResourceStates.Finished : KnownResourceStates.Exited, ExitCode = status.ExitCode
            },
            _ => previous
        };
        Volatile.Write(ref _observed, observed);
        if (status.Type == "process_started")
        {
            _started = true;
            lock (_restartLock)
            {
                _nextStart.TrySetResult();
            }
        }
        if (status.Type == "build_complete" && status.Success is false)
        {
            _logger.LogError("The watch build failed for '{ResourceName}'. Correct the source to resume watching.", Resource.Name);
        }
        await PublishAsync().ConfigureAwait(false);
    }

    internal Task PublishAsync() =>
        services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(Resource,
            snapshot => snapshot with
            {
                Properties = [.. snapshot.Properties.Where(property => property.Name != InternalUpdate), new(InternalUpdate, true)]
            });

    internal CustomResourceSnapshot ProjectSnapshot(CustomResourceSnapshot snapshot)
    {
        if (!group.Enabled)
        {
            return DotnetWatchCoordinator.SetCoverage(snapshot with
            {
                Properties = [.. snapshot.Properties.Where(property => property.Name != InternalUpdate &&
                    !property.Name.StartsWith("watch.transport.", StringComparison.Ordinal) &&
                    property.Name is not ("watch.build" or "watch.generation"))]
            }, false, group.Failure ?? "Ordinary execution.");
        }
        var internalUpdate = snapshot.Properties.Any(property => property.Name == InternalUpdate);
        // Per-process DCP snapshots contain executable.pid, including a null value.
        // Health/URL updates instead reuse the stored, already-projected snapshot. Do not
        // reinterpret those logical updates as a missing proxy PID or a proxy exit.
        var projectedUpdate = internalUpdate ||
            (!snapshot.Properties.Any(property => property.Name == "executable.pid") &&
                snapshot.Properties.Any(property => property.Name == "watch.transport.state"));
        var transportState = projectedUpdate
            ? snapshot.Properties.FirstOrDefault(property => property.Name == "watch.transport.state")?.Value as string
            : snapshot.State?.Text;
        var transportPid = projectedUpdate
            ? snapshot.Properties.FirstOrDefault(property => property.Name == "watch.transport.pid")?.Value
            : snapshot.Properties.FirstOrDefault(property => property.Name == "executable.pid")?.Value;
        var transportExit = projectedUpdate
            ? snapshot.Properties.FirstOrDefault(property => property.Name == "watch.transport.exitCode")?.Value
            : snapshot.ExitCode;
        var observed = Volatile.Read(ref _observed);
        var state = _started ? observed.State : transportState;
        if (group.Failed)
        {
            state = KnownResourceStates.FailedToStart;
        }
        else if (_stopped && transportState == KnownResourceStates.Running)
        {
            state = KnownResourceStates.Stopping;
        }
        else if (transportState is not null && KnownResourceStates.TerminalStates.Contains(transportState))
        {
            // A successful proxy exit is not successful application completion. Stop is
            // requested through the proxy, but the selected protocol does not acknowledge
            // application cleanup or supply its exit code.
            state = _stopped ? KnownResourceStates.Exited : KnownResourceStates.FailedToStart;
        }
        else if (transportState == KnownResourceStates.NotStarted || transportState == KnownResourceStates.Waiting ||
            transportState == KnownResourceStates.Stopping)
        {
            state = transportState;
        }
        return DotnetWatchCoordinator.SetCoverage(snapshot with
        {
            State = state is null ? null : new ResourceStateSnapshot(state, group.Failed ? KnownResourceStateStyles.Error : null),
            ExitCode = observed.ExitCode,
            StartTimeStamp = null,
            StopTimeStamp = null,
            Properties = [.. snapshot.Properties.Where(property => property.Name != InternalUpdate &&
                property.Name != "executable.pid" && !property.Name.StartsWith("watch.transport.", StringComparison.Ordinal) &&
                property.Name is not ("watch.build" or "watch.generation")),
                new("watch.transport.state", transportState),
                new("watch.transport.pid", transportPid),
                new("watch.transport.exitCode", transportExit),
                new("watch.build", observed.Build) { IsHighlighted = observed.Build == "failed" },
                new("watch.generation", observed.Generation)]
        }, true, group.Failure ??
            "Experimental, last-observed process state. Successful application exits are not reported by the selected protocol.");
    }

    internal void RestoreLaunch()
    {
        if (_watchLaunch is not null)
        {
            Resource.Annotations.Remove(_watchLaunch);
        }
        var executable = Resource.Annotations.OfType<ExecutableAnnotation>().Last();
        executable.Command = registration.Command;
        executable.WorkingDirectory = registration.WorkingDirectory;
        _logger.LogWarning("Resource '{ResourceName}' is not watched: {Reason} Running normally.", Resource.Name, group.Failure);
    }

    internal async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return true;
        }
        if (_stop is null)
        {
            _logger.LogError("The lifecycle stop callback for watched resource '{ResourceName}' is unavailable.", Resource.Name);
            return false;
        }
        _stopped = true;
        return await DotnetWatchGroup.ExecuteStopAsync(Resource, _stop, services, _logger, cancellationToken).ConfigureAwait(false);
    }
}
