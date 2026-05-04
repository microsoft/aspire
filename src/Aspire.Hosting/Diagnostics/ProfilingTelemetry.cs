// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;

namespace Aspire.Hosting.Diagnostics;

internal static class ProfilingTelemetry
{
    public const string ActivitySourceName = "Aspire.Hosting.Profiling";

    internal static class Activities
    {
        public const string DcpRunApplication = "aspire.hosting.dcp.run_application";
        public const string DcpPrepareServices = "aspire.hosting.dcp.prepare_services";
        public const string DcpPrepareResources = "aspire.hosting.dcp.prepare_resources";
        public const string DcpAllocateServiceAddresses = "aspire.hosting.dcp.allocate_service_addresses";
        public const string DcpCreateObjects = "aspire.hosting.dcp.create_objects";
        public const string DcpCreateObject = "aspire.hosting.dcp.create_object";
        public const string DcpCreateRenderedResources = "aspire.hosting.dcp.create_rendered_resources";
        public const string ResourceCreate = "aspire.hosting.resource.create";
        public const string DcpCreateResourceReplica = "aspire.hosting.dcp.create_resource_replica";
        public const string DcpKubernetesApi = "aspire.hosting.dcp.kubernetes_api";
        public const string DcpEnsureKubernetesClient = "aspire.hosting.dcp.ensure_kubernetes_client";
        public const string DcpResourceObserved = "aspire.hosting.dcp.resource_observed";
        public const string ResourceBeforeStartWait = "aspire.hosting.resource.before_start_wait";
        public const string ResourceWaitForDependency = "aspire.hosting.resource.wait_for_dependency";
        public const string ResourceWaitForDependencies = "aspire.hosting.resource.wait_for_dependencies";
        public const string ResourceStop = "aspire.hosting.resource.stop";
        public const string ResourceStart = "aspire.hosting.resource.start";
    }

    internal static class Tags
    {
        public const string OperationId = "aspire.startup.operation_id";
        public const string AppHostName = "aspire.apphost.name";
        public const string AppHostOperation = "aspire.apphost.operation";
        public const string ResourceName = "aspire.resource.name";
        public const string ResourceId = "aspire.resource.id";
        public const string ResourceType = "aspire.resource.type";
        public const string ResourceKind = "aspire.resource.kind";
        public const string ResourceCount = "aspire.resource.count";
        public const string ResourceReplicaCount = "aspire.resource.replica_count";
        public const string ResourceStopped = "aspire.resource.stopped";
        public const string ResourceState = "aspire.resource.state";
        public const string ResourceHealthStatus = "aspire.resource.health_status";
        public const string ResourceExitCode = "aspire.resource.exit_code";
        public const string ResourceReady = "aspire.resource.ready";
        public const string ResourceSnapshotVersion = "aspire.resource.snapshot.version";
        public const string ResourceStartTime = "aspire.resource.start_time";
        public const string ResourceStopTime = "aspire.resource.stop_time";
        public const string ResourceWaitExpectedExitCode = "aspire.resource.wait.expected_exit_code";
        public const string ResourceWaitDependencyCount = "aspire.resource.wait.dependency_count";
        public const string ResourceWaitType = "aspire.resource.wait.type";
        public const string ResourceWaitDependencyName = "aspire.resource.wait.dependency.name";
        public const string ResourceWaitDependencyType = "aspire.resource.wait.dependency.type";
        public const string ResourceWaitBehavior = "aspire.resource.wait.behavior";
        public const string ResourceWaitTargetName = "aspire.resource.wait.target.name";
        public const string ResourceWaitCondition = "aspire.resource.wait.condition";
        public const string DcpResourceName = "aspire.dcp.resource.name";
        public const string DcpResourceKind = "aspire.dcp.resource.kind";
        public const string DcpResourceCount = "aspire.dcp.resource.count";
        public const string DcpContainerCount = "aspire.dcp.container.count";
        public const string DcpExecutableCount = "aspire.dcp.executable.count";
        public const string DcpServiceCount = "aspire.dcp.service.count";
        public const string DcpServiceAllocatedCount = "aspire.dcp.service.allocated_count";
        public const string DcpServiceName = "aspire.dcp.service.name";
        public const string DcpApiOperation = "aspire.dcp.api.operation";
        public const string DcpApiRetryCount = "aspire.dcp.api.retry_count";
        public const string DcpApiRetryAttempt = "aspire.dcp.api.retry_attempt";
        public const string DcpApiRetryDelayMilliseconds = "aspire.dcp.api.retry_delay_ms";
        public const string DcpKubeconfigExists = "aspire.dcp.kubeconfig.exists";
        public const string DcpKubeconfigLockWaitMilliseconds = "aspire.dcp.kubeconfig.lock_wait_ms";
        public const string DcpKubeconfigReadDurationMilliseconds = "aspire.dcp.kubeconfig.read_duration_ms";
        public const string DcpKubernetesClientAlreadyInitialized = "aspire.dcp.kubernetes_client_already_initialized";
        public const string DcpCreateObjectId = "aspire.hosting.dcp.create_object.id";
        public const string DcpCreateObjectKind = "aspire.hosting.dcp.create_object.kind";
        public const string DcpCreateObjectName = "aspire.hosting.dcp.create_object.name";
        public const string DcpCreateObjectTraceId = "aspire.hosting.dcp.create_object.trace_id";
        public const string DcpCreateObjectSpanId = "aspire.hosting.dcp.create_object.span_id";
        public const string ExceptionType = "exception.type";
        public const string ExceptionMessage = "exception.message";
    }

    internal static class Events
    {
        public const string DcpServiceAddressAllocated = "aspire.dcp.service_address_allocated";
        public const string DcpServiceAddressAllocationFailed = "aspire.dcp.service_address_allocation_failed";
        public const string KubernetesApiTimeout = "aspire.hosting.dcp.kubernetes_api.timeout";
        public const string KubernetesApiRetry = "aspire.hosting.dcp.kubernetes_api.retry";
        public const string KubeconfigLockAcquired = "aspire.hosting.dcp.kubeconfig_lock_acquired";
        public const string KubeconfigReadComplete = "aspire.hosting.dcp.kubeconfig_read_complete";
        public const string KubernetesClientCreated = "aspire.hosting.dcp.kubernetes_client_created";
        public const string ResourceWaitObserved = "aspire.resource.wait.observed";
        public const string ResourceWaitCompleted = "aspire.resource.wait.completed";
        public const string ResourceWaitCancelled = "aspire.resource.wait.cancelled";
        public const string Exception = "exception";
    }

    internal static class Annotations
    {
        public const string OperationId = "aspire-startup-operation-id";
        public const string TraceParent = "aspire-startup-traceparent";
        public const string TraceState = "aspire-startup-tracestate";
    }

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    public static ActivityScope CurrentActivity => IsEnabled() ? new(Activity.Current, ownsActivity: false) : default;

    public static IEnumerable<KeyValuePair<string, object>> CreateAppHostResourceAttributes(string appHostPath, string operation)
    {
        return
        [
            new(Tags.AppHostName, Path.GetFileName(appHostPath)),
            new(Tags.AppHostOperation, operation)
        ];
    }

    public static ActivityScope StartDcpRunApplication(int resourceCount)
    {
        var activity = StartActivity(Activities.DcpRunApplication);
        activity.SetResourceCount(resourceCount);
        return activity;
    }

    public static ActivityScope StartDcpPrepareServices()
    {
        return StartActivity(Activities.DcpPrepareServices);
    }

    public static ActivityScope StartDcpPrepareResources()
    {
        return StartActivity(Activities.DcpPrepareResources);
    }

    public static ActivityScope StartDcpAllocateServiceAddresses(int serviceCount)
    {
        var activity = StartActivity(Activities.DcpAllocateServiceAddresses);
        activity.SetDcpServiceCount(serviceCount);
        return activity;
    }

    public static ActivityScope StartDcpCreateObjects(string resourceKind, int resourceCount)
    {
        var activity = StartActivity(Activities.DcpCreateObjects);
        activity.SetDcpResourceSet(resourceKind, resourceCount);
        return activity;
    }

    public static ActivityScope StartDcpCreateObject(string resourceKind, string resourceName)
    {
        var activity = StartActivity(Activities.DcpCreateObject);
        activity.SetDcpResource(resourceKind, resourceName);
        activity.SetDcpCreateObject(resourceKind, resourceName);
        return activity;
    }

    public static ActivityScope StartDcpCreateRenderedResources(string resourceKind, int resourceCount)
    {
        var activity = StartActivity(Activities.DcpCreateRenderedResources);
        activity.SetDcpResourceSet(resourceKind, resourceCount);
        return activity;
    }

    public static ActivityScope StartDcpCreateResourceReplica(IResource resource, string resourceKind, string resourceName)
    {
        var activity = StartActivity(Activities.DcpCreateResourceReplica);
        activity.SetResource(resource);
        activity.SetDcpResource(resourceKind, resourceName);
        return activity;
    }

    public static ActivityScope StartDcpEnsureKubernetesClient(bool kubeconfigExists)
    {
        var activity = StartActivity(Activities.DcpEnsureKubernetesClient);
        activity.SetDcpKubeconfigExists(kubeconfigExists);
        return activity;
    }

    public static ActivityScope StartDcpKubernetesApi(DcpApiOperationType operationType, string resourceType)
    {
        var activity = StartActivity(Activities.DcpKubernetesApi);
        activity.SetDcpKubernetesApi(operationType, resourceType);
        return activity;
    }

    public static ActivityScope StartDcpResourceObserved(
        IResource appModelResource,
        string resourceKind,
        string resourceName,
        string? state,
        DateTime? startupTimestamp,
        DateTime? finishedTimestamp,
        IDictionary<string, string>? annotations)
    {
        var activity = StartActivityFromTraceAnnotations(Activities.DcpResourceObserved, annotations);
        activity.SetResource(appModelResource);
        activity.SetDcpResource(resourceKind, resourceName);
        activity.SetDcpCreateObjectFromTraceAnnotations(resourceKind, resourceName, annotations);
        activity.SetResourceObserved(state, startupTimestamp, finishedTimestamp);
        return activity;
    }

    public static ActivityScope StartResourceBeforeStartWait(IResource resource)
    {
        var activity = StartActivity(Activities.ResourceBeforeStartWait);
        activity.SetResource(resource);
        return activity;
    }

    public static ActivityScope StartResourceCreate(IResource resource, string resourceKind, int replicaCount)
    {
        var activity = StartActivity(Activities.ResourceCreate);
        activity.SetResource(resource);
        activity.SetResourceCreate(resourceKind, replicaCount);
        return activity;
    }

    public static ActivityScope StartResourceStart(IResource resource, string resourceKind, string resourceName, string resourceType)
    {
        var activity = StartActivity(Activities.ResourceStart);
        activity.SetResource(resource);
        activity.SetDcpResource(resourceKind, resourceName);
        activity.SetResourceKind(resourceType);
        return activity;
    }

    public static ActivityScope StartResourceStop(IResource resource, string resourceKind, string resourceName)
    {
        var activity = StartActivity(Activities.ResourceStop);
        activity.SetResource(resource);
        activity.SetDcpResource(resourceKind, resourceName);
        return activity;
    }

    public static ActivityScope StartResourceWaitForDependencies(IResource resource, int dependencyCount)
    {
        var activity = StartActivity(Activities.ResourceWaitForDependencies);
        activity.SetResource(resource);
        activity.SetResourceWaitDependencyCount(dependencyCount);
        return activity;
    }

    public static ActivityScope StartResourceWaitForDependency(IResource resource, IResource dependency, WaitType waitType, WaitBehavior? waitBehavior)
    {
        var activity = StartActivity(Activities.ResourceWaitForDependency);
        activity.SetDependencyWait(resource, dependency, waitType, waitBehavior);
        return activity;
    }

    private static ActivityScope StartActivity(string name)
    {
        if (!IsEnabled())
        {
            return default;
        }

        var activity = Activity.Current is null && TryGetStartupParentContext(out var parentContext)
            ? s_activitySource.StartActivity(name, ActivityKind.Internal, parentContext)
            : s_activitySource.StartActivity(name, ActivityKind.Internal);

        AddStartupOperationId(activity);
        return new ActivityScope(activity);
    }

    private static ActivityScope StartActivityFromTraceAnnotations(string name, IDictionary<string, string>? annotations)
    {
        if (!IsEnabled())
        {
            return default;
        }

        Activity? activity = null;
        if (annotations is not null &&
            annotations.TryGetValue(Annotations.TraceParent, out var traceParent) &&
            !string.IsNullOrEmpty(traceParent))
        {
            // DCP annotations carry the create_object trace context to later watch/reconcile spans.
            annotations.TryGetValue(Annotations.TraceState, out var traceState);
            if (ActivityContext.TryParse(traceParent, traceState, out var parentContext))
            {
                activity = s_activitySource.StartActivity(name, ActivityKind.Internal, parentContext);
            }
        }

        if (activity is null)
        {
            return StartActivity(name);
        }

        AddStartupOperationId(activity, annotations);

        return new ActivityScope(activity);
    }

    private static void SetDcpCreateObjectTags(Activity activity, string resourceKind, string resourceName, string traceId, string spanId)
    {
        activity.SetTag(Tags.DcpCreateObjectId, $"{resourceKind}/{resourceName}");
        activity.SetTag(Tags.DcpCreateObjectKind, resourceKind);
        activity.SetTag(Tags.DcpCreateObjectName, resourceName);
        activity.SetTag(Tags.DcpCreateObjectTraceId, traceId);
        activity.SetTag(Tags.DcpCreateObjectSpanId, spanId);
    }

    private static void AddStartupOperationId(Activity? activity, IDictionary<string, string>? annotations = null)
    {
        if (activity is null)
        {
            return;
        }

        var operationId = annotations is not null && annotations.TryGetValue(Annotations.OperationId, out var annotationOperationId)
            ? annotationOperationId
            : Environment.GetEnvironmentVariable(KnownConfigNames.StartupOperationId);
        if (!string.IsNullOrEmpty(operationId))
        {
            activity.SetTag(Tags.OperationId, operationId);
        }
    }

    private static bool TryGetStartupParentContext(out ActivityContext parentContext)
    {
        var traceParent = Environment.GetEnvironmentVariable(KnownConfigNames.StartupTraceParent);
        var traceState = Environment.GetEnvironmentVariable(KnownConfigNames.StartupTraceState);
        if (string.IsNullOrEmpty(traceParent))
        {
            parentContext = default;
            return false;
        }

        return ActivityContext.TryParse(traceParent, traceState, out parentContext);
    }

    internal static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(KnownConfigNames.StartupProfilingEnabled);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    internal readonly struct ActivityScope(Activity? activity, bool ownsActivity = true) : IDisposable
    {
        public void AddDcpServiceAddressAllocated(string serviceName)
        {
            activity?.AddEvent(new ActivityEvent(Events.DcpServiceAddressAllocated, tags: new ActivityTagsCollection
            {
                [Tags.DcpServiceName] = serviceName
            }));
        }

        public void AddDcpServiceAddressAllocationFailed(string serviceName)
        {
            activity?.AddEvent(new ActivityEvent(Events.DcpServiceAddressAllocationFailed, tags: new ActivityTagsCollection
            {
                [Tags.DcpServiceName] = serviceName
            }));
        }

        public void AddKubeconfigLockAcquired() => AddEvent(Events.KubeconfigLockAcquired);

        public void AddKubeconfigReadComplete() => AddEvent(Events.KubeconfigReadComplete);

        public void AddKubernetesApiRetry(int attemptNumber, TimeSpan retryDelay, Exception? exception)
        {
            activity?.AddEvent(new ActivityEvent(Events.KubernetesApiRetry, tags: new ActivityTagsCollection
            {
                [Tags.DcpApiRetryAttempt] = attemptNumber,
                [Tags.DcpApiRetryDelayMilliseconds] = retryDelay.TotalMilliseconds,
                [Tags.ExceptionType] = exception?.GetType().FullName,
                [Tags.ExceptionMessage] = exception?.Message
            }));
        }

        public void AddKubernetesApiTimeout() => AddEvent(Events.KubernetesApiTimeout);

        public void AddKubernetesClientCreated() => AddEvent(Events.KubernetesClientCreated);

        public void AddResourceWaitCancelled(string resourceName, string waitCondition)
        {
            activity?.AddEvent(new ActivityEvent(Events.ResourceWaitCancelled, tags: new ActivityTagsCollection
            {
                [Tags.ResourceWaitTargetName] = resourceName,
                [Tags.ResourceWaitCondition] = waitCondition
            }));
        }

        public void AddResourceWaitCompleted(ResourceEvent resourceEvent, string waitCondition) =>
            AddResourceWaitEvent(Events.ResourceWaitCompleted, resourceEvent, waitCondition);

        public void AddResourceWaitObserved(ResourceEvent resourceEvent, string waitCondition) =>
            AddResourceWaitEvent(Events.ResourceWaitObserved, resourceEvent, waitCondition);

        public void AnnotateTraceContext(Action<string, string> annotate)
        {
            if (!IsEnabled())
            {
                return;
            }

            var operationId = Environment.GetEnvironmentVariable(KnownConfigNames.StartupOperationId);
            if (!string.IsNullOrEmpty(operationId))
            {
                annotate(Annotations.OperationId, operationId);
            }

            var traceParent = activity?.Id ?? Environment.GetEnvironmentVariable(KnownConfigNames.StartupTraceParent);
            if (!string.IsNullOrEmpty(traceParent))
            {
                annotate(Annotations.TraceParent, traceParent);
            }

            var traceState = activity?.TraceStateString ?? Environment.GetEnvironmentVariable(KnownConfigNames.StartupTraceState);
            if (!string.IsNullOrEmpty(traceState))
            {
                annotate(Annotations.TraceState, traceState);
            }
        }

        public void SetDcpCreateObject(string resourceKind, string resourceName)
        {
            if (activity is null)
            {
                return;
            }

            SetDcpCreateObjectTags(activity, resourceKind, resourceName, activity.TraceId.ToString(), activity.SpanId.ToString());
        }

        public void SetDcpCreateObjectFromTraceAnnotations(string resourceKind, string resourceName, IDictionary<string, string>? annotations)
        {
            if (activity is null)
            {
                return;
            }

            if (annotations is not null &&
                annotations.TryGetValue(Annotations.TraceParent, out var traceParent) &&
                ActivityContext.TryParse(traceParent, annotations.TryGetValue(Annotations.TraceState, out var traceState) ? traceState : null, out var createObjectContext))
            {
                SetDcpCreateObjectTags(activity, resourceKind, resourceName, createObjectContext.TraceId.ToString(), createObjectContext.SpanId.ToString());
            }
            else
            {
                SetDcpCreateObject(resourceKind, resourceName);
            }
        }

        public void SetDcpKubeconfigExists(bool exists) => SetTag(Tags.DcpKubeconfigExists, exists);

        public void SetDcpKubeconfigLockWait(long elapsedMilliseconds) => SetTag(Tags.DcpKubeconfigLockWaitMilliseconds, elapsedMilliseconds);

        public void SetDcpKubeconfigReadDuration(long elapsedMilliseconds) => SetTag(Tags.DcpKubeconfigReadDurationMilliseconds, elapsedMilliseconds);

        public void SetDcpKubernetesApi(DcpApiOperationType operationType, string resourceType)
        {
            SetTag(Tags.DcpApiOperation, operationType.ToString());
            SetTag(Tags.DcpResourceKind, resourceType);
        }

        public void SetDcpKubernetesClientAlreadyInitialized() => SetTag(Tags.DcpKubernetesClientAlreadyInitialized, true);

        public void SetDcpPreparedResourceCounts(int containerCount, int executableCount)
        {
            SetTag(Tags.DcpContainerCount, containerCount);
            SetTag(Tags.DcpExecutableCount, executableCount);
        }

        public void SetDcpResource(string resourceKind, string resourceName)
        {
            SetTag(Tags.DcpResourceKind, resourceKind);
            SetTag(Tags.DcpResourceName, resourceName);
        }

        public void SetDcpResourceSet(string resourceKind, int resourceCount)
        {
            SetTag(Tags.DcpResourceKind, resourceKind);
            SetTag(Tags.DcpResourceCount, resourceCount);
        }

        public void SetDcpServiceAllocatedCount(int count) => SetTag(Tags.DcpServiceAllocatedCount, count);

        public void SetDcpServiceCount(int count) => SetTag(Tags.DcpServiceCount, count);

        public void SetDcpApiRetryCount(int retryCount) => SetTag(Tags.DcpApiRetryCount, retryCount);

        public void SetDependencyWait(IResource resource, IResource dependency, WaitType waitType, WaitBehavior? waitBehavior)
        {
            SetResource(resource);
            SetTag(Tags.ResourceWaitType, waitType.ToString());
            SetTag(Tags.ResourceWaitDependencyName, dependency.Name);
            SetTag(Tags.ResourceWaitDependencyType, dependency.GetType().Name);
            SetTag(Tags.ResourceWaitBehavior, waitBehavior?.ToString());
        }

        public void SetError(Exception exception)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddEvent(new ActivityEvent(Events.Exception, tags: new ActivityTagsCollection
            {
                [Tags.ExceptionType] = exception.GetType().FullName,
                [Tags.ExceptionMessage] = exception.Message
            }));
        }

        public void SetResource(IResource resource)
        {
            SetTag(Tags.ResourceName, resource.Name);
            SetTag(Tags.ResourceType, resource.GetType().Name);
        }

        public void SetResourceCount(int count) => SetTag(Tags.ResourceCount, count);

        public void SetResourceCreate(string resourceKind, int replicaCount)
        {
            SetTag(Tags.ResourceKind, resourceKind);
            SetTag(Tags.ResourceReplicaCount, replicaCount);
        }

        public void SetResourceKind(string resourceKind) => SetTag(Tags.ResourceKind, resourceKind);

        public void SetResourceObserved(string? state, DateTime? startupTimestamp, DateTime? finishedTimestamp)
        {
            SetTag(Tags.ResourceState, state);
            SetTag(Tags.ResourceStartTime, startupTimestamp?.ToString("O", CultureInfo.InvariantCulture));
            SetTag(Tags.ResourceStopTime, finishedTimestamp?.ToString("O", CultureInfo.InvariantCulture));
        }

        public void SetResourceStopped(bool stopped) => SetTag(Tags.ResourceStopped, stopped);

        public void SetResourceWaitDependencyCount(int count) => SetTag(Tags.ResourceWaitDependencyCount, count);

        public void SetResourceWaitExpectedExitCode(int exitCode) => SetTag(Tags.ResourceWaitExpectedExitCode, exitCode);

        public void SetResourceWaitTarget(string resourceName, string waitCondition)
        {
            SetTag(Tags.ResourceWaitTargetName, resourceName);
            SetTag(Tags.ResourceWaitCondition, waitCondition);
        }

        public void Dispose()
        {
            if (ownsActivity)
            {
                activity?.Dispose();
            }
        }

        private void AddEvent(string name) => activity?.AddEvent(new ActivityEvent(name));

        private void AddResourceWaitEvent(string eventName, ResourceEvent resourceEvent, string waitCondition)
        {
            if (activity is null)
            {
                return;
            }

            var snapshot = resourceEvent.Snapshot;
            var tags = new ActivityTagsCollection
            {
                [Tags.ResourceName] = resourceEvent.Resource.Name,
                [Tags.ResourceId] = resourceEvent.ResourceId,
                [Tags.ResourceType] = snapshot.ResourceType,
                [Tags.ResourceWaitCondition] = waitCondition,
                [Tags.ResourceSnapshotVersion] = snapshot.Version,
                [Tags.ResourceReady] = snapshot.ResourceReadyEvent is not null
            };

            if (snapshot.State?.Text is { } state)
            {
                tags[Tags.ResourceState] = state;
            }

            if (snapshot.HealthStatus is { } healthStatus)
            {
                tags[Tags.ResourceHealthStatus] = healthStatus.ToString();
            }

            if (snapshot.ExitCode is { } exitCode)
            {
                tags[Tags.ResourceExitCode] = exitCode;
            }

            activity.AddEvent(new ActivityEvent(eventName, tags: tags));
        }

        private void SetTag(string key, object? value) => activity?.SetTag(key, value);
    }
}
