// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Coordinates app-model lifecycle events, callback caches, and replica creation without DCP-specific start/stop mechanics.
/// </summary>
internal sealed class DcpResourceLifecycle(
    IConfiguration configuration,
    ResourceLoggerService loggerService,
    DcpExecutorEvents executorEvents,
    DistributedApplicationExecutionContext executionContext)
{
    private readonly HashSet<string> _endpointsAdvertised = new(StringComparers.ResourceName);
    private readonly HashSet<string> _connectionStringsAdvertised = new(StringComparers.ResourceName);

    public async Task PublishEndpointsAllocatedAsync(IEnumerable<IResource> resources, CancellationToken cancellationToken)
    {
        var events = new List<ResourceEndpointsAllocatedEvent>();
        lock (_endpointsAdvertised)
        {
            foreach (var resource in resources.Distinct())
            {
                if (_endpointsAdvertised.Add(resource.Name))
                {
                    events.Add(new ResourceEndpointsAllocatedEvent(resource, executionContext.Services));
                }
            }
        }

        // Callers allocate the entire batch before publishing: callbacks can resolve other resources' URLs.
        await executorEvents.PublishAsync(new OnEndpointsAllocatedContext(cancellationToken, events)).ConfigureAwait(false);
    }

    public async Task PublishConnectionStringAvailableAsync(IResource resource, CancellationToken cancellationToken)
    {
        if (!DcpModelUtilities.AreResourceEndpointsAllocated(resource))
        {
            return;
        }

        lock (_connectionStringsAdvertised)
        {
            if (!_connectionStringsAdvertised.Add(resource.Name))
            {
                return;
            }
        }

        await executorEvents.PublishAsync(new OnConnectionStringAvailableContext(cancellationToken, resource)).ConfigureAwait(false);
    }

    public void ResetForRestart(IResource resource)
    {
        if (resource.TryGetEnvironmentVariables(out var envCallbacks))
        {
            foreach (var callback in envCallbacks)
            {
                ((ICallbackResourceAnnotation<EnvironmentCallbackContext, Dictionary<string, object>>)callback).ForgetCachedResult();
            }
        }

        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbacks))
        {
            foreach (var callback in argsCallbacks)
            {
                ((ICallbackResourceAnnotation<CommandLineArgsCallbackContext, IList<object>>)callback).ForgetCachedResult();
            }
        }

        if (resource.TryGetAnnotationsOfType<LaunchToolArgsCallbackAnnotation>(out var launchToolArgsCallbacks))
        {
            foreach (var callback in launchToolArgsCallbacks)
            {
                ((ICallbackResourceAnnotation<CommandLineArgsCallbackContext, IList<object>>)callback).ForgetCachedResult();
            }
        }

        lock (_connectionStringsAdvertised)
        {
            _connectionStringsAdvertised.Remove(resource.Name);
        }
    }

    public static async ValueTask<IDisposable> AcquireInitializedResourceAsync(IAppResource resource, CancellationToken cancellationToken)
    {
        await resource.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await ConcurrencyUtils.AcquireAllAsync([resource.SerializedOpSemaphore], cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateResourcesAsync<TResource>(
        IEnumerable<TResource> resources,
        Func<TResource, string> getResourceType,
        Func<TResource, bool> isReadyToCreate,
        Func<TResource, bool> isDelayedStart,
        Func<TResource, Func<CustomResourceSnapshot, CustomResourceSnapshot>> buildSnapshot,
        Func<TResource, ILogger, CancellationToken, Task> create,
        CancellationToken shutdownToken,
        CancellationToken cancellationToken)
        where TResource : IRenderedModelResource
    {
        var allResources = resources.ToArray();
        if (allResources.Length == 0)
        {
            return;
        }

        var kinds = allResources.Select(resource => resource.DcpResourceKind).Distinct().ToArray();
        if (kinds.Length != 1)
        {
            throw new ArgumentException($"All resources should be of the same kind when calling CreateRenderedResourcesAsync. Found resource kinds: {string.Join(", ", kinds)}");
        }

        var tasks = allResources.GroupBy(resource => resource.ModelResource)
            .Select(group => Task.Run(() => CreateReplicasAsync(group.Key, group.ToArray()), cancellationToken))
            .ToArray();
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);

        async Task CreateReplicasAsync(IResource modelResource, TResource[] replicas)
        {
            var resourceLogger = loggerService.GetLogger(modelResource);
            var resourceType = getResourceType(replicas[0]);
            using var activity = ProfilingTelemetry.StartResourceCreate(configuration, modelResource, resourceType, replicas.Length);

            try
            {
                using var _ = await ConcurrencyUtils.AcquireAllAsync(replicas.Select(resource => resource.SerializedOpSemaphore), cancellationToken).ConfigureAwait(false);

                // Populate configuration even when startup is deferred, without evaluating start-time callbacks.
                foreach (var replica in replicas)
                {
                    await executorEvents.PublishAsync(new OnResourceChangedContext(
                        shutdownToken, resourceType, modelResource, replica.DcpResourceName,
                        new ResourceStatus(null, null, null), PreviousState: null, buildSnapshot(replica))).ConfigureAwait(false);
                }

                if (!replicas.All(isReadyToCreate))
                {
                    await PublishNotStartedAsync().ConfigureAwait(false);
                    return;
                }

                if (replicas.All(isDelayedStart))
                {
                    // Some backends register persistent, delayed-start objects to discover existing workloads.
                    // This is not a start operation, so do not publish the starting event before registration.
                    await PublishNotStartedAsync().ConfigureAwait(false);
                    foreach (var replica in replicas)
                    {
                        await CreateReplicaAsync(replica).ConfigureAwait(false);
                    }

                    return;
                }

                await PublishConnectionStringAvailableAsync(modelResource, cancellationToken).ConfigureAwait(false);
                var startingName = replicas.Length == 1 ? replicas[0].DcpResourceName : null;
                await executorEvents.PublishAsync(new OnResourceStartingContext(
                    cancellationToken, resourceType, modelResource, startingName)).ConfigureAwait(false);

                foreach (var replica in replicas)
                {
                    await CreateReplicaAsync(replica).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                activity.SetError(ex);
                resourceLogger.LogError(ex, "Failed to create resource {ResourceName}", modelResource.Name);
                await executorEvents.PublishAsync(new OnResourceFailedToStartContext(
                    cancellationToken, resourceType, modelResource, DcpResourceName: null)).ConfigureAwait(false);
            }
            finally
            {
                foreach (var replica in replicas)
                {
                    replica.MarkInitialized();
                }
            }

            async Task PublishNotStartedAsync()
            {
                foreach (var replica in replicas)
                {
                    await executorEvents.PublishAsync(new OnResourceChangedContext(
                        cancellationToken, resourceType, modelResource, replica.DcpResourceName,
                        new ResourceStatus(KnownResourceStates.NotStarted, null, null), PreviousState: null,
                        snapshot => snapshot with { State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, null) })).ConfigureAwait(false);
                }
            }

            async Task CreateReplicaAsync(TResource replica)
            {
                try
                {
                    await create(replica, resourceLogger, cancellationToken).ConfigureAwait(false);
                    await PublishConnectionStringAvailableAsync(replica.ModelResource, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (FailedToApplyEnvironmentException ex)
                {
                    // Configuration resolution already logged actionable details; do not duplicate the stack trace.
                    await executorEvents.PublishAsync(new OnResourceFailedToStartContext(
                        cancellationToken, resourceType, replica.ModelResource, replica.DcpResourceName, ex.Message)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    resourceLogger.LogError(ex, "Failed to create resource {ResourceName}", replica.ModelResource.Name);
                    await executorEvents.PublishAsync(new OnResourceFailedToStartContext(
                        cancellationToken, resourceType, replica.ModelResource, replica.DcpResourceName)).ConfigureAwait(false);
                }
            }
        }
    }

    internal static string? GetInitialResourceState(IResource resource)
    {
        return resource.TryGetLastAnnotation<ResourceSnapshotAnnotation>(out var initial) &&
            initial.InitialSnapshot.State?.Text is string state && !string.IsNullOrEmpty(state)
                ? state
                : null;
    }

    internal static DcpInstance GetDcpInstance(IResource resource, int instanceIndex)
    {
        if (!resource.TryGetInstances(out var instances))
        {
            throw new DistributedApplicationException($"Couldn't find required {nameof(DcpInstancesAnnotation)} annotation on resource {resource.Name}.");
        }

        foreach (var instance in instances)
        {
            if (instance.Index == instanceIndex)
            {
                return instance;
            }
        }

        throw new DistributedApplicationException($"Couldn't find required instance ID for index {instanceIndex} on resource {resource.Name}.");
    }
}
