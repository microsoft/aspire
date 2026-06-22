// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Diagnostics;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpResourceWatcherTests
{
    /// <summary>
    /// Verifies that unobserved task exceptions from watch stream IOException are properly observed
    /// during DcpResourceWatcher disposal. The k8s client's WatchAsync internally creates a
    /// background reader task that faults with IOException ("The request was aborted.") when
    /// the HTTP/2 connection is aborted during shutdown. KubernetesService.WatchAsync provides
    /// an onError callback to the k8s client to catch this exception.
    /// See https://github.com/microsoft/aspire/issues/18388
    /// </summary>
    [Fact]
    public async Task DisposeAsync_DoesNotLeakUnobservedIOExceptionFromWatchStreams()
    {
        var unobservedExceptions = new ConcurrentBag<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobservedExceptions.Add(e.Exception);
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await StartAndDisposeWatcherWithLeakyWatchStreams();

            // Force GC to trigger finalization of any faulted tasks that were
            // not properly observed. If the onError callback is missing, the k8s
            // client's internal read task would surface here as unobserved.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var ioExceptions = unobservedExceptions
                .SelectMany(e => e is AggregateException agg ? (IEnumerable<Exception>)agg.InnerExceptions : new[] { e })
                .Where(e => e is IOException)
                .ToList();

            Assert.Empty(ioExceptions);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    // NoInlining prevents the JIT from keeping local variables (including Task references held
    // transitively by the watcher and service) alive past their last use, which would prevent
    // the fire-and-forget tasks from being collected by GC.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task StartAndDisposeWatcherWithLeakyWatchStreams()
    {
        using var shutdownCts = new CancellationTokenSource();
        var kubernetesService = new LeakyWatchKubernetesService();

        var model = new DistributedApplicationModel(new ResourceCollection());
        using var loggerService = new ResourceLoggerService();
        var executorEvents = new DcpExecutorEvents();
        var appResources = new DcpAppResourceStore();
        var configuration = new ConfigurationBuilder().Build();
        var profilingTelemetry = new ProfilingTelemetry(configuration);

        var watcher = new DcpResourceWatcher(
            NullLogger.Instance,
            kubernetesService,
            loggerService,
            executorEvents,
            model,
            appResources,
            configuration,
            profilingTelemetry,
            shutdownCts.Token);

        // Disable retry pipeline so watch tasks execute immediately without retries.
        watcher.WatchResourceRetryPipeline = ResiliencePipeline.Empty;

        watcher.Start();

        // Wait until all 5 resource type watchers have entered WatchAsync.
        await kubernetesService.AllWatchersStarted;

        // Simulate shutdown: cancel the shutdown token first (mirrors DcpExecutor.StopAsync
        // calling _shutdownCancellation.Cancel()), then dispose the watcher.
        shutdownCts.Cancel();

        await watcher.DisposeAsync();

        // Wait for the leaked fire-and-forget tasks to fault. The semaphore is signaled from
        // the finally block of each leaked task, which runs just before the task is marked faulted.
        await kubernetesService.WaitForLeakedTasksToCompleteAsync();
    }

    /// <summary>
    /// A test <see cref="IKubernetesService"/> that simulates the k8s client behavior where
    /// cancelling a watch stream causes internal HTTP/2 reader tasks to fault with
    /// <see cref="IOException"/>. In the real KubernetesClient, <c>WatchAsync</c> internally
    /// creates a <c>Watcher&lt;T&gt;</c> that starts a background loop reading from
    /// <c>LineSeparatedHttpContent.CancelableStream</c>. When the HTTP/2 connection is aborted
    /// during <c>DistributedApplication</c> dispose, this read task faults with
    /// <c>IOException("The request was aborted.")</c>. When the caller provides an
    /// <c>onError</c> callback (as <see cref="KubernetesService"/> does), the k8s
    /// client routes the exception through it instead of leaving the task faulted and unobserved.
    /// Without <c>onError</c>, the exception surfaces as
    /// <see cref="TaskScheduler.UnobservedTaskException"/> during GC finalization.
    /// </summary>
    private sealed class LeakyWatchKubernetesService : IKubernetesService
    {
        private int _watcherCount;
        private readonly TaskCompletionSource _allWatchersStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // DcpResourceWatcher.Start() creates watchers for 5 resource types:
        // Executable, Container, ContainerExec, Service, Endpoint.
        private readonly SemaphoreSlim _leakedTasksCompleted = new(0, 5);

        /// <summary>
        /// Completes when all 5 resource type watchers have entered their
        /// <see cref="WatchAsync{T}"/> enumerations.
        /// </summary>
        public Task AllWatchersStarted => _allWatchersStarted.Task;

        /// <summary>
        /// Waits for all 5 background tasks to complete. The tasks signal the semaphore from
        /// their <c>finally</c> blocks. When <c>onError</c> is null the tasks fault with
        /// <see cref="IOException"/> and remain unobserved (eligible for GC finalization).
        /// When <c>onError</c> is provided the exception is routed through the callback
        /// and the tasks complete normally.
        /// </summary>
        public async Task WaitForLeakedTasksToCompleteAsync()
        {
            for (var i = 0; i < 5; i++)
            {
                await _leakedTasksCompleted.WaitAsync();
            }
        }

        public async IAsyncEnumerable<(WatchEventType, T)> WatchAsync<T>(
            string? namespaceParameter = null,
            Action<Exception>? onError = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where T : CustomResource, IKubernetesStaticMetadata
        {
            // Simulate the k8s client's internal HTTP/2 stream reader task. When the
            // cancellation token fires the task encounters an IOException, matching the
            // real Http2Connection.Http2Stream.ReadDataAsync abort behaviour. The k8s
            // client routes the exception through the onError callback when provided;
            // otherwise the task faults and the exception becomes unobserved.
#pragma warning disable CA2016 // Forward the CancellationToken parameter to methods
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    var ioException = new IOException("The request was aborted.");
                    if (onError is not null)
                    {
                        // The k8s client calls onError and completes the task normally.
                        onError(ioException);
                    }
                    else
                    {
                        // Without onError the exception faults the task — unobserved.
                        throw ioException;
                    }
                }
                finally
                {
                    _leakedTasksCompleted.Release();
                }
            });
#pragma warning restore CA2016

            // DcpResourceWatcher.Start() creates watchers for 5 resource types.
            if (Interlocked.Increment(ref _watcherCount) >= 5)
            {
                _allWatchersStarted.TrySetResult();
            }

            // Block until cancellation, simulating a long-running watch stream.
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }

        public Task<T> GetAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default)
            where T : CustomResource, IKubernetesStaticMetadata
            => throw new NotImplementedException();

        public Task<T> CreateAsync<T>(T obj, CancellationToken cancellationToken = default)
            where T : CustomResource, IKubernetesStaticMetadata
            => throw new NotImplementedException();

        public Task<T> PatchAsync<T>(T obj, V1Patch patch, CancellationToken cancellationToken = default)
            where T : CustomResource, IKubernetesStaticMetadata
            => throw new NotImplementedException();

        public Task<List<T>> ListAsync<T>(string? namespaceParameter = null, CancellationToken cancellationToken = default)
            where T : CustomResource, IKubernetesStaticMetadata
            => throw new NotImplementedException();

        public Task<T> DeleteAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default)
            where T : CustomResource, IKubernetesStaticMetadata
            => throw new NotImplementedException();

        public Task<Stream> GetLogStreamAsync<T>(T obj, string logStreamType, CancellationToken cancellationToken = default,
            bool? follow = true, bool? timestamps = false, bool? lineNumbers = false,
            long? limit = null, long? tail = null, long? skip = null)
            where T : CustomResource, IKubernetesStaticMetadata
            => Task.FromResult<Stream>(new MemoryStream());

        public Task StopServerAsync(string resourceCleanup = ResourceCleanup.Full, CancellationToken cancellation = default)
            => Task.CompletedTask;

        public Task CleanupResourcesAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
