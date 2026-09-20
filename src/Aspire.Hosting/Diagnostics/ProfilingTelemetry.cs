// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace Aspire.Hosting.Diagnostics;

internal sealed class ProfilingTelemetry(IConfiguration configuration)
{
    public const string ActivitySourceName = "Aspire.Hosting.Profiling";

    internal static class Activities
    {
        // Activity names describe AppHost/DCP orchestration work. Keep names stable
        // because profiling exports are queried across CLI and AppHost versions.
        public const string DcpRunApplication = "aspire.hosting.dcp.run_application";
        public const string DcpPrepareTlsCertificate = "aspire.hosting.dcp.prepare_tls_certificate";
        public const string AppHostProcessStartup = "aspire.hosting.apphost.process_startup";
        public const string AppHostStart = "aspire.hosting.apphost.start";
        public const string AppHostBeforeStart = "aspire.hosting.apphost.before_start";
        public const string AppHostEventingSubscribers = "aspire.hosting.apphost.eventing_subscribers";
        public const string AppHostEventingSubscriber = "aspire.hosting.apphost.eventing_subscriber";
        public const string AppHostPublishEvent = "aspire.hosting.apphost.publish_event";
        public const string AppHostEventCallback = "aspire.hosting.apphost.event_callback";
        public const string AppHostLifecycleHooks = "aspire.hosting.apphost.lifecycle_hooks";
        public const string AppHostLifecycleHook = "aspire.hosting.apphost.lifecycle_hook";
        public const string AppHostBeforeStartPipeline = "aspire.hosting.apphost.before_start_pipeline";
        public const string AppHostHostStartup = "aspire.hosting.apphost.host_startup";
        public const string DcpPrepareServices = "aspire.hosting.dcp.prepare_services";
        public const string DcpPrepareResources = "aspire.hosting.dcp.prepare_resources";
        public const string DcpAllocateServiceAddresses = "aspire.hosting.dcp.allocate_service_addresses";
        public const string ResourceCreate = "aspire.hosting.resource.create";
        public const string DcpKubernetesApi = "aspire.hosting.dcp.kubernetes_api";
        public const string DcpEnsureKubernetesClient = "aspire.hosting.dcp.ensure_kubernetes_client";
        public const string DcpResourceObserved = "aspire.hosting.dcp.resource_observed";
        public const string ResourceStartup = "aspire.hosting.resource.startup";
        public const string ResourceBeforeStartWait = "aspire.hosting.resource.before_start_wait";
        public const string ResourceWaitForDependency = "aspire.hosting.resource.wait_for_dependency";
        public const string ResourceWaitForDependencies = "aspire.hosting.resource.wait_for_dependencies";
        public const string ResourceStop = "aspire.hosting.resource.stop";
        public const string ResourceStart = "aspire.hosting.resource.start";
        public const string BackchannelStartup = "aspire.hosting.backchannel.startup";
        public const string JsonRpcServerCall = "aspire.hosting.jsonrpc.server";
        public const string DashboardGetConnectionInfo = "aspire.hosting.dashboard.get_connection_info";
        public const string DashboardWaitHealthy = "aspire.hosting.dashboard.wait_healthy";
        public const string DashboardResolveUrls = "aspire.hosting.dashboard.resolve_urls";
    }

    internal static class Tags
    {
        // Tags capture dimensions and diagnostics for spans/events, such as resource
        // identity, DCP object identity, wait conditions, exit codes, and timing data.
        public const string ProfilingSessionId = "aspire.profiling.session_id";
        public const string LegacyStartupOperationId = "aspire.startup.operation_id";
        public const string AppHostName = "aspire.apphost.name";
        public const string AppHostOperation = "aspire.apphost.operation";
        public const string AppHostEntryPoint = "aspire.apphost.entry_point";
        public const string AppHostEventType = "aspire.apphost.event.type";
        public const string AppHostEventDispatchBehavior = "aspire.apphost.event.dispatch_behavior";
        public const string AppHostEventSubscriberCount = "aspire.apphost.event.subscriber_count";
        public const string AppHostLifecycleHookCount = "aspire.apphost.lifecycle_hook_count";
        public const string AppHostComponentType = "aspire.apphost.component.type";
        public const string AppHostPipelineStep = "aspire.apphost.pipeline.step";
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
        public const string DcpKubeconfigFileWaitMilliseconds = "aspire.dcp.kubeconfig.file_wait_ms";
        public const string DcpKubeconfigBuildDurationMilliseconds = "aspire.dcp.kubeconfig.build_duration_ms";
        public const string DcpKubeconfigReadDurationMilliseconds = "aspire.dcp.kubeconfig.read_duration_ms";
        public const string DcpKubernetesClientWaitMilliseconds = "aspire.dcp.kubernetes_client.wait_ms";
        public const string DcpKubernetesClientAlreadyInitialized = "aspire.dcp.kubernetes_client_already_initialized";
        public const string DcpKubernetesClientInitialized = "aspire.dcp.kubernetes_client.initialized";
        public const string DcpTlsDeveloperCertificateEnabled = "aspire.dcp.tls.developer_certificate.enabled";
        public const string DcpTlsCertificateMode = "aspire.dcp.tls.certificate.mode";
        public const string DcpTlsCertificatePrepared = "aspire.dcp.tls.certificate.prepared";
        public const string DcpTlsCertificateResult = "aspire.dcp.tls.certificate.result";
        public const string BackchannelSocketPath = "aspire.hosting.backchannel.socket.path";
        public const string PreviousResourceState = "aspire.resource.previous_state";
        public const string PreviousResourceHealthStatus = "aspire.resource.previous_health_status";
        public const string JsonRpcMethod = "rpc.method";
        public const string JsonRpcStreaming = "aspire.hosting.jsonrpc.streaming";
        public const string JsonRpcStreamItemCount = "aspire.hosting.jsonrpc.stream.item_count";
        public const string DashboardHealthy = "aspire.hosting.dashboard.healthy";
        public const string DashboardUrlSource = "aspire.hosting.dashboard.url.source";
        public const string DashboardHasApiBaseUrl = "aspire.hosting.dashboard.api_base_url.exists";
        public const string ExceptionType = "exception.type";
        public const string ExceptionMessage = "exception.message";
    }

    internal static class Events
    {
        // Events mark important moments within longer spans, for example retries,
        // readiness observations, resource wait completions, and exception details.
        public const string DcpServiceAddressAllocated = "aspire.dcp.service_address_allocated";
        public const string DcpServiceAddressAllocationFailed = "aspire.dcp.service_address_allocation_failed";
        public const string AppHostCreateBuilderEntered = "aspire.hosting.apphost.create_builder_entered";
        public const string AppHostBuilderConstructing = "aspire.hosting.apphost.builder_constructing";
        public const string AppHostBuilderConstructed = "aspire.hosting.apphost.builder_constructed";
        public const string AppHostBuildStarted = "aspire.hosting.apphost.build_started";
        public const string AppHostBuildCompleted = "aspire.hosting.apphost.build_completed";
        public const string AppHostStartAsyncEntered = "aspire.hosting.apphost.start_async_entered";
        public const string AppHostRunAsyncEntered = "aspire.hosting.apphost.run_async_entered";
        public const string AppHostHostStarting = "aspire.hosting.apphost.host_starting";
        public const string AppHostHostStarted = "aspire.hosting.apphost.host_started";
        public const string KubernetesApiTimeout = "aspire.hosting.dcp.kubernetes_api.timeout";
        public const string KubernetesApiRetry = "aspire.hosting.dcp.kubernetes_api.retry";
        public const string KubeconfigLockAcquired = "aspire.hosting.dcp.kubeconfig_lock_acquired";
        public const string KubeconfigFileDetected = "aspire.hosting.dcp.kubeconfig_file_detected";
        public const string KubeconfigReadComplete = "aspire.hosting.dcp.kubeconfig_read_complete";
        public const string KubernetesClientCreated = "aspire.hosting.dcp.kubernetes_client_created";
        public const string KubernetesClientReady = "aspire.hosting.dcp.kubernetes_client_ready";
        public const string ResourceStartupObserved = "aspire.resource.startup.observed";
        public const string ResourceStartupStateChanged = "aspire.resource.startup.state_changed";
        public const string ResourceStartupHealthChanged = "aspire.resource.startup.health_changed";
        public const string ResourceStartupReady = "aspire.resource.startup.ready";
        public const string ResourceWaitObserved = "aspire.resource.wait.observed";
        public const string ResourceWaitCompleted = "aspire.resource.wait.completed";
        public const string ResourceWaitCancelled = "aspire.resource.wait.cancelled";
        public const string BackchannelSocketDeleted = "aspire.hosting.backchannel.socket_deleted";
        public const string BackchannelListening = "aspire.hosting.backchannel.listening";
        public const string BackchannelReadyPublished = "aspire.hosting.backchannel.ready_published";
        public const string BackchannelClientAccepted = "aspire.hosting.backchannel.client_accepted";
        public const string BackchannelRpcListening = "aspire.hosting.backchannel.rpc_listening";
        public const string BackchannelConnectedPublished = "aspire.hosting.backchannel.connected_published";
        public const string Exception = "exception";
        public const string JsonRpcStreamFirstItem = "aspire.hosting.jsonrpc.stream.first_item";
        public const string JsonRpcStreamCompleted = "aspire.hosting.jsonrpc.stream.completed";
        public const string DashboardWaitHealthyCompleted = "aspire.hosting.dashboard.wait_healthy.completed";
        public const string DashboardWaitHealthyFailed = "aspire.hosting.dashboard.wait_healthy.failed";
    }

    internal static class Values
    {
        public const string DashboardUrlSourceNone = "none";
        public const string DashboardUrlSourceResource = "resource";
        public const string DashboardUrlSourceConfiguration = "configuration";
        public const string DcpTlsCertificateModeFiles = "files";
        public const string DcpTlsCertificateModeThumbprint = "thumbprint";
        public const string DcpTlsCertificateResultMissingThumbprint = "missing_thumbprint";
        public const string DcpTlsCertificateResultNoCertificate = "no_certificate";
        public const string DcpTlsCertificateResultNoPrivateKeyCertificate = "no_private_key_certificate";
        public const string DcpTlsCertificateResultPrepared = "prepared";
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

    internal static ActivitySource ActivitySource => s_activitySource;

    private static readonly ConcurrentQueue<AppHostStartupEvent> s_startupEvents = new();

    private readonly IConfiguration _configuration = configuration;

    // These static helpers are used before ProfilingTelemetry can be resolved from DI. Some startup
    // milestones occur before HostApplicationBuilder has created IConfiguration or before OpenTelemetry
    // has built the TracerProvider, so buffer timestamps here and attach them later to the process-start
    // span without forcing the full telemetry stack to initialize too soon.
    public static void RecordAppHostStartupEvent(string eventName, IConfiguration? configuration = null)
    {
        // CreateBuilder-entered events happen before IConfiguration exists. Later startup phases
        // pass configuration explicitly so command-line/config providers are honored as soon as
        // they are available.
        if (!IsStartupEventRecordingEnabled(configuration))
        {
            return;
        }

        s_startupEvents.Enqueue(new AppHostStartupEvent(eventName, DateTimeOffset.UtcNow));
    }

    public static ActivityScope StartAppHostProcessStartup(IConfiguration? configuration)
    {
        if (!IsEnabled(configuration))
        {
            return default;
        }

        var activity = StartActivity(configuration, Activities.AppHostProcessStartup, ActivityKind.Internal, GetProcessStartTime());
        activity.AddAppHostStartupEvents(DrainAppHostStartupEvents());
        return activity;
    }

    public static void RecordAppHostProcessStartup(IConfiguration? configuration)
    {
        using var activity = StartAppHostProcessStartup(configuration);
    }

    public static void EnsureInitialized(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (IsEnabled(configuration))
        {
            // OpenTelemetry.Extensions.Hosting normally builds the TracerProvider when the host starts.
            // Before-start hooks run before that, so force provider construction when self-profiling is
            // enabled or the spans that explain pre-hosted-service startup work would be dropped.
            _ = services.GetService<TracerProvider>();
        }
    }

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

    public static ActivityScope StartDcpPrepareTlsCertificate(IConfiguration? configuration)
    {
        var activity = StartActivity(configuration, Activities.DcpPrepareTlsCertificate);
        activity.SetDcpTlsDeveloperCertificateEnabled(true);
        return activity;
    }

    public static ActivityScope StartAppHostStart(IConfiguration? configuration, string entryPoint)
    {
        var activity = StartActivity(configuration, Activities.AppHostStart);
        activity.SetAppHostEntryPoint(entryPoint);
        return activity;
    }

    public static ActivityScope StartAppHostBeforeStart(IConfiguration? configuration)
    {
        return StartActivity(configuration, Activities.AppHostBeforeStart);
    }

    public static ActivityScope StartAppHostEventingSubscribers(IConfiguration? configuration, int subscriberCount)
    {
        var activity = StartActivity(configuration, Activities.AppHostEventingSubscribers);
        activity.SetAppHostEventSubscriberCount(subscriberCount);
        return activity;
    }

    public static ActivityScope StartAppHostEventingSubscriber(IConfiguration? configuration, Type subscriberType)
    {
        var activity = StartActivity(configuration, Activities.AppHostEventingSubscriber);
        activity.SetAppHostComponentType(subscriberType);
        return activity;
    }

    public static ActivityScope StartAppHostPublishEvent(IConfiguration? configuration, Type eventType)
    {
        var activity = StartActivity(configuration, Activities.AppHostPublishEvent);
        activity.SetAppHostEvent(eventType);
        return activity;
    }

    public static ActivityScope StartAppHostEventCallback(Type eventType, string dispatchBehavior)
    {
        var parentActivity = Activity.Current;
        if (parentActivity?.Source.Name != ActivitySourceName ||
            parentActivity.OperationName != Activities.AppHostPublishEvent)
        {
            return default;
        }

        var activity = s_activitySource.StartActivity(Activities.AppHostEventCallback);
        AddProfilingSessionId(activity, parentActivity.GetBaggageItem(Tags.ProfilingSessionId));
        var scope = new ActivityScope(activity);
        scope.SetAppHostEvent(eventType, dispatchBehavior);
        return scope;
    }

    public static ActivityScope StartAppHostLifecycleHooks(IConfiguration? configuration, int lifecycleHookCount)
    {
        var activity = StartActivity(configuration, Activities.AppHostLifecycleHooks);
        activity.SetAppHostLifecycleHookCount(lifecycleHookCount);
        return activity;
    }

    public static ActivityScope StartAppHostLifecycleHook(IConfiguration? configuration, Type lifecycleHookType)
    {
        var activity = StartActivity(configuration, Activities.AppHostLifecycleHook);
        activity.SetAppHostComponentType(lifecycleHookType);
        return activity;
    }

    public static ActivityScope StartAppHostBeforeStartPipeline(IConfiguration? configuration, string pipelineStep)
    {
        var activity = StartActivity(configuration, Activities.AppHostBeforeStartPipeline);
        activity.SetAppHostPipelineStep(pipelineStep);
        return activity;
    }

    public ActivityScope StartAppHostHostStartup()
    {
        return StartActivity(Activities.AppHostHostStartup);
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

    public ActivityScope StartDcpResourceObserved(
        IResource appModelResource,
        string resourceKind,
        string resourceName,
        string? state,
        DateTime? startupTimestamp,
        DateTime? finishedTimestamp,
        IDictionary<string, string>? annotations)
    {
        // Resource observations arrive from DCP watch notifications after the outbound Kubernetes create call
        // has ended, so use a short child activity from the propagated profiling trace context.
        var activity = StartActivityFromTraceAnnotations(Activities.DcpResourceObserved, annotations);
        activity.SetResource(appModelResource);
        activity.SetDcpResource(resourceKind, resourceName);
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

    public static ActivityScope StartResourceStartup(
        IConfiguration? configuration,
        IResource resource,
        string resourceId,
        CustomResourceSnapshot snapshot,
        DateTimeOffset startTime)
    {
        var activity = StartActivity(configuration, $"{Activities.ResourceStartup} {resource.Name}", ActivityKind.Internal, startTime);
        activity.SetResourceLifecycle(resource, resourceId, snapshot);
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

    public ActivityScope StartBackchannelStartup(string socketPath)
    {
        var activity = StartActivity(Activities.BackchannelStartup);
        activity.SetBackchannelSocketPath(socketPath);
        return activity;
    }

    public ActivityScope StartJsonRpcServerCall(string methodName, bool streaming, BackchannelTraceContext? traceContext = null)
    {
        var activity = StartActivityFromTraceContext(Activities.JsonRpcServerCall, ActivityKind.Server, traceContext);
        activity.SetJsonRpcCall(methodName, streaming);
        return activity;
    }

    public ActivityScope StartDashboardGetConnectionInfo()
    {
        return StartActivity(Activities.DashboardGetConnectionInfo);
    }

    public ActivityScope StartDashboardWaitHealthy()
    {
        return StartActivity(Activities.DashboardWaitHealthy);
    }

    public ActivityScope StartDashboardResolveUrls()
    {
        return StartActivity(Activities.DashboardResolveUrls);
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

    private static ActivityScope StartActivity(IConfiguration? configuration, string name, ActivityKind activityKind = ActivityKind.Internal)
    {
        if (!IsEnabled(configuration))
        {
            return default;
        }

        var activity = (Activity.Current is null || Activity.Current.Source.Name != ActivitySourceName) &&
            TryGetProfilingParentContext(configuration, out var parentContext)
            ? s_activitySource.StartActivity(name, activityKind, parentContext)
            : s_activitySource.StartActivity(name, activityKind);

        AddProfilingSessionId(activity, configuration);
        return new ActivityScope(activity, configuration);
    }

    private static ActivityScope StartActivity(IConfiguration? configuration, string name, ActivityKind activityKind, DateTimeOffset startTime)
    {
        if (!IsEnabled(configuration))
        {
            return default;
        }

        Activity? activity;
        if (TryGetProfilingParentContext(configuration, out var parentContext))
        {
            // Backdated lifecycle spans summarize async resource state changes and can start before the
            // short-lived publish/update span that observes the milestone. Keep them under the profiling
            // root instead of making them children of an ambient activity that may start later.
            activity = s_activitySource.StartActivity(name, activityKind, parentContext, tags: null, links: null, startTime: startTime);
        }
        else if (Activity.Current is { } currentActivity)
        {
            activity = s_activitySource.StartActivity(name, activityKind, currentActivity.Context, tags: null, links: null, startTime: startTime);
        }
        else
        {
            activity = s_activitySource.StartActivity(name, activityKind, parentContext: default, tags: null, links: null, startTime: startTime);
        }

        AddProfilingSessionId(activity, configuration);
        return new ActivityScope(activity, configuration);
    }

    private ActivityScope StartActivity(string name, ActivityKind activityKind = ActivityKind.Internal)
    {
        return StartActivity(_configuration, name, activityKind);
    }

    private ActivityScope StartActivityFromTraceAnnotations(string name, IDictionary<string, string>? annotations)
    {
        if (!IsEnabled(_configuration))
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
            return StartActivity(name);
        }

        AddProfilingSessionId(activity, _configuration, annotations);

        return new ActivityScope(activity, _configuration);
    }

    private ActivityScope StartActivityFromTraceContext(string name, ActivityKind activityKind, BackchannelTraceContext? traceContext)
    {
        if (!IsEnabled(_configuration))
        {
            return default;
        }

        Activity? activity;
        if (TryGetBackchannelTraceParent(traceContext, out var traceContextParent))
        {
            activity = s_activitySource.StartActivity(name, activityKind, traceContextParent);
        }
        else if (TryGetAmbientRemoteParentContext(out var ambientParent))
        {
            // StreamJsonRpc's ActivityTracingStrategy creates an unexported server activity
            // from the caller's W3C traceparent. Parent profiling spans to the remote caller
            // instead of that hidden activity so exported CLI and Hosting spans are adjacent.
            activity = s_activitySource.StartActivity(name, activityKind, ambientParent);
        }
        else if ((Activity.Current is null || Activity.Current.Source.Name != ActivitySourceName) &&
            TryGetProfilingParentContext(_configuration, out var configuredParent))
        {
            activity = s_activitySource.StartActivity(name, activityKind, configuredParent);
        }
        else
        {
            activity = s_activitySource.StartActivity(name, activityKind);
        }

        AddBaggage(activity, traceContext);
        AddProfilingSessionId(activity, _configuration, traceContext);
        return new ActivityScope(activity, _configuration);
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
            activity.SetBaggage(Tags.ProfilingSessionId, sessionId);
            activity.SetTag(Tags.ProfilingSessionId, sessionId);
            activity.SetTag(Tags.LegacyStartupOperationId, sessionId);
        }
    }

    private static void AddProfilingSessionId(Activity? activity, string? sessionId)
    {
        if (activity is null || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        activity.SetBaggage(Tags.ProfilingSessionId, sessionId);
        activity.SetTag(Tags.ProfilingSessionId, sessionId);
        activity.SetTag(Tags.LegacyStartupOperationId, sessionId);
    }

    private static void AddBaggage(Activity? activity, BackchannelTraceContext? traceContext)
    {
        if (activity is null || traceContext is null)
        {
            return;
        }

        foreach (var (key, value) in traceContext.Baggage)
        {
            activity.SetBaggage(key, value);
        }
    }

    private static void AddProfilingSessionId(Activity? activity, IConfiguration? configuration, BackchannelTraceContext? traceContext)
    {
        if (activity is null)
        {
            return;
        }

        var sessionId = traceContext?.Baggage.TryGetValue(Tags.ProfilingSessionId, out var baggageSessionId) == true && !string.IsNullOrEmpty(baggageSessionId)
            ? baggageSessionId
            : GetConfigurationValue(configuration, KnownConfigNames.ProfilingSessionId, KnownConfigNames.Legacy.StartupOperationId);
        if (!string.IsNullOrEmpty(sessionId))
        {
            activity.SetBaggage(Tags.ProfilingSessionId, sessionId);
            activity.SetTag(Tags.ProfilingSessionId, sessionId);
            activity.SetTag(Tags.LegacyStartupOperationId, sessionId);
        }
    }

    private static bool TryGetBackchannelTraceParent(BackchannelTraceContext? traceContext, out ActivityContext parentContext)
    {
        if (!string.IsNullOrEmpty(traceContext?.TraceParent) &&
            ActivityContext.TryParse(traceContext.TraceParent, traceContext.TraceState, out parentContext))
        {
            return true;
        }

        parentContext = default;
        return false;
    }

    private static bool TryGetAmbientRemoteParentContext(out ActivityContext parentContext)
    {
        var ambientActivity = Activity.Current;
        if (ambientActivity is not null &&
            ambientActivity.Source.Name != ActivitySourceName &&
            ambientActivity.Parent is null &&
            ambientActivity.ParentSpanId != default)
        {
            parentContext = new ActivityContext(
                ambientActivity.TraceId,
                ambientActivity.ParentSpanId,
                ambientActivity.ActivityTraceFlags,
                ambientActivity.TraceStateString,
                isRemote: true);
            return true;
        }

        parentContext = default;
        return false;
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
        return configuration?.GetBool(KnownConfigNames.ProfilingEnabled, KnownConfigNames.Legacy.StartupProfilingEnabled) is true;
    }

    private static bool IsStartupEventRecordingEnabled(IConfiguration? configuration)
    {
        if (configuration is not null)
        {
            return IsEnabled(configuration);
        }

        return IsTruthy(Environment.GetEnvironmentVariable(KnownConfigNames.ProfilingEnabled)) ||
            IsTruthy(Environment.GetEnvironmentVariable(KnownConfigNames.Legacy.StartupProfilingEnabled));
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

    private static DateTimeOffset GetProcessStartTime()
    {
        return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    private static AppHostStartupEvent[] DrainAppHostStartupEvents()
    {
        // Move the pre-DI startup timestamps into the process-start activity exactly once. The queue
        // is static because the earliest records happen before DI is available, but draining here
        // prevents the same milestones from leaking into later AppHost instances in this process.
        // ConcurrentQueue.TryDequeue atomically claims each event, so callers that race with the
        // drain do not need a separate lock.
        if (s_startupEvents.IsEmpty)
        {
            return [];
        }

        var events = new List<AppHostStartupEvent>();
        while (s_startupEvents.TryDequeue(out var startupEvent))
        {
            events.Add(startupEvent);
        }

        return events.ToArray();
    }

    internal readonly record struct AppHostStartupEvent(string Name, DateTimeOffset Timestamp);

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

        public void AddAppHostHostStarting() => AddEvent(Events.AppHostHostStarting);

        public void AddAppHostHostStarted() => AddEvent(Events.AppHostHostStarted);

        internal void AddAppHostStartupEvents(ReadOnlySpan<AppHostStartupEvent> events)
        {
            if (activity is null)
            {
                return;
            }

            foreach (var startupEvent in events)
            {
                activity.AddEvent(new ActivityEvent(startupEvent.Name, startupEvent.Timestamp));
            }
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

        public void AddJsonRpcStreamFirstItemEvent() => AddEvent(Events.JsonRpcStreamFirstItem);

        public void AddJsonRpcStreamCompletedEvent() => AddEvent(Events.JsonRpcStreamCompleted);

        public void AddDashboardWaitHealthyCompleted() => AddEvent(Events.DashboardWaitHealthyCompleted);

        public void AddDashboardWaitHealthyFailed() => AddEvent(Events.DashboardWaitHealthyFailed);

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

        public void SetDcpKubeconfigExists(bool exists) => SetTag(Tags.DcpKubeconfigExists, exists);

        public void SetDcpKubeconfigLockWait(long elapsedMilliseconds) => SetTag(Tags.DcpKubeconfigLockWaitMilliseconds, elapsedMilliseconds);

        public void SetDcpKubeconfigFileWait(long elapsedMilliseconds) => SetTag(Tags.DcpKubeconfigFileWaitMilliseconds, elapsedMilliseconds);

        public void SetDcpKubeconfigBuildDuration(long elapsedMilliseconds) => SetTag(Tags.DcpKubeconfigBuildDurationMilliseconds, elapsedMilliseconds);

        public void SetDcpKubeconfigReadDuration(long elapsedMilliseconds) => SetTag(Tags.DcpKubeconfigReadDurationMilliseconds, elapsedMilliseconds);

        public void AddKubeconfigFileDetected() => AddEvent(Events.KubeconfigFileDetected);

        public void AddKubernetesClientReady(long waitMilliseconds, bool initialized)
        {
            SetTag(Tags.DcpKubernetesClientWaitMilliseconds, waitMilliseconds);
            SetTag(Tags.DcpKubernetesClientInitialized, initialized);
            activity?.AddEvent(new ActivityEvent(Events.KubernetesClientReady, tags: new ActivityTagsCollection
            {
                [Tags.DcpKubernetesClientWaitMilliseconds] = waitMilliseconds,
                [Tags.DcpKubernetesClientInitialized] = initialized
            }));
        }

        public void SetDcpKubernetesApi(DcpApiOperationType operationType, string resourceType)
        {
            SetTag(Tags.DcpApiOperation, operationType.ToString());
            SetTag(Tags.DcpResourceKind, resourceType);
        }

        public void SetDcpTlsDeveloperCertificateEnabled(bool enabled) => SetTag(Tags.DcpTlsDeveloperCertificateEnabled, enabled);

        public void SetDcpTlsCertificateResult(string result, string? mode = null, bool prepared = false)
        {
            SetTag(Tags.DcpTlsCertificateResult, result);
            SetTag(Tags.DcpTlsCertificatePrepared, prepared);

            if (!string.IsNullOrEmpty(mode))
            {
                SetTag(Tags.DcpTlsCertificateMode, mode);
            }
        }

        public void SetAppHostEntryPoint(string entryPoint) => SetTag(Tags.AppHostEntryPoint, entryPoint);

        public void SetAppHostEventSubscriberCount(int subscriberCount) => SetTag(Tags.AppHostEventSubscriberCount, subscriberCount);

        public void SetAppHostLifecycleHookCount(int lifecycleHookCount) => SetTag(Tags.AppHostLifecycleHookCount, lifecycleHookCount);

        public void SetAppHostPipelineStep(string pipelineStep) => SetTag(Tags.AppHostPipelineStep, pipelineStep);

        public void SetAppHostComponentType(Type componentType) => SetTag(Tags.AppHostComponentType, componentType.FullName);

        public void SetAppHostEvent(Type eventType, string? dispatchBehavior = null)
        {
            SetTag(Tags.AppHostEventType, eventType.FullName);
            SetTag(Tags.AppHostEventDispatchBehavior, dispatchBehavior);
        }

        public void SetDcpKubernetesClientAlreadyInitialized() => SetTag(Tags.DcpKubernetesClientAlreadyInitialized, true);

        public void SetBackchannelSocketPath(string socketPath) => SetTag(Tags.BackchannelSocketPath, socketPath);

        public void AddBackchannelSocketDeleted() => AddEvent(Events.BackchannelSocketDeleted);

        public void AddBackchannelListening() => AddEvent(Events.BackchannelListening);

        public void AddBackchannelReadyPublished() => AddEvent(Events.BackchannelReadyPublished);

        public void AddBackchannelClientAccepted() => AddEvent(Events.BackchannelClientAccepted);

        public void AddBackchannelRpcListening() => AddEvent(Events.BackchannelRpcListening);

        public void AddBackchannelConnectedPublished() => AddEvent(Events.BackchannelConnectedPublished);

        public void SetJsonRpcCall(string methodName, bool streaming)
        {
            SetTag(Tags.JsonRpcMethod, methodName);
            SetTag(Tags.JsonRpcStreaming, streaming);
        }

        public void SetJsonRpcStreamItemCount(int count) => SetTag(Tags.JsonRpcStreamItemCount, count);

        public void SetDashboardHealthy(bool healthy) => SetTag(Tags.DashboardHealthy, healthy);

        public void SetDashboardUrlSource(string source) => SetTag(Tags.DashboardUrlSource, source);

        public void SetDashboardHasApiBaseUrl(bool hasApiBaseUrl) => SetTag(Tags.DashboardHasApiBaseUrl, hasApiBaseUrl);

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

        public void SetResourceLifecycle(IResource resource, string resourceId, CustomResourceSnapshot snapshot)
        {
            SetResource(resource);
            SetTag(Tags.ResourceId, resourceId);
            SetTag(Tags.ResourceSnapshotVersion, snapshot.Version);
            SetTag(Tags.ResourceReady, snapshot.ResourceReadyEvent is not null);
            SetTag(Tags.ResourceState, snapshot.State?.Text);
            SetTag(Tags.ResourceHealthStatus, snapshot.HealthStatus?.ToString());
            SetTag(Tags.ResourceStartTime, snapshot.StartTimeStamp?.ToString("O", CultureInfo.InvariantCulture));
            SetTag(Tags.ResourceStopTime, snapshot.StopTimeStamp?.ToString("O", CultureInfo.InvariantCulture));

            if (snapshot.ExitCode is { } exitCode)
            {
                SetTag(Tags.ResourceExitCode, exitCode);
            }
        }

        public void AddResourceStartupObserved(CustomResourceSnapshot snapshot, DateTimeOffset timestamp) =>
            AddResourceStartupEvent(Events.ResourceStartupObserved, snapshot, timestamp);

        public void AddResourceStartupStateChanged(CustomResourceSnapshot snapshot, DateTimeOffset timestamp, string? previousState) =>
            AddResourceStartupEvent(Events.ResourceStartupStateChanged, snapshot, timestamp, previousState: previousState);

        public void AddResourceStartupHealthChanged(CustomResourceSnapshot snapshot, DateTimeOffset timestamp, string? previousHealthStatus) =>
            AddResourceStartupEvent(Events.ResourceStartupHealthChanged, snapshot, timestamp, previousHealthStatus: previousHealthStatus);

        public void AddResourceStartupReady(CustomResourceSnapshot snapshot, DateTimeOffset timestamp) =>
            AddResourceStartupEvent(Events.ResourceStartupReady, snapshot, timestamp);

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

        private void AddResourceStartupEvent(
            string eventName,
            CustomResourceSnapshot snapshot,
            DateTimeOffset timestamp,
            string? previousState = null,
            string? previousHealthStatus = null)
        {
            if (activity is null)
            {
                return;
            }

            var tags = new ActivityTagsCollection
            {
                [Tags.ResourceSnapshotVersion] = snapshot.Version,
                [Tags.ResourceReady] = snapshot.ResourceReadyEvent is not null
            };

            if (previousState is not null)
            {
                tags[Tags.PreviousResourceState] = previousState;
            }

            if (previousHealthStatus is not null)
            {
                tags[Tags.PreviousResourceHealthStatus] = previousHealthStatus;
            }

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

            activity.AddEvent(new ActivityEvent(eventName, timestamp, tags));
        }

        private void SetTag(string key, object? value) => activity?.SetTag(key, value);
    }
}
