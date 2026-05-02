// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

internal static class CommandsConfigurationExtensions
{
    private const string BuildLogPrefix = "[build] ";
    internal static void AddLifeCycleCommands(this IResource resource)
    {
        if (resource.TryGetLastAnnotation<ExcludeLifecycleCommandsAnnotation>(out _))
        {
            return;
        }

        resource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.StartCommand,
            displayName: CommandStrings.StartName,
            executeCommand: async context =>
            {
                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();

                await orchestrator.StartResourceAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceStarted, resource.GetResolvedDisplayResourceName(context.ResourceName)) };
            },
            updateState: context =>
            {
                var state = context.ResourceSnapshot.State?.Text;
                if (IsStarting(state) || IsBuilding(state) || IsRuntimeUnhealthy(state) || HasNoState(state))
                {
                    return ResourceCommandState.Disabled;
                }
                else if (IsStopped(state) || IsWaiting(state))
                {
                    return ResourceCommandState.Enabled;
                }
                else
                {
                    return ResourceCommandState.Hidden;
                }
            },
            displayDescription: CommandStrings.StartDescription,
            parameter: null,
            argumentInputs: null,
            confirmationMessage: null,
            iconName: "Play",
            iconVariant: IconVariant.Filled,
            isHighlighted: true));

        resource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.StopCommand,
            displayName: CommandStrings.StopName,
            executeCommand: async context =>
            {
                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();

                await orchestrator.StopResourceAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceStopped, resource.GetResolvedDisplayResourceName(context.ResourceName)) };
            },
            updateState: context =>
            {
                var state = context.ResourceSnapshot.State?.Text;
                if (IsStopping(state))
                {
                    return ResourceCommandState.Disabled;
                }
                else if (!IsStopped(state) && !IsStarting(state) && !IsWaiting(state) && !IsBuilding(state) && !IsRuntimeUnhealthy(state) && !HasNoState(state))
                {
                    return ResourceCommandState.Enabled;
                }
                else
                {
                    return ResourceCommandState.Hidden;
                }
            },
            displayDescription: CommandStrings.StopDescription,
            parameter: null,
            argumentInputs: null,
            confirmationMessage: null,
            iconName: "Stop",
            iconVariant: IconVariant.Filled,
            isHighlighted: true));

        // Use a more detailed description for .NET projects to help AI understand
        // that source code changes won't take effect until rebuilding the project.
        var restartDescription = resource is ProjectResource
            ? CommandStrings.RestartProjectDescription
            : CommandStrings.RestartDescription;

        resource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.RestartCommand,
            displayName: CommandStrings.RestartName,
            executeCommand: async context =>
            {
                var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();

                await orchestrator.StopResourceAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                await orchestrator.StartResourceAsync(context.ResourceName, context.CancellationToken).ConfigureAwait(false);
                return new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceRestarted, resource.GetResolvedDisplayResourceName(context.ResourceName)) };
            },
            updateState: context =>
            {
                var state = context.ResourceSnapshot.State?.Text;
                if (IsStarting(state) || IsStopping(state) || IsStopped(state) || IsWaiting(state) || IsBuilding(state) || IsRuntimeUnhealthy(state) || HasNoState(state))
                {
                    return ResourceCommandState.Disabled;
                }
                else
                {
                    return ResourceCommandState.Enabled;
                }
            },
            displayDescription: restartDescription,
            parameter: null,
            argumentInputs: null,
            confirmationMessage: null,
            iconName: "ArrowCounterclockwise",
            iconVariant: IconVariant.Regular,
            isHighlighted: false));

        if (resource is ProjectResource projectResource)
        {
            var projectMetadata = projectResource.Annotations.OfType<IProjectMetadata>().SingleOrDefault();
            if (projectMetadata is null || !projectMetadata.IsFileBasedApp)
            {
                AddRebuildCommand(projectResource);
            }
        }

        // Treat "Unknown" as stopped so the command to start the resource is available when "Unknown".
        // There is a situation where a container can be stopped with this state: https://github.com/microsoft/aspire/issues/5977
        static bool IsStopped(string? state) => KnownResourceStates.TerminalStates.Contains(state) || state == KnownResourceStates.NotStarted || state == "Unknown";
        static bool IsStopping(string? state) => state == KnownResourceStates.Stopping;
        static bool IsStarting(string? state) => state == KnownResourceStates.Starting;
        static bool IsWaiting(string? state) => state == KnownResourceStates.Waiting;
        static bool IsBuilding(string? state) => state == KnownResourceStates.Building;
        static bool IsRuntimeUnhealthy(string? state) => state == KnownResourceStates.RuntimeUnhealthy;
        static bool HasNoState(string? state) => string.IsNullOrEmpty(state);
    }

    private static void AddRebuildCommand(ProjectResource projectResource)
    {
        // When a resource has replicas, the command framework invokes the handler
        // once per replica in parallel. We use a shared task so only a single build
        // runs and every replica handler awaits the same result.
        Task<ExecuteCommandResult>? activeRebuildTask = null;
        var rebuildLock = new object();

        projectResource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.RebuildCommand,
            displayName: CommandStrings.RebuildName,
            executeCommand: context =>
            {
                lock (rebuildLock)
                {
                    activeRebuildTask ??= ExecuteRebuildAndResetAsync(context);
                    return activeRebuildTask;
                }
            },
            updateState: context =>
            {
                var state = context.ResourceSnapshot.State?.Text;
                return state is not null && KnownResourceStates.BuildableStates.Contains(state)
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Disabled;
            },
            displayDescription: CommandStrings.RebuildDescription,
            parameter: null,
            argumentInputs: null,
            confirmationMessage: null,
            iconName: "ArrowSync",
            iconVariant: IconVariant.Regular,
            isHighlighted: false));

        async Task<ExecuteCommandResult> ExecuteRebuildAndResetAsync(ExecuteCommandContext context)
        {
            try
            {
                return await ExecuteRebuildAsync(context, projectResource).ConfigureAwait(false);
            }
            finally
            {
                lock (rebuildLock)
                {
                    activeRebuildTask = null;
                }
            }
        }
    }

    private static async Task<ExecuteCommandResult> ExecuteRebuildAsync(ExecuteCommandContext context, ProjectResource projectResource)
    {
        var orchestrator = context.ServiceProvider.GetRequiredService<ApplicationOrchestrator>();
        var resourceNotificationService = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var loggerService = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        var model = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();

        var rebuilderResource = model.Resources.OfType<ProjectRebuilderResource>().FirstOrDefault(r => r.Parent == projectResource);
        if (rebuilderResource is null)
        {
            return new ExecuteCommandResult { Success = false, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.RebuilderResourceNotFound, projectResource.Name) };
        }

        var mainLogger = loggerService.GetLogger(projectResource);
        var replicaNames = projectResource.GetResolvedResourceNames();
        using var buildOutput = new BuildOutputCollector();

        // Capture each replica's state before rebuild so we can restore inactive replicas.
        var preRebuildStates = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in replicaNames)
        {
            if (resourceNotificationService.TryGetCurrentState(name, out var evt))
            {
                preRebuildStates[name] = evt.Snapshot.State?.Text;
            }
        }

        // Stop non-waiting replicas. Waiting replicas have no running process — their DCP
        // lifecycle is blocked at WaitForInBeforeResourceStartedEvent waiting for dependencies.
        // Attempting to stop them would fail because no DCP Executable has been created yet.
        var replicasToStop = replicaNames.Where(name =>
            !preRebuildStates.TryGetValue(name, out var state)
            || state != KnownResourceStates.Waiting);

        LogBuildInformation(mainLogger, buildOutput, "Stopping resource for rebuild...");
        await Task.WhenAll(replicasToStop.Select(name => orchestrator.StopResourceAsync(name, context.CancellationToken))).ConfigureAwait(false);

        // Set state to Building after replicas are stopped. Leave Waiting replicas in their
        // current state — changing their state text would unblock WaitForInBeforeResourceStartedEvent,
        // causing CreateExecutableAsync to launch the OLD binary while the build is in progress.
        await resourceNotificationService.PublishUpdateAsync(projectResource, s =>
            s.State?.Text == KnownResourceStates.Waiting
                ? s
                : s with { State = new ResourceStateSnapshot(KnownResourceStates.Building, KnownResourceStateStyles.Info) }
        ).ConfigureAwait(false);

        // Start forwarding logs from the rebuilder to the main resource's console.
        using var logCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var rebuilderInstanceName = rebuilderResource.GetResolvedResourceNames()[0];
        var logForwardTask = ForwardLogsAsync(loggerService, rebuilderInstanceName, mainLogger, buildOutput, logCts.Token);
        var logForwardingStopped = false;

        async Task<ExecuteCommandResult> FinishAsync(ExecuteCommandResult result)
        {
            if (!logForwardingStopped)
            {
                await StopLogForwardingAsync(logCts, logForwardTask).ConfigureAwait(false);
                logForwardingStopped = true;
            }

            return AttachBuildOutput(result, buildOutput.GetOutput());
        }

        try
        {
            // Start the rebuilder resource (runs dotnet build).
            LogBuildInformation(mainLogger, buildOutput, "Building project...");
            await orchestrator.StartResourceAsync(rebuilderInstanceName, context.CancellationToken).ConfigureAwait(false);

            // Wait for the rebuilder to reach a terminal state, with a timeout.
            int? exitCode = null;
            using var buildTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            buildTimeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                await foreach (var evt in resourceNotificationService.WatchAsync(buildTimeoutCts.Token).ConfigureAwait(false))
                {
                    if (evt.Resource == rebuilderResource &&
                        KnownResourceStates.TerminalStates.Contains(evt.Snapshot.State?.Text))
                    {
                        exitCode = evt.Snapshot.ExitCode;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                // Build timed out.
                LogBuildError(mainLogger, buildOutput, "Build timed out.");

                await resourceNotificationService.PublishUpdateAsync(projectResource, s => s with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error)
                }).ConfigureAwait(false);
                return await FinishAsync(new ExecuteCommandResult { Success = false, Message = "Build timed out." }).ConfigureAwait(false);
            }

            if (exitCode == 0)
            {
                // Restart replicas that were Running before the rebuild.
                // Waiting replicas are already in the startup pipeline — when their deps become
                // ready, CreateExecutableAsync will launch the freshly-built binary automatically.
                LogBuildInformation(mainLogger, buildOutput, "Build succeeded. Restarting resource...");
                var anyRestarted = false;
                foreach (var name in replicaNames)
                {
                    var wasActive = preRebuildStates.TryGetValue(name, out var priorState)
                        && priorState == KnownResourceStates.Running;

                    var wasWaiting = preRebuildStates.TryGetValue(name, out var waitState)
                        && waitState == KnownResourceStates.Waiting;

                    if (wasWaiting)
                    {
                        // The resource is still waiting for dependencies. The build output on disk
                        // has been updated, so when dependencies become ready the new binary will
                        // be launched. Log a message so the user knows the build succeeded.
                        LogBuildInformation(mainLogger, buildOutput, "Build succeeded. Resource will start with the updated binary when dependencies are ready.");
                        anyRestarted = true;
                    }
                    else if (wasActive)
                    {
                        anyRestarted = true;
                        await resourceNotificationService.PublishUpdateAsync(projectResource, name, s => s with
                        {
                            State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info)
                        }).ConfigureAwait(false);

                        await orchestrator.StartResourceAsync(name, context.CancellationToken).ConfigureAwait(false);
                    }
                }

                if (!anyRestarted)
                {
                    // No replicas were running before rebuild (e.g. resource was stopped).
                    // Restore each replica to its pre-build state so it doesn't stay stuck
                    // in "Building" indefinitely.
                    foreach (var name in replicaNames)
                    {
                        if (preRebuildStates.TryGetValue(name, out var priorState) && priorState is not null)
                        {
                            await resourceNotificationService.PublishUpdateAsync(projectResource, name, s => s with
                            {
                                State = new ResourceStateSnapshot(priorState, KnownResourceStateStyles.Info)
                            }).ConfigureAwait(false);
                        }
                    }
                }

                return await FinishAsync(new ExecuteCommandResult { Success = true, Message = string.Format(CultureInfo.InvariantCulture, CommandStrings.ResourceRebuilt, projectResource.Name) }).ConfigureAwait(false);
            }
            else
            {
                var failureMessage = $"Build failed with exit code {exitCode}.";
                buildOutput.Append(failureMessage);
                mainLogger.LogError(BuildLogPrefix + "Build failed with exit code {ExitCode}.", exitCode);
                await resourceNotificationService.PublishUpdateAsync(projectResource, s => s with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error)
                }).ConfigureAwait(false);
                return await FinishAsync(new ExecuteCommandResult { Success = false, Message = failureMessage }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The command was cancelled (e.g. user navigated away or the dashboard closed).
            // The replicas were already stopped for the rebuild, so set them to Exited.
            LogBuildWarning(mainLogger, buildOutput, "Rebuild was cancelled.");
            await resourceNotificationService.PublishUpdateAsync(projectResource, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);
            return await FinishAsync(new ExecuteCommandResult { Success = false, Message = "Rebuild was cancelled." }).ConfigureAwait(false);
        }
        finally
        {
            if (!logForwardingStopped)
            {
                await StopLogForwardingAsync(logCts, logForwardTask).ConfigureAwait(false);
            }
        }
    }

    private static ExecuteCommandResult AttachBuildOutput(ExecuteCommandResult result, string output)
    {
        if (result.Data is not null || string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        return new ExecuteCommandResult
        {
            Success = result.Success,
            Canceled = result.Canceled,
            Message = result.Message,
            Data = new CommandResultData
            {
                Value = output,
                Format = CommandResultFormat.Text
            }
        };
    }

    private static async Task StopLogForwardingAsync(CancellationTokenSource logCts, Task logForwardTask)
    {
        await logCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await logForwardTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelling the log forwarder.
        }
    }

    private static void LogBuildInformation(ILogger logger, BuildOutputCollector buildOutput, string message)
    {
        buildOutput.Append(message);
        logger.LogInformation(BuildLogPrefix + "{Message}", message);
    }

    private static void LogBuildWarning(ILogger logger, BuildOutputCollector buildOutput, string message)
    {
        buildOutput.Append(message);
        logger.LogWarning(BuildLogPrefix + "{Message}", message);
    }

    private static void LogBuildError(ILogger logger, BuildOutputCollector buildOutput, string message)
    {
        buildOutput.Append(message);
        logger.LogError(BuildLogPrefix + "{Message}", message);
    }

    private static async Task ForwardLogsAsync(ResourceLoggerService loggerService, string sourceResourceName, ILogger targetLogger, BuildOutputCollector buildOutput, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batch in loggerService.WatchAsync(sourceResourceName).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var line in batch)
                {
                    buildOutput.Append(line.Content);

                    if (line.IsErrorMessage)
                    {
                        targetLogger.LogWarning(BuildLogPrefix + "{Content}", line.Content);
                    }
                    else
                    {
                        targetLogger.LogInformation(BuildLogPrefix + "{Content}", line.Content);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the log forwarding is cancelled.
        }
    }

    private sealed class BuildOutputCollector : IDisposable
    {
        private const int MaxBuildOutputLineCount = 10_000;

        private readonly object _lock = new();
        private readonly Queue<string> _lines = new();
        private int _droppedLineCount;

        public void Append(string content)
        {
            var lines = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            lock (_lock)
            {
                foreach (var line in lines)
                {
                    AppendLine(line);
                }
            }
        }

        private void AppendLine(string content)
        {
            if (_lines.Count == MaxBuildOutputLineCount)
            {
                _lines.Dequeue();
                _droppedLineCount++;
            }

            _lines.Enqueue(BuildLogPrefix + content);
        }

        public string GetOutput()
        {
            lock (_lock)
            {
                var builder = new StringBuilder();
                if (_droppedLineCount > 0)
                {
                    builder
                        .Append(BuildLogPrefix)
                        .Append("Output truncated to last ")
                        .Append(MaxBuildOutputLineCount)
                        .Append(" lines. ")
                        .Append(_droppedLineCount)
                        .AppendLine(" earlier lines omitted.");
                }

                foreach (var line in _lines)
                {
                    builder.AppendLine(line);
                }

                return builder.ToString().TrimEnd();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _lines.Clear();
                _droppedLineCount = 0;
            }
        }
    }
}
