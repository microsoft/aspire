// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;
using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Health;

internal class ResourceHealthCheckService(ILogger<ResourceHealthCheckService> logger, ResourceNotificationService resourceNotificationService, HealthCheckService healthCheckService, IServiceProvider services, IDistributedApplicationEventing eventing, TimeProvider timeProvider) : BackgroundService
{
    private readonly Dictionary<string, ResourceMonitorState> _resourceMonitoringStates = new();

    // Internal for testing.
    internal TimeSpan HealthyHealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan NonHealthyHealthCheckStepInterval { get; set; } = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var resourceEvents = resourceNotificationService.WatchAsync(stoppingToken);

            // Watch for resource notifications and start and stop health monitoring based on the state of the resource.
            await foreach (var resourceEvent in resourceEvents.ConfigureAwait(false))
            {
                var resourceName = resourceEvent.Resource.Name;
                ResourceMonitorState? state;

                lock (_resourceMonitoringStates)
                {
                    if (_resourceMonitoringStates.TryGetValue(resourceName, out state))
                    {
                        state.SetLatestEvent(resourceEvent);
                    }
                }

                if (resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running)
                {
                    if (state == null)
                    {
                        // The resource has entered a running state so it's time to start monitoring it's health.
                        state = new ResourceMonitorState(logger, resourceEvent, stoppingToken);

                        lock (_resourceMonitoringStates)
                        {
                            _resourceMonitoringStates[resourceName] = state;
                        }

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await MonitorResourceHealthAsync(state).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // Ignore error if resource monitoring was cancelled.
                                if (state.CancellationToken.IsCancellationRequested)
                                {
                                    return;
                                }

                                logger.LogError(ex, "Unexpected error ended health monitoring for resource '{ResourceName}'.", resourceName);
                            }
                        }, state.CancellationToken);
                    }
                }
                else if (KnownResourceStates.TerminalStates.Contains(resourceEvent.Snapshot.State?.Text))
                {
                    if (state != null)
                    {
                        // Health monitors are keyed by the resource model name, but events
                        // arrive per-replica. When a resource has multiple replicas, a single
                        // replica entering a terminal state should not stop health monitoring
                        // for the entire resource — other replicas may still be running and
                        // need continued health evaluation through the shared DCP port.
                        var resource = resourceEvent.Resource;
                        var allReplicasTerminal = resource.GetResolvedResourceNames().All(name =>
                            resourceNotificationService.TryGetCurrentState(name, out var evt)
                            && KnownResourceStates.TerminalStates.Contains(evt.Snapshot.State?.Text));

                        if (allReplicasTerminal)
                        {
                            state.StopResourceMonitor();
                            lock (_resourceMonitoringStates)
                            {
                                _resourceMonitoringStates.Remove(resourceName);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // This was expected as the token was canceled
        }
    }

    // Internal for testing.
    internal ResourceMonitorState? GetResourceMonitorState(string resourceName)
    {
        lock (_resourceMonitoringStates)
        {
            _resourceMonitoringStates.TryGetValue(resourceName, out var state);
            return state;
        }
    }

    private async Task MonitorResourceHealthAsync(ResourceMonitorState state)
    {
        var cancellationToken = state.CancellationToken;
        var resource = state.LatestEvent.Resource;

        if (!resource.TryGetAnnotationsIncludingAncestorsOfType<HealthCheckAnnotation>(out var annotations))
        {
            // NOTE: If there are no health check annotations then there
            //       is currently nothing to monitor. At this point in time we don't
            //       dynamically add health checks at runtime. If this changes then we
            //       would need to revisit this and scan for transitive health checks
            //       on a periodic basis (you wouldn't want to do it on every pass.
            logger.LogDebug("Resource '{ResourceName}' has no health checks to monitor.", resource.Name);
            FireResourceReadyEvent(resource, cancellationToken);

            return;
        }

        var registrationKeysToCheck = annotations.DistinctBy(a => a.Key).Select(a => a.Key).ToFrozenSet();
        logger.LogDebug("Resource '{ResourceName}' health checks to monitor: {HeathCheckKeys}", resource.Name, string.Join(", ", registrationKeysToCheck));

        var lastHealthCheckTimestamp = 0L;
        var lastDelayInterrupted = false;
        var resourceReadyEventFired = false;
        var nonHealthyReportCount = 0;
        ResourceEvent? currentEvent = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // If the delay was interrupted after less than a second then delay again.
                // This prevents health checks from being called too frequently.
                if (lastDelayInterrupted && TimeSpan.FromSeconds(1) - timeProvider.GetElapsedTime(lastHealthCheckTimestamp) is { Ticks: > 0 } delay)
                {
                    await state.DelayAsync(currentEvent: null, delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                HealthReport report;
                try
                {
                    report = await healthCheckService.CheckHealthAsync(
                        r => registrationKeysToCheck.Contains(r.Name),
                        cancellationToken
                        ).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // It's possible for CheckHealthAsync to throw if there is an error creating the IHealthCheck instance.
                    // In this case we don't get an error report so we have to build one. We don't know exactly which registration failed
                    // so set them all to unhealthy with the thrown error as the reason.
                    // This situation won't be common, but we need to handle it to prevent the monitoring loop from never informing the user.
                    report = new HealthReport(registrationKeysToCheck.ToDictionary(k => k, k => new HealthReportEntry(HealthStatus.Unhealthy, "Error calling HealthCheckService.", TimeSpan.Zero, ex, data: null)), TimeSpan.Zero);
                }

                var lastRunAt = timeProvider.GetUtcNow().UtcDateTime;

                logger.LogTrace("Health report status for '{ResourceName}' is {HealthReportStatus}.", resource.Name, report.Status);

                if (report.Status == HealthStatus.Healthy)
                {
                    if (!resourceReadyEventFired)
                    {
                        resourceReadyEventFired = true;
                        FireResourceReadyEvent(resource, cancellationToken);
                    }
                    nonHealthyReportCount = 0;
                }
                else
                {
                    nonHealthyReportCount++;
                }

                currentEvent = state.LatestEvent;

                if (ContainsAnyHealthReportChange(report, currentEvent.Snapshot.HealthReports))
                {
                    logger.LogTrace("Health reports for '{ResourceName}' have changed.", resource.Name);
                }

                // Publish updated health reports every time because the last run time has changed.
                await resourceNotificationService.PublishUpdateAsync(resource, s =>
                {
                    var healthReports = MergeHealthReports(s.HealthReports, report, lastRunAt);

                    return s with
                    {
                        // HealthStatus is automatically re-computed after health reports change.
                        HealthReports = healthReports
                    };
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // When debugging sometimes we'll get cancelled here but we don't want
                // to tear down the loop. We only want to crash out when the service's
                // cancellation token is signaled.
                logger.LogTrace(
                    ex,
                    "Health check monitoring loop for resource '{ResourceName}' observed exception but was ignored.",
                    resource.Name
                    );
            }

            lastHealthCheckTimestamp = timeProvider.GetTimestamp();

            // Long delay if the resource is healthy.
            // Non-heathy delay increases with each consecutive non-healthy report.
            var delayInterval = nonHealthyReportCount == 0
                ? HealthyHealthCheckInterval
                : NonHealthyHealthCheckStepInterval * Math.Min(5, nonHealthyReportCount);

            logger.LogTrace("Resource '{ResourceName}' health check monitoring loop starting delay of {DelayInterval}.", resource.Name, delayInterval);

            lastDelayInterrupted = await state.DelayAsync(currentEvent, delayInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ContainsAnyHealthReportChange(HealthReport report, ImmutableArray<HealthReportSnapshot> latestHealthReportSnapshots)
    {
        var healthCheckNameToStatus = latestHealthReportSnapshots.ToDictionary(p => p.Name);
        foreach (var (key, value) in report.Entries)
        {
            if (!healthCheckNameToStatus.TryGetValue(key, out var checkReportSnapshot))
            {
                return true;
            }

            if (checkReportSnapshot.Status != value.Status
                || !string.Equals(checkReportSnapshot.Description, value.Description, StringComparisons.HealthReportPropertyValue)
                || !string.Equals(checkReportSnapshot.ExceptionText, value.Exception?.ToString(), StringComparisons.HealthReportPropertyValue))
            {
                return true;
            }
        }

        return false;
    }

    private void FireResourceReadyEvent(IResource resource, CancellationToken cancellationToken)
    {
        logger.LogDebug("Resource '{ResourceName}' is ready.", resource.Name);

        // We don't want to block the monitoring loop while we fire the event.
        _ = Task.Run(async () =>
        {
            var resourceReadyEvent = new ResourceReadyEvent(resource, services);

            logger.LogDebug("Publishing ResourceReadyEvent for '{ResourceName}'.", resource.Name);

            // Execute the publish and store the task so that waiters can await it and observe the result.
            var task = eventing.PublishAsync(resourceReadyEvent, cancellationToken);

            logger.LogDebug("Waiting for ResourceReadyEvent for '{ResourceName}'.", resource.Name);

            // Suppress exceptions, we just want to make sure that the event is completed.
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            logger.LogDebug("ResourceReadyEvent for '{ResourceName}' completed.", resource.Name);

            logger.LogDebug("Publishing the result of ResourceReadyEvent for '{ResourceName}'.", resource.Name);

            await resourceNotificationService.PublishUpdateAsync(resource, s => s with
            {
                ResourceReadyEvent = new(task)
            })
            .ConfigureAwait(false);
        },
        cancellationToken);
    }

    private static ImmutableArray<HealthReportSnapshot> MergeHealthReports(ImmutableArray<HealthReportSnapshot> healthReports, HealthReport report, DateTime runAt)
    {
        var builder = healthReports.ToBuilder();

        foreach (var (key, entry) in report.Entries)
        {
            var snapshot = new HealthReportSnapshot(key, entry.Status, entry.Description, entry.Exception?.ToString())
            {
                LastRunAt = runAt
            };

            var found = false;
            for (var i = 0; i < builder.Count; i++)
            {
                var existing = builder[i];
                if (existing.Name == key)
                {
                    // Replace the existing entry.
                    builder[i] = snapshot;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                // Add a new entry.
                builder.Add(snapshot);
            }
        }

        return builder.ToImmutable();
    }

    internal class ResourceMonitorState
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts;
        private readonly object _lock = new object();
        private readonly string _resourceName;
        private TaskCompletionSource? _delayInterruptTcs;

        public ResourceMonitorState(ILogger logger, ResourceEvent initialEvent, CancellationToken serviceStoppingToken)
        {
            _logger = logger;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serviceStoppingToken);
            _resourceName = initialEvent.Resource.Name;
            LatestEvent = initialEvent;

            _logger.LogDebug("Starting health monitoring for resource '{ResourceName}'.", _resourceName);
        }

        // Used to cancel and exit the monitoring loop for a resource.
        public CancellationToken CancellationToken => _cts.Token;

        public ResourceEvent LatestEvent { get; private set; }

        public void StopResourceMonitor()
        {
            _logger.LogDebug("Stopping health monitoring for resource '{ResourceName}'.", _resourceName);
            _cts.Cancel();
        }

        public void SetLatestEvent(ResourceEvent resourceEvent)
        {
            // Set the latest event to the monitor. The monitor delay may be interrupted if necessary.
            // A lock protects against a race between starting a delay and setting the latest event.
            lock (_lock)
            {
                var shouldInterrupt = ShouldInterrupt(resourceEvent, LatestEvent);
                LatestEvent = resourceEvent;

                if (shouldInterrupt)
                {
                    _delayInterruptTcs?.TrySetResult();
                }
            }
        }

        internal async Task<bool> DelayAsync(ResourceEvent? currentEvent, TimeSpan delay, CancellationToken cancellationToken)
        {
            Task delayInterruptedTask;
            lock (_lock)
            {
                // The event might have changed before delay was called. Interrupt immediately if required.
                if (currentEvent != null && ShouldInterrupt(currentEvent, LatestEvent))
                {
                    _logger.LogTrace("Health monitoring delay interrupted for resource '{ResourceName}'.", _resourceName);
                    return true;
                }
                _delayInterruptTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delayInterruptedTask = _delayInterruptTcs.Task;
            }

            // Don't throw to avoid writing the thrown exception to the debug console.
            // See https://github.com/microsoft/aspire/issues/7486
            await delayInterruptedTask.WaitAsync(delay, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var delayInterrupted = delayInterruptedTask.IsCompletedSuccessfully == true;

            return delayInterrupted;
        }

        private static bool ShouldInterrupt(ResourceEvent currentEvent, ResourceEvent previousEvent)
        {
            // Interrupt if a newer snapshot is available and the state has changed.
            // This is to ensure that health checks are immediately re-evaluated when the state changes.
            if (currentEvent.Snapshot.Version <= previousEvent.Snapshot.Version)
            {
                return false;
            }
            if (currentEvent.Snapshot.State?.Text == previousEvent.Snapshot.State?.Text)
            {
                return false;
            }

            return true;
        }
    }
}
