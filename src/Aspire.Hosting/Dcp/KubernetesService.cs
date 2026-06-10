// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;
using k8s;
using k8s.Autorest;
using k8s.Exceptions;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using YamlDotNet.Core;

namespace Aspire.Hosting.Dcp;

internal enum DcpApiOperationType
{
    Create = 1,
    List = 2,
    Delete = 3,
    Watch = 4,
    GetLogSubresource = 5,
    Get = 6,
    Patch = 7,
    ServerStop = 8,
    ResourceCleanup = 9
}

internal interface IKubernetesService
{
    Task<T> GetAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata;
    Task<T> CreateAsync<T>(T obj, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata;
    Task<T> PatchAsync<T>(T obj, V1Patch patch, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata;
    Task<List<T>> ListAsync<T>(string? namespaceParameter = null, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata;
    Task<T> DeleteAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata;
    IAsyncEnumerable<(WatchEventType, T)> WatchAsync<T>(
        string? namespaceParameter = null,
        CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata;

    /// <summary>
    /// Returns a log stream for the specified resource.
    /// </summary>
    /// <param name="obj">The resource to get the log stream for.</param>
    /// <param name="logStreamType">The type of log stream to retrieve ("stdout", "stderr", "startup_stdout", or "startup_stderr", see <see cref="Aspire.Hosting.Dcp.Model.Logs"/>).</param>
    /// <param name="cancellationToken">The cancellation token for the stream retrieval operation (does not affect the returned stream).</param>
    /// <param name="follow">If true, the log stream will be followed until the resource is deleted or the stream is disposed of.</param>
    /// <param name="timestamps">If true, timestamps (RFC3339) will be included in the log stream.</param>
    /// <param name="lineNumbers">If true, line numbers will be included in the log stream.</param>
    /// <param name="limit">If specified, limits the number of log linets returned. Cannot be used with "follow".</param>
    /// <param name="tail">If specified, limits the response to at most N existing, NEWEST log lines. If "follow" is true, new log lines that appear after the log stream was created do not count against the limit, and will be streamed until the client closes the stream.</param>
    /// <param name="skip">If specified, skips the first N log lines in the result set. Cannot be used together with "tail".</param>
    Task<Stream> GetLogStreamAsync<T>(
        T obj,
        string logStreamType,
        CancellationToken cancellationToken = default,
        bool? follow = true,
        bool? timestamps = false,
        bool? lineNumbers = false,
        long? limit = null,
        long? tail = null,
        long? skip = null
    ) where T : CustomResource, IKubernetesStaticMetadata;
    Task StopServerAsync(string resourceCleanup = ResourceCleanup.Full, CancellationToken cancellation = default);
    Task CleanupResourcesAsync(CancellationToken cancellationToken = default);
}

internal sealed class KubernetesService(ILogger<KubernetesService> logger, IOptions<DcpOptions> dcpOptions, Locations locations, IConfiguration configuration) : IKubernetesService, IDisposable
{
    // A pseudo-resource type used for log operations on the DCP execution document.
    private const string DcpExecutionResourceType = "DcpExecution";

    private static readonly TimeSpan s_initialRetryDelay = TimeSpan.FromMilliseconds(100);
    private static GroupVersion GroupVersion => Model.Dcp.GroupVersion;
    private readonly SemaphoreSlim _kubeconfigReadSemaphore = new(1);

    private DcpKubernetesClient? _kubernetes;
    private ResiliencePipeline? _resiliencePipeline;
    private bool _disposed;

    public TimeSpan MaxRetryDuration { get; set; } = TimeSpan.FromSeconds(20);

    public Task<T> GetAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        var resourceType = GetResourceFor<T>();

        return ExecuteWithRetry(
            DcpApiOperationType.Get,
            T.ObjectKind,
            async (kubernetes) =>
            {
                var response = string.IsNullOrEmpty(namespaceParameter)
                ? await kubernetes.CustomObjects.GetClusterCustomObjectWithHttpMessagesAsync(
                    GroupVersion.Group,
                    GroupVersion.Version,
                    resourceType,
                    name,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kubernetes.CustomObjects.GetNamespacedCustomObjectWithHttpMessagesAsync(
                    GroupVersion.Group,
                    GroupVersion.Version,
                    namespaceParameter,
                    resourceType,
                    name,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return KubernetesJson.Deserialize<T>(response.Body.ToString());
            },
            RetryOnConnectivityAndConflictErrors,
            cancellationToken);
    }

    public Task<T> CreateAsync<T>(T obj, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resourceType = GetResourceFor<T>();
        var namespaceParameter = obj.Namespace();

        return ExecuteWithRetry(
           DcpApiOperationType.Create,
           T.ObjectKind,
           async (kubernetes) =>
           {
               var response = string.IsNullOrEmpty(namespaceParameter)
                ? await kubernetes.CustomObjects.CreateClusterCustomObjectWithHttpMessagesAsync(
                    obj,
                    GroupVersion.Group,
                    GroupVersion.Version,
                    resourceType,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kubernetes.CustomObjects.CreateNamespacedCustomObjectWithHttpMessagesAsync(
                    obj,
                    GroupVersion.Group,
                    GroupVersion.Version,
                    namespaceParameter,
                    resourceType,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

               return KubernetesJson.Deserialize<T>(response.Body.ToString());
           },
           RetryOnConnectivityErrors,
           cancellationToken);
    }

    public Task<T> PatchAsync<T>(T obj, V1Patch patch, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resourceType = GetResourceFor<T>();
        var namespaceParameter = obj.Namespace();

        return ExecuteWithRetry(
           DcpApiOperationType.Patch,
           T.ObjectKind,
           async (kubernetes) =>
           {
               var response = string.IsNullOrEmpty(namespaceParameter)
                ? await kubernetes.CustomObjects.PatchClusterCustomObjectWithHttpMessagesAsync(
                    patch,
                    GroupVersion.Group,
                    GroupVersion.Version,
                    resourceType,
                    obj.Metadata.Name,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kubernetes.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(
                    patch,
                    GroupVersion.Group,
                    GroupVersion.Version,
                    namespaceParameter,
                    resourceType,
                    obj.Metadata.Name,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

               return KubernetesJson.Deserialize<T>(response.Body.ToString());
           },
           RetryOnConnectivityErrors,
           cancellationToken);
    }

    public Task<List<T>> ListAsync<T>(string? namespaceParameter = null, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resourceType = GetResourceFor<T>();

        return ExecuteWithRetry(
            DcpApiOperationType.List,
            T.ObjectKind,
            async (kubernetes) =>
            {
                var response = string.IsNullOrEmpty(namespaceParameter)
                    ? await kubernetes.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
                        GroupVersion.Group,
                        GroupVersion.Version,
                        resourceType,
                        cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await kubernetes.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(
                        GroupVersion.Group,
                        GroupVersion.Version,
                        namespaceParameter,
                        resourceType,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                return KubernetesJson.Deserialize<CustomResourceList<T>>(response.Body.ToString()).Items;
            },
            RetryOnConnectivityAndConflictErrors,
            cancellationToken);
    }

    public Task<T> DeleteAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resourceType = GetResourceFor<T>();

        return ExecuteWithRetry(
            DcpApiOperationType.Delete,
            T.ObjectKind,
            async (kubernetes) =>
            {
                var response = string.IsNullOrEmpty(namespaceParameter)
                ? await kubernetes.CustomObjects.DeleteClusterCustomObjectWithHttpMessagesAsync(
                    GroupVersion.Group,
                    GroupVersion.Version,
                    resourceType,
                    name,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await kubernetes.CustomObjects.DeleteNamespacedCustomObjectWithHttpMessagesAsync(
                    GroupVersion.Group,
                    GroupVersion.Version,
                    namespaceParameter,
                    resourceType,
                    name,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return KubernetesJson.Deserialize<T>(response.Body.ToString());
            },
            RetryOnConnectivityAndConflictErrors,
            cancellationToken);
    }

    public async IAsyncEnumerable<(WatchEventType, T)> WatchAsync<T>(
        string? namespaceParameter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resourceType = GetResourceFor<T>();

        // WatchAsync can become unresponsive if running long enough
        // We use a helper to periodically restart the inner watch enumerable
        var innerWatchFactory = ((WatchEventType, T)? lastValue, CancellationToken restartCancellationToken) =>
        {
            return ExecuteWithRetry(
                DcpApiOperationType.Watch,
                T.ObjectKind,
                (kubernetes) =>
                {
                    var responseTask = string.IsNullOrEmpty(namespaceParameter)
                        ? kubernetes.CustomObjects.ListClusterCustomObjectWithHttpMessagesAsync(
                            GroupVersion.Group,
                            GroupVersion.Version,
                            resourceType,
                            watch: true,
                            cancellationToken: restartCancellationToken)
                        : kubernetes.CustomObjects.ListNamespacedCustomObjectWithHttpMessagesAsync(
                            GroupVersion.Group,
                            GroupVersion.Version,
                            namespaceParameter,
                            resourceType,
                            watch: true,
                            cancellationToken: restartCancellationToken);

                    // TODO: KubernetesClient v18 marked WatchAsync extension method as obsolete.
                    // The new pattern uses Watcher<T> directly, but requires significant refactoring.
                    // This API still works in v18.x and will be updated in a future change.
#pragma warning disable CS0618 // Type or member is obsolete
                    return responseTask.WatchAsync<T, object>(onError: null, restartCancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete
                },
                RetryOnConnectivityAndConflictErrors,
                restartCancellationToken);
        };

        await foreach (var item in PeriodicRestartAsyncEnumerable.CreateAsync(innerWatchFactory, restartInterval: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task<Stream> GetLogStreamAsync<T>(
        T obj,
        string logStreamType,
        CancellationToken cancellationToken = default,
        bool? follow = true,
        bool? timestamps = false,
        bool? lineNumbers = false,
        long? limit = null,
        long? tail = null,
        long? skip = null) where T : CustomResource, IKubernetesStaticMetadata
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resourceType = GetResourceFor<T>();

        List<(string name, string value)> queryParams = [
            (name: "follow", value: follow == true ? "true": "false"),
            (name: "timestamps", value: timestamps == true ? "true" : "false"),
            (name: "source", value: logStreamType),
            (name: "line_numbers", value: lineNumbers == true ? "true" : "false"),
        ];
        if (limit.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit.Value, 1, nameof(limit));
            queryParams.Add((name: "limit", value: limit.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (tail.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(tail.Value, 1, nameof(tail));
            queryParams.Add((name: "tail", value: tail.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (skip.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(skip.Value, 1, nameof(skip));
            queryParams.Add((name: "skip", value: skip.Value.ToString(CultureInfo.InvariantCulture)));
        }

        return ExecuteWithRetry(
            DcpApiOperationType.GetLogSubresource,
            T.ObjectKind,
            async (kubernetes) =>
            {
                var response = await kubernetes.ReadSubResourceAsStreamAsync(
                    GroupVersion.Group,
                    GroupVersion.Version,
                    resourceType,
                    obj.Metadata.Name,
                    Logs.SubResourceName,
                    obj.Metadata.Namespace(),
                    queryParams,
                    cancellationToken
                ).ConfigureAwait(false);

                return response.Body;
            },
            RetryOnConnectivityAndConflictErrors,
            cancellationToken
        );
    }

    public Task StopServerAsync(string resourceCleanup = ResourceCleanup.Full, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return ExecuteWithRetry(
            DcpApiOperationType.ServerStop,
            DcpExecutionResourceType,
            async (kubernetes) =>
            {
                await kubernetes.PatchExecutionDocumentAsync(
                    new ApiServerExecution
                    {
                        ApiServerStatus = ApiServerStatus.Stopping,
                        ShutdownResourceCleanup = ResourceCleanup.Full
                    },
                    cancellationToken
                    ).ConfigureAwait(false);
                return (object?)null;
            },
            RetryOnConnectivityErrors,
            cancellationToken
        );
    }

    public async Task CleanupResourcesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var executionDoc = await ExecuteWithRetry(
            DcpApiOperationType.ResourceCleanup,
            DcpExecutionResourceType,
            async (kubernetes) =>
            {
                return await kubernetes.PatchExecutionDocumentAsync(
                    new ApiServerExecution
                    {
                        ApiServerStatus = ApiServerStatus.CleaningResources,
                        ShutdownResourceCleanup = ResourceCleanup.Full
                    },
                    cancellationToken
                    ).ConfigureAwait(false);
            },
            RetryOnConnectivityErrors,
            cancellationToken
        ).ConfigureAwait(false);

        if (executionDoc.ResourcesCleanedUp)
        {
            return;
        }

        var retryPipeline = new ResiliencePipelineBuilder<ApiServerExecution>()
            .AddRetry(new RetryStrategyOptions<ApiServerExecution>()
            {
                ShouldHandle = new PredicateBuilder<ApiServerExecution>()
                    .Handle<Exception>(RetryOnConnectivityErrors)
                    .HandleResult(executionDoc => !executionDoc.ResourcesCleanedUp),
                BackoffType = DelayBackoffType.Constant,
                MaxRetryAttempts = int.MaxValue,
                Delay = s_initialRetryDelay,
                MaxDelay = TimeSpan.FromSeconds(1),
            })
            .Build();

        await retryPipeline.ExecuteAsync(async cancellationContext =>
        {
            // Re-establish the client before each attempt. A concurrent connectivity failure elsewhere can
            // invalidate the cached client (set _kubernetes to null), so we must not dereference it directly.
            await EnsureKubernetesAsync(cancellationContext).ConfigureAwait(false);
            return await _kubernetes!.GetExecutionDocumentAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        _kubeconfigReadSemaphore?.Dispose();
        _kubernetes?.Dispose();
    }

    private static string GetResourceFor<T>() where T : CustomResource
    {
        if (!Model.Dcp.Schema.TryGet<T>(out var kindWithResource))
        {
            throw new InvalidOperationException($"Unknown custom resource type: {typeof(T).Name}");
        }

        return kindWithResource.Resource;
    }

    private Task<TResult> ExecuteWithRetry<TResult>(
        DcpApiOperationType operationType,
        string resourceType,
        Func<DcpKubernetesClient, TResult> operation,
        Func<Exception, bool> isRetryable,
        CancellationToken cancellationToken)
    {
        return ExecuteWithRetry<TResult>(
            operationType,
            resourceType,
            (DcpKubernetesClient kubernetes) => Task.FromResult(operation(kubernetes)),
            isRetryable,
            cancellationToken);
    }

    private async Task<TResult> ExecuteWithRetry<TResult>(
        DcpApiOperationType operationType,
        string resourceType,
        Func<DcpKubernetesClient, Task<TResult>> operation,
        Func<Exception, bool> isRetryable,
        CancellationToken cancellationToken)
    {
        using var activity = ProfilingTelemetry.StartDcpKubernetesApi(configuration, operationType, resourceType);
        var retryCount = 0;
        var clientReadyRecorded = false;

        var resiliencePipeline = CreateKubernetesCallResiliencePipeline(isRetryable, activity, () => retryCount++);

        try
        {
            return await resiliencePipeline.ExecuteAsync<TResult>(async (cancellationToken) =>
            {
                // Establish (or re-establish) the connection to DCP inside the retry loop. Doing this here
                // (rather than once up front) means a failure to read a partially-written kubeconfig, or a
                // client built from a stale kubeconfig, is retried by this same pipeline instead of failing
                // permanently. The cached client short-circuits EnsureKubernetesAsync once it is established.
                var clientReady = await EnsureKubernetesAsync(cancellationToken).ConfigureAwait(false);
                if (!clientReadyRecorded)
                {
                    // Record the readiness telemetry once, for the attempt that actually established the client.
                    clientReadyRecorded = true;
                    activity.AddKubernetesClientReady(clientReady.WaitMilliseconds, clientReady.Initialized);
                }

                var client = _kubernetes!;
                try
                {
                    return await operation(client).ConfigureAwait(false);
                }
                catch (Exception ex) when (RetryOnConnectivityErrors(ex))
                {
                    // A connectivity failure can mean the cached client was built from a stale or
                    // partially-written kubeconfig (for example, DCP rewrote it with a new endpoint).
                    // Invalidate the client so the next retry iteration re-reads the kubeconfig and
                    // rebuilds it. We deliberately do NOT invalidate on other retryable errors such as
                    // HTTP 409 Conflict, which are legitimate API results rather than a bad client.
                    await InvalidateKubernetesClientAsync(client).ConfigureAwait(false);
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            activity.SetDcpApiRetryCount(retryCount);
        }
    }

    private async Task InvalidateKubernetesClientAsync(DcpKubernetesClient failedClient)
    {
        await _kubeconfigReadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // Only clear the field if it still points at the client that failed. A concurrent operation
            // may already have rebuilt a fresh client, and we must not discard that one.
            if (ReferenceEquals(_kubernetes, failedClient))
            {
                _kubernetes = null;
            }
        }
        finally
        {
            _kubeconfigReadSemaphore.Release();
        }

        // Note: we intentionally do not dispose the failed client here. Other operations running
        // concurrently may have captured the same instance and still be mid-request; disposing it would
        // turn their connectivity failures into ObjectDisposedExceptions. The orphaned client is reclaimed
        // by the GC, and KubernetesService.Dispose() disposes whatever client is current at shutdown.
    }

    private static bool RetryOnConnectivityErrors(Exception ex) => ex is HttpRequestException || ex is KubeConfigException;
    private static bool RetryOnConnectivityAndConflictErrors(Exception ex) =>
        ex is HttpRequestException ||
        ex is KubeConfigException ||
        (ex is HttpOperationException hoe && hoe.Response.StatusCode == System.Net.HttpStatusCode.Conflict);

    private ResiliencePipeline CreateKubernetesCallResiliencePipeline(
        Func<Exception, bool> isRetryable,
        ProfilingTelemetry.ActivityScope activity,
        Action recordRetry)
    {
        var resiliencePipeline = new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = MaxRetryDuration,
                OnTimeout = (_) =>
                {
                    activity.AddKubernetesApiTimeout();
                    return ValueTask.CompletedTask;
                }
            })
            .AddRetry(new RetryStrategyOptions()
            {
                ShouldHandle = new PredicateBuilder().Handle(isRetryable),
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = int.MaxValue,
                Delay = s_initialRetryDelay,
                MaxDelay = TimeSpan.FromSeconds(5),
                OnRetry = (retry) =>
                {
                    recordRetry();
                    activity.AddKubernetesApiRetry(retry.AttemptNumber, retry.RetryDelay, retry.Outcome.Exception);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
        return resiliencePipeline;
    }

    private ResiliencePipeline CreateReadKubeconfigResiliencePipeline()
    {
        if (_resiliencePipeline == null)
        {
            var configurationReadRetry = new RetryStrategyOptions()
            {
                // Handle exceptions caused by races between writing and reading the configuration file.
                // If the file is loaded while it is still being written, this can result in a YamlException being thrown.
                ShouldHandle = new PredicateBuilder().Handle<KubeConfigException>().Handle<YamlException>().Handle<IOException>(),
                BackoffType = DelayBackoffType.Constant,
                MaxRetryAttempts = dcpOptions.Value.KubernetesConfigReadRetryCount,
                MaxDelay = TimeSpan.FromMilliseconds(dcpOptions.Value.KubernetesConfigReadRetryIntervalMilliseconds),
                OnRetry = (retry) =>
                {
                    logger.LogDebug(
                        "Waiting for Kubernetes configuration file at '{DcpKubeconfigPath}' (attempt {Iteration}).",
                        locations.DcpKubeconfigPath,
                        retry.AttemptNumber
                        );
                    return ValueTask.CompletedTask;
                }
            };

            _resiliencePipeline = new ResiliencePipelineBuilder().AddRetry(configurationReadRetry).Build();
        }

        return _resiliencePipeline;
    }

    private async Task<KubernetesClientReady> EnsureKubernetesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Return early before waiting for the semaphore if we can.
        if (_kubernetes != null)
        {
            return new KubernetesClientReady(WaitMilliseconds: 0, Initialized: false);
        }

        var lockWaitStopwatch = Stopwatch.StartNew();
        await _kubeconfigReadSemaphore.WaitAsync(-1, cancellationToken).ConfigureAwait(false);
        lockWaitStopwatch.Stop();
        var lockWaitMilliseconds = lockWaitStopwatch.ElapsedMilliseconds;

        try
        {
            // Second chance shortcut if multiple threads got caught.
            if (_kubernetes != null)
            {
                return new KubernetesClientReady(lockWaitMilliseconds, Initialized: false);
            }

            using var activity = ProfilingTelemetry.StartDcpEnsureKubernetesClient(configuration, File.Exists(locations.DcpKubeconfigPath));
            activity.SetDcpKubeconfigLockWait(lockWaitMilliseconds);
            activity.AddKubeconfigLockAcquired();

            // We retry reading the kubeconfig file because DCP takes a few moments to write
            // it to disk. This retry pipeline will only be invoked by a single thread the
            // rest will be held at the semaphore.
            var readStopwatch = new Stopwatch();
            readStopwatch.Start();

            var readPipeline = CreateReadKubeconfigResiliencePipeline();

            // The overall time budget for establishing the connection is enforced by the caller's
            // resilience pipeline (CreateKubernetesCallResiliencePipeline applies MaxRetryDuration as a
            // timeout) via the cancellation token, which the file-wait loop below observes. We therefore
            // do not wrap the read in its own timeout pipeline here.
            try
            {
                _kubernetes = await readPipeline.ExecuteAsync<DcpKubernetesClient>(async (cancellationToken) =>
                {
                    var fileWaitStopwatch = Stopwatch.StartNew();
                    var fileInfo = new FileInfo(locations.DcpKubeconfigPath);
                    while (!fileInfo.Exists)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(dcpOptions.Value.KubernetesConfigReadRetryIntervalMilliseconds), cancellationToken).ConfigureAwait(false);
                        fileInfo = new FileInfo(locations.DcpKubeconfigPath);
                    }
                    fileWaitStopwatch.Stop();
                    activity.SetDcpKubeconfigFileWait(fileWaitStopwatch.ElapsedMilliseconds);
                    activity.AddKubeconfigFileDetected();

                    var buildConfigStopwatch = Stopwatch.StartNew();
                    var config = await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(kubeconfig: fileInfo, useRelativePaths: false).ConfigureAwait(false);
                    buildConfigStopwatch.Stop();
                    readStopwatch.Stop();

                    logger.LogDebug(
                        "Successfully read Kubernetes configuration from '{DcpKubeconfigPath}' after {DurationMs} milliseconds.",
                        locations.DcpKubeconfigPath,
                        readStopwatch.ElapsedMilliseconds
                        );
                    activity.SetDcpKubeconfigBuildDuration(buildConfigStopwatch.ElapsedMilliseconds);
                    activity.SetDcpKubeconfigReadDuration(readStopwatch.ElapsedMilliseconds);
                    activity.AddKubeconfigReadComplete();

                    return new DcpKubernetesClient(config);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                activity.SetError(ex);
                throw;
            }
            activity.AddKubernetesClientCreated();
            return new KubernetesClientReady(lockWaitMilliseconds, Initialized: true);
        }
        finally
        {
            _kubeconfigReadSemaphore.Release();
        }
    }

    private readonly record struct KubernetesClientReady(long WaitMilliseconds, bool Initialized);
}
