// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui.Annotations;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Lifecycle;

/// <summary>
/// Event subscriber that serializes MAUI platform resource builds per-project.
/// </summary>
/// <remarks>
/// Multiple MAUI platform resources (Android, iOS, Mac Catalyst, Windows) can reference
/// the same project. MSBuild cannot handle concurrent builds of the same project file,
/// so this subscriber uses a semaphore to ensure only one platform builds at a time.
/// Resources waiting for their turn show a "Queued" state in the dashboard.
/// The build is run as a separate <c>dotnet build</c> subprocess so that the exit code
/// provides reliable build-completion detection and the "Building" state persists in the
/// dashboard for the full build duration. Once the build completes, DCP launches the app
/// with just the Run target.
/// </remarks>
internal class MauiBuildQueueEventSubscriber(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService) : IDistributedApplicationEventingSubscriber
{
    private static readonly ResourceStateSnapshot s_queuedState = new("Queued", KnownResourceStateStyles.Info);
    private static readonly ResourceStateSnapshot s_buildingState = new("Building", KnownResourceStateStyles.Info);
    private static readonly ResourceStateSnapshot s_cancelledState = new(KnownResourceStates.Exited, KnownResourceStateStyles.Warn);

    /// <summary>
    /// Maximum time to wait for a <c>dotnet build</c> process before cancelling.
    /// Prevents a hung build from blocking the queue indefinitely.
    /// </summary>
    internal TimeSpan BuildTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <inheritdoc/>
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeResourceStartedEvent>(OnBeforeResourceStartedAsync);
        return Task.CompletedTask;
    }

    private async Task OnBeforeResourceStartedAsync(BeforeResourceStartedEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Resource is not IMauiPlatformResource mauiResource)
        {
            return;
        }

        var resource = @event.Resource;
        var parent = mauiResource.Parent;
        var logger = loggerService.GetLogger(resource);

        if (!parent.TryGetLastAnnotation<MauiBuildQueueAnnotation>(out var queueAnnotation))
        {
            return;
        }

        // Replace the default stop command with one that can cancel queued/building resources.
        // This must happen here (not at app model build time) because the default lifecycle
        // commands are added by DcpExecutor.EnsureRequiredAnnotations AFTER app model building.
        EnsureStopCommandReplaced(resource, queueAnnotation);

        var semaphore = queueAnnotation.BuildSemaphore;

        // Create a per-resource CTS so the stop command can cancel a queued/building resource.
        using var resourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queueAnnotation.ResourceCancellations[resource.Name] = resourceCts;

        var semaphoreAcquired = false;
        var releaseInFinally = true;

        try
        {
            // Try to acquire the semaphore without blocking. If it's already held,
            // show "Queued" state and then do the real wait.
            if (!semaphore.Wait(TimeSpan.Zero, CancellationToken.None))
            {
                logger.LogInformation("Queued — waiting for another build of project '{ProjectName}' to complete.", parent.Name);

                await notificationService.PublishUpdateAsync(resource, s => s with
                {
                    State = s_queuedState
                }).ConfigureAwait(false);

                await semaphore.WaitAsync(resourceCts.Token).ConfigureAwait(false);
            }

            semaphoreAcquired = true;

            logger.LogInformation("Building project '{ProjectName}' for {ResourceName}.", parent.Name, resource.Name);

            await notificationService.PublishUpdateAsync(resource, s => s with
            {
                State = s_buildingState
            }).ConfigureAwait(false);

            await RunBuildAsync(resource, logger, resourceCts.Token).ConfigureAwait(false);

            // Build succeeded. Keep the semaphore held until DCP finishes launching the app.
            // After this handler returns, DCP invokes `dotnet build /t:Run` which also runs
            // MSBuild. Releasing the semaphore now would let the next queued build start while
            // DCP's MSBuild is still touching shared dependency outputs (e.g., MauiServiceDefaults),
            // causing intermittent XamlC assembly resolution failures.
            releaseInFinally = false;
            _ = ReleaseSemaphoreAfterLaunchAsync(resource, semaphore, s_buildingState.Text, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (resourceCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The per-resource CTS was cancelled by CancelResource (user clicked stop).
            // Re-throw so DCP does not proceed to create/start the executable.
            // The stop command handler sets the final "Exited" state.
            logger.LogInformation("Build cancelled for resource '{ResourceName}'.", resource.Name);
            throw;
        }
        finally
        {
            if (semaphoreAcquired && releaseInFinally)
            {
                ReleaseSemaphoreSafely(semaphore);
                logger.LogDebug("Released build lock (resource '{ResourceName}').", resource.Name);
            }

            queueAnnotation.ResourceCancellations.TryRemove(resource.Name, out _);
        }
    }

    /// <summary>
    /// Runs <c>dotnet build</c> as a subprocess and pipes its output to the resource logger.
    /// </summary>
    internal virtual async Task RunBuildAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        if (!resource.TryGetLastAnnotation<MauiBuildInfoAnnotation>(out var buildInfo))
        {
            logger.LogWarning("No build info annotation found for resource '{ResourceName}'. Skipping build.", resource.Name);
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' is missing MauiBuildInfoAnnotation. " +
                "Cannot proceed with build — the semaphore would be held indefinitely.");
        }

        // Note: --configuration is intentionally omitted. DistributedApplicationOptions.Configuration
        // is internal to Aspire.Hosting and not accessible from this package. DCP handles
        // configuration for the actual 'dotnet build /t:Run' launch command. This pre-build
        // defaults to Debug, which matches the typical local development scenario.
        var args = new List<string> { "build", buildInfo.ProjectPath };

        if (!string.IsNullOrEmpty(buildInfo.TargetFramework))
        {
            args.Add("-f");
            args.Add(buildInfo.TargetFramework);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = buildInfo.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        logger.LogInformation("Running: dotnet {Arguments}", string.Join(" ", args));

        // Apply a timeout so that a hung build does not block the queue forever.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(BuildTimeout);
        var token = timeoutCts.Token;

        using var process = new Process { StartInfo = psi };

        process.Start();

        // Pipe stdout/stderr to the resource logger so output is visible in the dashboard.
        var stdoutTask = PipeOutputAsync(process.StandardOutput, logger, LogLevel.Information, token);
        var stderrTask = PipeOutputAsync(process.StandardError, logger, LogLevel.Warning, token);

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout CTS fired, not the caller's token — this is a build timeout.
            TryKillProcess(process, logger);
            throw new TimeoutException(
                $"Build for resource '{resource.Name}' timed out after {BuildTimeout:c}.");
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process, logger);
            throw;
        }
        finally
        {
            // Always drain remaining output — even on cancellation the process was killed
            // and the streams will reach EOF, so the tasks will complete promptly.
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Build failed for resource '{resource.Name}' with exit code {process.ExitCode}.");
        }

        logger.LogInformation("Build succeeded for resource '{ResourceName}'.", resource.Name);
    }

    private static async Task PipeOutputAsync(System.IO.StreamReader reader, ILogger logger, LogLevel level, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                logger.Log(level, "{Line}", line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the build is cancelled or timed out.
        }
        catch (System.IO.IOException)
        {
            // Broken pipe after the process is killed.
        }
    }

    private static void TryKillProcess(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to kill build process.");
        }
    }

    /// <summary>
    /// Releases the build semaphore after DCP has finished launching the app. DCP invokes
    /// <c>dotnet build /t:Run</c> after this handler returns, which also runs MSBuild.
    /// Holding the semaphore until the resource reaches a stable state prevents concurrent
    /// MSBuild operations on shared dependency outputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate requires the state to differ from <paramref name="stateAtCallTime"/>
    /// (typically "Building") so that the replayed snapshot from
    /// <see cref="ResourceNotificationService.WatchAsync"/> does not immediately satisfy it.
    /// Without this guard a restart could match on a stale snapshot.
    /// </para>
    /// <para>
    /// Including "Running" in the predicate is intentional: the pre-build step already compiled
    /// the project, so DCP's <c>dotnet build /t:Run</c> performs only a fast incremental (no-op)
    /// check before launching the app. Waiting for a terminal state would hold the semaphore for
    /// the entire app lifetime, blocking other platforms from starting.
    /// </para>
    /// </remarks>
    internal virtual async Task ReleaseSemaphoreAfterLaunchAsync(
        IResource resource, SemaphoreSlim semaphore, string? stateAtCallTime,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await notificationService.WaitForResourceAsync(
                resource.Name,
                e =>
                {
                    var text = e.Snapshot.State?.Text;
                    // Skip the replayed snapshot that matches the state when we were called.
                    if (string.Equals(text, stateAtCallTime, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    return text == KnownResourceStates.Running
                        || text == KnownResourceStates.FailedToStart
                        || text == KnownResourceStates.Exited
                        || text == KnownResourceStates.Finished;
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Timed out waiting for resource '{ResourceName}' to reach a stable state; releasing build lock.", resource.Name);
        }
        finally
        {
            ReleaseSemaphoreSafely(semaphore);
            logger.LogDebug("Released build lock (resource '{ResourceName}').", resource.Name);
        }
    }

    /// <summary>
    /// Releases the semaphore, guarding against <see cref="ObjectDisposedException"/>
    /// which can occur during rapid AppHost shutdown if the annotation is disposed concurrently.
    /// </summary>
    private static void ReleaseSemaphoreSafely(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // The semaphore was disposed during shutdown — safe to ignore.
        }
    }

    /// <summary>
    /// Replaces the default stop command with one that can cancel queued/building resources
    /// via the <see cref="MauiBuildQueueAnnotation.CancelResource"/> method, while delegating
    /// to the original stop command for the Running state.
    /// </summary>
    private void EnsureStopCommandReplaced(IResource resource, MauiBuildQueueAnnotation queueAnnotation)
    {
        // Only replace once per resource (supports restart).
        if (resource.Annotations.OfType<MauiStopCommandReplacedAnnotation>().Any())
        {
            return;
        }

        var originalStop = resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .SingleOrDefault(a => a.Name == KnownResourceCommands.StopCommand);

        if (originalStop is null)
        {
            return;
        }

        // Mark as replaced only after confirming the stop command exists
        // and the replacement is fully in place, so a retry on restart can
        // succeed if any step fails.
        resource.Annotations.Remove(originalStop);

        resource.Annotations.Add(new ResourceCommandAnnotation(
            name: KnownResourceCommands.StopCommand,
            displayName: "Stop",
            updateState: context =>
            {
                var state = context.ResourceSnapshot.State?.Text;

                // Show stop for Queued/Building states.
                if (state == s_queuedState.Text || state == s_buildingState.Text)
                {
                    return ResourceCommandState.Enabled;
                }

                // For all other states, delegate to original logic.
                return originalStop.UpdateState(context);
            },
            executeCommand: async context =>
            {
                // Cancel via the annotation — works for both Queued and Building.
                // Use resource.Name (the model name) because the CTS dictionary is keyed
                // by model name, while context.ResourceName is the DCP-resolved name
                // (e.g., "mauiapp-maccatalyst-vqfdyejk" vs "mauiapp-maccatalyst").
                var wasCancelled = queueAnnotation.CancelResource(resource.Name);

                if (wasCancelled)
                {
                    var logger = loggerService.GetLogger(resource);

                    // The BeforeResourceStartedEvent handler re-throws the OCE, which causes
                    // DCP to set FailedToStart. We reactively wait for that state and immediately
                    // override it since this was a user-initiated stop, not a build failure.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            try
                            {
                                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                await notificationService.WaitForResourceAsync(
                                    resource.Name,
                                    e => string.Equals(e.Snapshot.State?.Text, KnownResourceStates.FailedToStart, StringComparison.Ordinal),
                                    cts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Timeout — override anyway in case FailedToStart was never published.
                            }

                            // Only override if DCP set FailedToStart and the user hasn't already clicked
                            // Start again. If a new start registered a CTS, a new build attempt is underway
                            // and we must not overwrite it. Note: there is a narrow TOCTOU window between
                            // this check and PublishUpdateAsync, but the inner guard on FailedToStart state
                            // makes it benign — in the worst case, a concurrent restart's state wins.
                            if (queueAnnotation.ResourceCancellations.ContainsKey(resource.Name))
                            {
                                return;
                            }

                            await notificationService.PublishUpdateAsync(resource, s =>
                            {
                                if (s.State?.Text is not null && s.State.Text != KnownResourceStates.FailedToStart)
                                {
                                    return s;
                                }

                                return s with
                                {
                                    State = s_cancelledState,
                                    StartTimeStamp = null,
                                    StopTimeStamp = null,
                                    ExitCode = null,
                                };
                            }).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Failed to override state to Exited for resource '{ResourceName}'.", resource.Name);
                        }
                    });

                    return CommandResults.Success();
                }

                // Resource is past the queue (Running) — delegate to original stop.
                return await originalStop.ExecuteCommand(context).ConfigureAwait(false);
            },
            displayDescription: null,
            parameter: null,
            confirmationMessage: null,
            iconName: "Stop",
            iconVariant: IconVariant.Filled,
            isHighlighted: true));

        resource.Annotations.Add(new MauiStopCommandReplacedAnnotation());
    }

    /// <summary>
    /// Marker annotation to prevent replacing lifecycle commands more than once.
    /// </summary>
    private sealed class MauiStopCommandReplacedAnnotation : IResourceAnnotation;
}
