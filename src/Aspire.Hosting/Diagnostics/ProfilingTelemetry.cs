// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Diagnostics;

internal static class ProfilingTelemetry
{
    public const string ActivitySourceName = "Aspire.Hosting.Profiling";

    internal static class Activities
    {
        // Activity names describe AppHost/DCP orchestration work. Keep names stable
        // because profiling exports are queried across CLI and AppHost versions.
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
        // Tags capture dimensions and diagnostics for spans/events, such as resource
        // identity, DCP object identity, wait conditions, exit codes, and timing data.
        public const string ProfilingSessionId = "aspire.profiling.session_id";
        public const string LegacyStartupOperationId = "aspire.startup.operation_id";
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
        // Events mark important moments within longer spans, for example retries,
        // readiness observations, resource wait completions, and exception details.
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
        // DCP annotations carry profiling trace context through rendered resources so
        // later watch/reconcile notifications can reconnect to the resource creation span.
        public const string ProfilingSessionId = "aspire-profiling-session-id";
        public const string TraceParent = "aspire-profiling-traceparent";
        public const string TraceState = "aspire-profiling-tracestate";
        public const string LegacyStartupOperationId = "aspire-startup-operation-id";
        public const string LegacyStartupTraceParent = "aspire-startup-traceparent";
        public const string LegacyStartupTraceState = "aspire-startup-tracestate";
    }

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    public static ActivityScope CurrentActivity(IConfiguration? configuration) =>
        IsEnabled(configuration) ? new(Activity.Current, configuration, ownsActivity: false) : default;

    public static IEnumerable<KeyValuePair<string, object>> CreateAppHostResourceAttributes(string appHostPath, string operation)
    {
        return
        [
            new(Tags.AppHostName, Path.GetFileName(appHostPath)),
            new(Tags.AppHostOperation, operation)
        ];
    }

    public static ActivityScope StartDcpRunApplication(IConfiguration? configuration, int resourceCount)
    {
        var activity = StartActivity(configuration, Activities.DcpRunApplication);
        activity.SetResourceCount(resourceCount);
        return activity;
    }

    public static ActivityScope StartDcpPrepareServices(IConfiguration? configuration)
    {
        return StartActivity(configuration, Activities.DcpPrepareServices);
    }

    public static ActivityScope StartDcpPrepareResources(IConfiguration? configuration)
    {
        return StartActivity(configuration, Activities.DcpPrepareResources);
    }

    public static ActivityScope StartDcpAllocateServiceAddresses(IConfiguration? configuration, int serviceCount)
    {
        var activity = StartActivity(configuration, Activities.DcpAllocateServiceAddresses);
        activity.SetDcpServiceCount(serviceCount);
        return activity;
    }

    public static ActivityScope StartDcpCreateObjects(IConfiguration? configuration, string resourceKind, int resourceCount)
    {
        var activity = StartActivity(configuration, Activities.DcpCreateObjects);
        activity.SetDcpResourceSet(resourceKind, resourceCount);
        return activity;
    }

    public static ActivityScope StartDcpCreateObject(IConfiguration? configuration, string resourceKind, string resourceName)
    {
        var activity = StartActivity(configuration, Activities.DcpCreateObject);
        activity.SetDcpResource(resourceKind, resourceName);
        activity.SetDcpCreateObject(resourceKind, resourceName);
        return activity;
    }

    public static ActivityScope StartDcpCreateRenderedResources(IConfiguration? configuration, string resourceKind, int resourceCount)
    {
        var activity = StartActivity(configuration, Activities.DcpCreateRenderedResources);
        activity.SetDcpResourceSet(resourceKind, resourceCount);
        return activity;
    }

    public static ActivityScope StartDcpCreateResourceReplica(IConfiguration? configuration, IResource resource, string resourceKind, string resourceName)
    {
        var activity = StartActivity(configuration, Activities.DcpCreateResourceReplica);
        activity.SetResource(resource);
        activity.SetDcpResource(resourceKind, resourceName);
        return activity;
    }

    public static ActivityScope StartDcpEnsureKubernetesClient(IConfiguration? configuration, bool kubeconfigExists)
    {
        var activity = StartActivity(configuration, Activities.DcpEnsureKubernetesClient);
        activity.SetDcpKubeconfigExists(kubeconfigExists);
        return activity;
    }

    public static ActivityScope StartDcpKubernetesApi(IConfiguration? configuration, DcpApiOperationType operationType, string resourceType)
    {
        var activity = StartActivity(configuration, Activities.DcpKubernetesApi);
        activity.SetDcpKubernetesApi(operationType, resourceType);
        return activity;
    }

    public static ActivityScope StartDcpResourceObserved(
        IConfiguration? configuration,
        IResource appModelResource,
        string resourceKind,
        string resourceName,
        string? state,
        DateTime? startupTimestamp,
        DateTime? finishedTimestamp,
        IDictionary<string, string>? annotations)
    {
        var activity = StartActivityFromTraceAnnotations(configuration, Activities.DcpResourceObserved, annotations);
        activity.SetResource(appModelResource);
        activity.SetDcpResource(resourceKind, resourceName);
        activity.SetDcpCreateObjectFromTraceAnnotations(resourceKind, resourceName, annotations);
        activity.SetResourceObserved(state, startupTimestamp, finishedTimestamp);
        return activity;
    }

    public static ActivityScope StartResourceBeforeStartWait(IConfiguration? configuration, IResource resource)
    {
        var activity = StartActivity(configuration, Activities.ResourceBeforeStartWait);
        activity.SetResource(resource);
        return activity;
    }

    public static ActivityScope StartResourceCreate(IConfiguration? configuration, IResource resource, string resourceKind, int replicaCount)
    {
        var activity = StartActivity(configuration, Activities.ResourceCreate);
        activity.SetResource(resource);
        activity.SetResourceCreate(resourceKind, replicaCount);
        return activity;
    }

    public static ActivityScope StartResourceStart(IConfiguration? configuration, IResource resource, string resourceKind, string resourceName, string resourceType)
    {
        var activity = StartActivity(configuration, Activities.ResourceStart);
        activity.SetResource(resource);
        activity.SetDcpResource(resourceKind, resourceName);
        activity.SetResourceKind(resourceType);
        return activity;
    }

    public static ActivityScope StartResourceStop(IConfiguration? configuration, IResource resource, string resourceKind, string resourceName)
    {
        var activity = StartActivity(configuration, Activities.ResourceStop);
        activity.SetResource(resource);
        activity.SetDcpResource(resourceKind, resourceName);
        return activity;
    }

    public static ActivityScope StartResourceWaitForDependencies(IConfiguration? configuration, IResource resource, int dependencyCount)
    {
        var activity = StartActivity(configuration, Activities.ResourceWaitForDependencies);
        activity.SetResource(resource);
        activity.SetResourceWaitDependencyCount(dependencyCount);
        return activity;
    }

    public static ActivityScope StartResourceWaitForDependency(IConfiguration? configuration, IResource resource, IResource dependency, WaitType waitType, WaitBehavior? waitBehavior)
    {
        var activity = StartActivity(configuration, Activities.ResourceWaitForDependency);
        activity.SetDependencyWait(resource, dependency, waitType, waitBehavior);
        return activity;
    }

    private static ActivityScope StartActivity(IConfiguration? configuration, string name)
    {
        if (!IsEnabled(configuration))
        {
            return default;
        }

        var activity = Activity.Current is null && TryGetProfilingParentContext(configuration, out var parentContext)
            ? s_activitySource.StartActivity(name, ActivityKind.Internal, parentContext)
            : s_activitySource.StartActivity(name, ActivityKind.Internal);

        AddProfilingSessionId(activity, configuration);
        return new ActivityScope(activity, configuration);
    }

    private static ActivityScope StartActivityFromTraceAnnotations(IConfiguration? configuration, string name, IDictionary<string, string>? annotations)
    {
        if (!IsEnabled(configuration))
        {
            return default;
        }

        Activity? activity = null;
        if (annotations is not null &&
            TryGetAnnotation(annotations, Annotations.TraceParent, Annotations.LegacyStartupTraceParent, out var traceParent))
        {
            // DCP annotations carry the create_object trace context to later watch/reconcile spans.
            TryGetAnnotation(annotations, Annotations.TraceState, Annotations.LegacyStartupTraceState, out var traceState);
            if (ActivityContext.TryParse(traceParent, traceState, out var parentContext))
            {
                activity = s_activitySource.StartActivity(name, ActivityKind.Internal, parentContext);
            }
        }

        if (activity is null)
        {
            return StartActivity(configuration, name);
        }

        AddProfilingSessionId(activity, configuration, annotations);

        return new ActivityScope(activity, configuration);
    }

    private static void SetDcpCreateObjectTags(Activity activity, string resourceKind, string resourceName, string traceId, string spanId)
    {
        activity.SetTag(Tags.DcpCreateObjectId, $"{resourceKind}/{resourceName}");
        activity.SetTag(Tags.DcpCreateObjectKind, resourceKind);
        activity.SetTag(Tags.DcpCreateObjectName, resourceName);
        activity.SetTag(Tags.DcpCreateObjectTraceId, traceId);
        activity.SetTag(Tags.DcpCreateObjectSpanId, spanId);
    }

    private static void AddProfilingSessionId(Activity? activity, IConfiguration? configuration, IDictionary<string, string>? annotations = null)
    {
        if (activity is null)
        {
            return;
        }

        var sessionId = annotations is not null && TryGetAnnotation(annotations, Annotations.ProfilingSessionId, Annotations.LegacyStartupOperationId, out var annotationSessionId)
            ? annotationSessionId
            : GetConfigurationValue(configuration, KnownConfigNames.ProfilingSessionId, KnownConfigNames.Legacy.StartupOperationId);
        if (!string.IsNullOrEmpty(sessionId))
        {
            activity.SetTag(Tags.ProfilingSessionId, sessionId);
            activity.SetTag(Tags.LegacyStartupOperationId, sessionId);
        }
    }

    private static bool TryGetProfilingParentContext(IConfiguration? configuration, out ActivityContext parentContext)
    {
        var traceParent = GetConfigurationValue(configuration, KnownConfigNames.ProfilingTraceParent, KnownConfigNames.Legacy.StartupTraceParent);
        var traceState = GetConfigurationValue(configuration, KnownConfigNames.ProfilingTraceState, KnownConfigNames.Legacy.StartupTraceState);
        if (string.IsNullOrEmpty(traceParent))
        {
            parentContext = default;
            return false;
        }

        return ActivityContext.TryParse(traceParent, traceState, out parentContext);
    }

    internal static bool IsEnabled(IConfiguration? configuration)
    {
        return IsTruthy(configuration?[KnownConfigNames.ProfilingEnabled]) ||
            IsTruthy(configuration?[KnownConfigNames.Legacy.StartupProfilingEnabled]);
    }

    private static bool TryGetAnnotation(IDictionary<string, string> annotations, string name, string legacyName, out string? value)
    {
        if (annotations.TryGetValue(name, out value) && !string.IsNullOrEmpty(value))
        {
            return true;
        }

        return annotations.TryGetValue(legacyName, out value) && !string.IsNullOrEmpty(value);
    }

    private static string? GetConfigurationValue(IConfiguration? configuration, string name, string legacyName)
    {
        return configuration?[name] is { Length: > 0 } value
            ? value
            : configuration?[legacyName];
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    internal readonly struct ActivityScope(Activity? activity, IConfiguration? configuration = null, bool ownsActivity = true) : IDisposable
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
            if (!IsEnabled(configuration))
            {
                return;
            }

            var sessionId = GetConfigurationValue(configuration, KnownConfigNames.ProfilingSessionId, KnownConfigNames.Legacy.StartupOperationId);
            if (!string.IsNullOrEmpty(sessionId))
            {
                annotate(Annotations.ProfilingSessionId, sessionId);
                annotate(Annotations.LegacyStartupOperationId, sessionId);
            }

            var traceParent = activity?.Id ?? GetConfigurationValue(configuration, KnownConfigNames.ProfilingTraceParent, KnownConfigNames.Legacy.StartupTraceParent);
            if (!string.IsNullOrEmpty(traceParent))
            {
                annotate(Annotations.TraceParent, traceParent);
                annotate(Annotations.LegacyStartupTraceParent, traceParent);
            }

            var traceState = activity?.TraceStateString ?? GetConfigurationValue(configuration, KnownConfigNames.ProfilingTraceState, KnownConfigNames.Legacy.StartupTraceState);
            if (!string.IsNullOrEmpty(traceState))
            {
                annotate(Annotations.TraceState, traceState);
                annotate(Annotations.LegacyStartupTraceState, traceState);
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
                TryGetAnnotation(annotations, Annotations.TraceParent, Annotations.LegacyStartupTraceParent, out var traceParent) &&
                ActivityContext.TryParse(
                    traceParent,
                    TryGetAnnotation(annotations, Annotations.TraceState, Annotations.LegacyStartupTraceState, out var traceState) ? traceState : null,
                    out var createObjectContext))
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
