// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // IFileSystemService is for evaluation purposes only.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Aspire.Hosting.Tests.Dcp;

public class KubernetesServiceTests(DcpApiServerCertificateFixture certificateFixture) : IClassFixture<DcpApiServerCertificateFixture>
{
    [Theory]
    [InlineData(null)]
    [InlineData("test-namespace")]
    public async Task WatchAsync_CancellationBeforeFirstEventClosesHttp2Stream(string? namespaceParameter)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<(string Protocol, string Path, string Authorization)>();
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue((context.Request.Protocol, context.Request.Path, context.Request.Headers.Authorization.ToString()));
            context.Response.ContentType = "application/json";
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            responseStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(namespaceParameter, watchCts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await responseStarted.Task.DefaultTimeout();
            Assert.False(moveNext.IsCompleted);

            watchCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            await requestCanceled.Task.DefaultTimeout();

            var collectionPath = namespaceParameter is null
                ? "/apis/usvc-dev.developer.microsoft.com/v1/containers"
                : $"/apis/usvc-dev.developer.microsoft.com/v1/namespaces/{namespaceParameter}/containers";
            Assert.Equal([("HTTP/2", collectionPath, "Bearer dcp-test-token")], requests.ToArray());
        }
        finally
        {
            watchCts.Cancel();
            try
            {
                await moveNext.DefaultTimeout();
            }
            catch (OperationCanceledException) when (watchCts.IsCancellationRequested)
            {
                // Expected when closing a pending watch read.
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task WatchAsync_RestartsAndClosesHttp2StreamWithoutConsumerProgress(bool sendHeaders, bool sendFirstEvent, bool blockFirstRequest)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        service.WatchRestartInterval = TimeSpan.FromSeconds(1);
        var requestsStarted = Channel.CreateUnbounded<string>();
        var requestCancellations = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var requestCount = 0;
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            var requestNumber = Interlocked.Increment(ref requestCount);
            var resourceName = $"frontend-{requestNumber}";
            var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            requestCancellations[resourceName] = requestCanceled;
            requestsStarted.Writer.TryWrite(resourceName);
            Assert.Equal("HTTP/2", context.Request.Protocol);
            try
            {
                if (blockFirstRequest && requestNumber == 1)
                {
                    // Force setup to expire so the first observed event belongs to a later generation.
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                context.Response.ContentType = "application/json";
                if (sendFirstEvent)
                {
                    await WriteWatchEventAsync(context, Container.Create(resourceName, "test-image"));
                }
                else if (sendHeaders)
                {
                    await context.Response.StartAsync(context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            string watchedResourceName;
            if (sendFirstEvent)
            {
                Assert.True(await moveNext.DefaultTimeout());
                watchedResourceName = enumerator.Current.Item2.Metadata.Name;
                string startedResourceName;
                do
                {
                    startedResourceName = await requestsStarted.Reader.ReadAsync(cts.Token).DefaultTimeout();
                }
                while (startedResourceName != watchedResourceName);
            }
            else
            {
                watchedResourceName = await requestsStarted.Reader.ReadAsync(cts.Token).DefaultTimeout();
            }
            if (blockFirstRequest)
            {
                await requestCancellations["frontend-1"].Task.DefaultTimeout();
                Assert.NotEqual("frontend-1", watchedResourceName);
            }

            await requestCancellations[watchedResourceName].Task.DefaultTimeout();
            var nextResourceName = await requestsStarted.Reader.ReadAsync(cts.Token).DefaultTimeout();
            Assert.NotEqual(watchedResourceName, nextResourceName);
            if (!sendFirstEvent)
            {
                Assert.False(moveNext.IsCompleted);
            }
        }
        finally
        {
            if (!moveNext.IsCompleted)
            {
                cts.Cancel();
            }
            try
            {
                await moveNext.DefaultTimeout();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when closing a pending watch read.
            }
        }
    }

    [Fact]
    public async Task WatchAsync_IdleHttp2StreamOutlivesConnectionDeadlineAndOtherApiTimeouts()
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var responseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            if (context.Request.Query["watch"] == "true")
            {
                context.Response.ContentType = "application/json";
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                responseStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    requestCanceled.TrySetResult();
                }
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);
        await using var enumerator = service.WatchAsync<Container>(cancellationToken: watchCts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await responseStarted.Task.DefaultTimeout();
            await Assert.ThrowsAsync<TimeoutRejectedException>(() => service.ListAsync<Container>(cancellationToken: cts.Token))
                .DefaultTimeout(TestConstants.LongTimeoutDuration);
            Assert.False(moveNext.IsCompleted);
            Assert.False(requestCanceled.Task.IsCompleted);
            watchCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            await requestCanceled.Task.DefaultTimeout();
        }
        finally
        {
            watchCts.Cancel();
            try
            {
                await moveNext.DefaultTimeout();
            }
            catch (OperationCanceledException) when (watchCts.IsCancellationRequested)
            {
                // Expected when closing a pending watch read.
            }
        }
    }

    [Fact]
    public async Task WatchAsync_DrainsHttp2StreamAndPreservesEventOrderWhileConsumerPaused()
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var sendRemainingEvents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int eventCount = 65;
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            Assert.Equal("HTTP/2", context.Request.Protocol);
            var container = Container.Create("frontend", "test-image");
            container.Metadata.ResourceVersion = "0";
            await WriteWatchEventAsync(context, container);
            await sendRemainingEvents.Task.WaitAsync(context.RequestAborted);
            // Exceed the HTTP/2 receive window: without independent reads, these writes
            // cannot finish while the consumer is paused on the first event.
            container.Metadata.Annotations = new Dictionary<string, string> { ["padding"] = new('x', 65536) };
            for (var i = 1; i < eventCount; i++)
            {
                container.Metadata.ResourceVersion = i.ToString(CultureInfo.InvariantCulture);
                await WriteWatchEventAsync(context, container);
            }
            eventsSent.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        Task<bool>? moveNext = null;
        try
        {
            moveNext = enumerator.MoveNextAsync().AsTask();
            Assert.True(await moveNext.DefaultTimeout());
            sendRemainingEvents.TrySetResult();
            await eventsSent.Task.DefaultTimeout(TestConstants.LongTimeoutDuration);
            var versions = new List<string> { enumerator.Current.Item2.Metadata.ResourceVersion };
            while (versions.Count < eventCount)
            {
                moveNext = enumerator.MoveNextAsync().AsTask();
                Assert.True(await moveNext.DefaultTimeout());
                versions.Add(enumerator.Current.Item2.Metadata.ResourceVersion);
            }
            Assert.Equal(Enumerable.Range(0, eventCount).Select(i => i.ToString(CultureInfo.InvariantCulture)), versions);
        }
        finally
        {
            cts.Cancel();
            if (moveNext is { IsCompleted: false })
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            }
        }
        await requestCanceled.Task.DefaultTimeout();
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("MODIFIED")]
    public async Task WatchAsync_PropagatesStatusErrors(string eventType)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        await using var server = await StartServerAsync(cts.Token, requestHandler: context =>
            context.Response.WriteAsync(
                $$$"""{"type":"{{{eventType}}}","object":{"kind":"Status","status":"Failure","reason":"Expired","code":410}}""" + "\n",
                context.RequestAborted)).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            var error = await Assert.ThrowsAsync<KubernetesException>(() => moveNext).DefaultTimeout();
            Assert.Equal("Failure", error.Status.Status);
            Assert.Equal(410, error.Status.Code);
        }
        finally
        {
            cts.Cancel();
            if (!moveNext.IsCompleted)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            }
        }
    }

    [Theory]
    [InlineData("ADDED")]
    [InlineData("MODIFIED")]
    [InlineData("ERROR")]
    public async Task WatchAsync_SuccessfulStatusDoesNotProduceResourceUpdateOrTerminateWatch(string eventType)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var statusSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendResource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            Interlocked.Increment(ref requestCount);
            await context.Response.WriteAsync(
                $$$"""{"type":"{{{eventType}}}","object":{"kind":"Status","status":"Success","code":200}}""" + "\n",
                context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            statusSent.TrySetResult();
            try
            {
                await sendResource.Task.WaitAsync(context.RequestAborted);
                await WriteWatchEventAsync(context, Container.Create("frontend", "test-image"));
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await statusSent.Task.DefaultTimeout();
            sendResource.TrySetResult();
            Assert.True(await moveNext.DefaultTimeout());
            Assert.Equal(WatchEventType.Modified, enumerator.Current.Item1);
            Assert.Equal("frontend", enumerator.Current.Item2.Metadata.Name);
            Assert.Equal(1, Volatile.Read(ref requestCount));
        }
        finally
        {
            cts.Cancel();
            if (!moveNext.IsCompleted)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            }
        }
        await requestCanceled.Task.DefaultTimeout();
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("null", false)]
    [InlineData("""{"type":"MODIFIED","object":null}""", true)]
    [InlineData("""{"type":"MODIFIED","object":null}""", false)]
    [InlineData("{invalid-json", true)]
    [InlineData("{invalid-json", false)]
    [InlineData("""{"type":"ERROR","object":{"kind":"Status","code":500}}""", true)]
    [InlineData("""{"type":"ERROR","object":{"kind":"Status","code":500}}""", false)]
    [InlineData("""{"type":"MODIFIED","object":{"kind":"Status","status":"Unknown"}}""", true)]
    [InlineData("""{"type":"MODIFIED","object":{"kind":"Status","status":"Unknown"}}""", false)]
    [InlineData("42", false)]
    [InlineData("true", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    public async Task WatchAsync_PropagatesMalformedEvents(string line, bool appendNewline)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        await using var server = await StartServerAsync(cts.Token, requestHandler: context =>
            context.Response.WriteAsync(line + (appendNewline ? "\n" : string.Empty), context.RequestAborted)).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<JsonException>(() => moveNext).DefaultTimeout();
        }
        finally
        {
            cts.Cancel();
            if (!moveNext.IsCompleted)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchResourceRetryPipeline_ReconnectsAfterAbortedHttp2Response(bool sendFirstEvent)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var firstEventReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            var requestNumber = Interlocked.Increment(ref requestCount);
            if (requestNumber == 1)
            {
                if (sendFirstEvent)
                {
                    await WriteWatchEventAsync(context, Container.Create("frontend-1", "test-image"));
                    // Wait for consumption so resetting the stream cannot discard the first event.
                    await firstEventReceived.Task.WaitAsync(context.RequestAborted);
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.StartAsync(context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }

                context.Abort();
                return;
            }

            await WriteWatchEventAsync(context, Container.Create("frontend-2", "test-image"));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);
        var pipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(NullLogger.Instance);
        var names = new List<string>();

        await pipeline.ExecuteAsync(async token =>
        {
            await foreach (var (_, container) in service.WatchAsync<Container>(cancellationToken: token))
            {
                names.Add(container.Metadata.Name);
                if (container.Metadata.Name == "frontend-1")
                {
                    firstEventReceived.TrySetResult();
                }
                else
                {
                    return;
                }
            }
        }, cts.Token).DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(sendFirstEvent ? ["frontend-1", "frontend-2"] : ["frontend-2"], names);
        Assert.Equal(2, Volatile.Read(ref requestCount));
        await requestCanceled.Task.DefaultTimeout();
    }

    [Theory]
    [InlineData("""{"type":"MODIFIED","object":""")]
    [InlineData("""{"type":"MODIFIED","object":{"kind":"Container","metadata":{"name":"frontend"}}""")]
    [InlineData("""{"type":"MODIFIED","object":{"kind":"Container","metadata":{"name":"front""")]
    public async Task WatchResourceRetryPipeline_ReconnectsAfterTruncatedEvent(string partialEvent)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requestCount = 0;
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            var requestNumber = Interlocked.Increment(ref requestCount);
            var container = Container.Create("frontend", "test-image");
            container.Metadata.ResourceVersion = requestNumber.ToString(CultureInfo.InvariantCulture);
            await WriteWatchEventAsync(context, container);
            if (requestNumber == 1)
            {
                await context.Response.WriteAsync(partialEvent, context.RequestAborted);
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);
        var pipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(NullLogger.Instance);
        var versions = new List<string>();

        await pipeline.ExecuteAsync(async token =>
        {
            await foreach (var (_, container) in service.WatchAsync<Container>(cancellationToken: token))
            {
                versions.Add(container.Metadata.ResourceVersion);
                if (versions.Count == 2)
                {
                    return;
                }
            }
        }, cts.Token).DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(["1", "2"], versions);
        Assert.Equal(2, Volatile.Read(ref requestCount));
        await requestCanceled.Task.DefaultTimeout();
    }

    [Fact]
    public async Task WatchResourceRetryPipeline_ReconnectsAfterEmptyResponses()
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requestCount = 0;
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            if (Interlocked.Increment(ref requestCount) <= 2)
            {
                await context.Response.StartAsync(context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                return;
            }

            await WriteWatchEventAsync(context, Container.Create("frontend", "test-image"));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
            }
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);
        var pipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(NullLogger.Instance);
        Container? observed = null;

        await pipeline.ExecuteAsync(async token =>
        {
            await foreach (var (_, container) in service.WatchAsync<Container>(cancellationToken: token))
            {
                observed = container;
                return;
            }
        }, cts.Token).DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal("frontend", observed?.Metadata.Name);
        Assert.Equal(3, Volatile.Read(ref requestCount));
        await requestCanceled.Task.DefaultTimeout();
    }

    [Fact]
    public async Task WatchAsync_AcceptsCompleteFinalEventWithoutNewline()
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requestCount = 0;
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                var container = Container.Create("frontend", "test-image");
                await context.Response.WriteAsync(
                    $$"""{"type":"MODIFIED","object":{{KubernetesJson.Serialize(container)}}}""", context.RequestAborted);
                return;
            }

            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            Assert.True(await moveNext.DefaultTimeout());
            Assert.Equal(WatchEventType.Modified, enumerator.Current.Item1);
            Assert.Equal("frontend", enumerator.Current.Item2.Metadata.Name);
        }
        finally
        {
            cts.Cancel();
            if (!moveNext.IsCompleted)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext).DefaultTimeout();
            }
        }
    }

    [Theory]
    [InlineData("\n", 1200)]
    [InlineData("\r\n", 1200)]
    [InlineData("\n", 1048576)]
    [InlineData("\r\n", 1048576)]
    public async Task WatchAsync_ReadsSegmentedUtf8EventWithoutWaitingForMoreData(string newline, int paddingLength)
    {
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var firstPartSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendRemaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new string('x', paddingLength) + "\u2603";
        var container = Container.Create("frontend", "test-image");
        container.Metadata.Annotations = new Dictionary<string, string> { ["message"] = message };
        var line = ($$"""{"type":"MODIFIED","object":{{KubernetesJson.Serialize(container)}}}""")
            .Replace("\\u2603", "\u2603", StringComparison.OrdinalIgnoreCase) + newline;
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(line)).ToArray();
        // Split a multibyte character across writes. Once the record ends, no more data
        // arrives: the reader must return the event without waiting to fill its buffer.
        var split = Array.IndexOf(bytes, (byte)0xe2) + 1;
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(bytes.AsMemory(0, split), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            firstPartSent.TrySetResult();
            await sendRemaining.Task.WaitAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(bytes.AsMemory(split), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        }).DefaultTimeout();
        WriteKubeconfig(kubeconfigPath, server.Port);
        await using var enumerator = service.WatchAsync<Container>(cancellationToken: cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync().AsTask();
        try
        {
            await firstPartSent.Task.DefaultTimeout();
            Assert.False(moveNext.IsCompleted);
            sendRemaining.TrySetResult();
            Assert.True(await moveNext.DefaultTimeout());
            Assert.Equal(message, enumerator.Current.Item2.Metadata.Annotations["message"]);
        }
        finally
        {
            sendRemaining.TrySetResult();
            if (!moveNext.IsCompleted)
            {
                cts.Cancel();
            }
            try
            {
                await moveNext.DefaultTimeout();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when closing a pending watch read.
            }
        }
    }

    [Theory]
    [InlineData(410, "Expired")]
    [InlineData(408, "Timeout")]
    [InlineData(429, "TooManyRequests")]
    [InlineData(500, "InternalError")]
    [InlineData(502, "BadGateway")]
    [InlineData(503, "ServiceUnavailable")]
    [InlineData(504, "Timeout")]
    public async Task WatchResourceRetryPipeline_ReconnectsAfterRetryableStatus(int statusCode, string reason)
    {
        foreach (var httpResponse in new[] { false, true })
        {
            using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
            var (service, kubeconfigPath, fileSystem) = CreateService();
            using var disposableFileSystem = fileSystem;
            using var disposableService = service;
            var requestCount = 0;
            var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
            {
                var requestNumber = Interlocked.Increment(ref requestCount);
                if (requestNumber == 1 && httpResponse)
                {
                    await WriteStatusAsync(context, statusCode, reason);
                    return;
                }
                var container = Container.Create("frontend", "test-image");
                container.Metadata.ResourceVersion = requestNumber.ToString(CultureInfo.InvariantCulture);
                await WriteWatchEventAsync(context, container);
                if (requestNumber == 1)
                {
                    await context.Response.WriteAsync(
                        $$$"""{"type":"ERROR","object":{"kind":"Status","status":"Failure","reason":"{{{reason}}}","code":{{{statusCode}}}}}""" + "\n",
                        context.RequestAborted);
                    return;
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    requestCanceled.TrySetResult();
                }
            }).DefaultTimeout();
            WriteKubeconfig(kubeconfigPath, server.Port);
            var pipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(NullLogger.Instance);
            var versions = new List<string>();
            string[] expectedVersions = httpResponse ? ["2"] : ["1", "2"];

            await pipeline.ExecuteAsync(async token =>
            {
                await foreach (var (_, container) in service.WatchAsync<Container>(cancellationToken: token))
                {
                    versions.Add(container.Metadata.ResourceVersion);
                    if (versions.Count == expectedVersions.Length)
                    {
                        return;
                    }
                }
            }, cts.Token).DefaultTimeout(TestConstants.LongTimeoutDuration);

            Assert.Equal(expectedVersions, versions);
            Assert.Equal(2, Volatile.Read(ref requestCount));
            await requestCanceled.Task.DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(400, "BadRequest")]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "NotFound")]
    public async Task WatchResourceRetryPipeline_DoesNotRetryPermanentStatus(int statusCode, string reason)
    {
        foreach (var httpResponse in new[] { false, true })
        {
            using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
            var (service, kubeconfigPath, fileSystem) = CreateService();
            using var disposableFileSystem = fileSystem;
            using var disposableService = service;
            var requestCount = 0;
            await using var server = await StartServerAsync(cts.Token, requestHandler: context =>
            {
                Interlocked.Increment(ref requestCount);
                if (httpResponse)
                {
                    return WriteStatusAsync(context, statusCode, reason);
                }
                return context.Response.WriteAsync(
                    $$$"""{"type":"ERROR","object":{"kind":"Status","status":"Failure","reason":"{{{reason}}}","code":{{{statusCode}}}}}""" + "\n",
                    context.RequestAborted);
            }).DefaultTimeout();
            WriteKubeconfig(kubeconfigPath, server.Port);
            var pipeline = DcpPipelineBuilder.BuildWatchResourcePipeline(NullLogger.Instance);

            if (httpResponse)
            {
                var error = await Assert.ThrowsAsync<HttpOperationException>(WatchAsync).DefaultTimeout();
                Assert.Equal(statusCode, (int)error.Response.StatusCode);
            }
            else
            {
                var error = await Assert.ThrowsAsync<KubernetesException>(WatchAsync).DefaultTimeout();
                Assert.Equal(statusCode, error.Status.Code);
            }

            Assert.Equal(1, Volatile.Read(ref requestCount));

            Task WatchAsync() => pipeline.ExecuteAsync(async token =>
            {
                await foreach (var _ in service.WatchAsync<Container>(cancellationToken: token))
                {
                }
            }, cts.Token).AsTask();
        }
    }

    private static async Task WriteWatchEventAsync(HttpContext context, Container container)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.ContentType = "application/json";
        }
        await context.Response.WriteAsync(
            $$"""{"type":"MODIFIED","object":{{KubernetesJson.Serialize(container)}}}""" + "\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private Task<TestDcpApiServer> StartServerAsync(
        CancellationToken cancellationToken = default,
        int successfulRequestNumber = 1,
        Func<HttpContext, Task>? requestHandler = null)
    {
        return TestDcpApiServer.StartAsync(certificateFixture.Certificate, cancellationToken, successfulRequestNumber, requestHandler);
    }

    // Verifies that establishing the connection happens inside the retry loop: when the kubeconfig does not
    // exist yet (DCP has not finished writing it), the operation waits and succeeds once it appears.
    [Fact]
    public async Task ExecuteWithRetry_EstablishesConnection_WhenKubeconfigInitiallyMissing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // No kubeconfig on disk initially.
        Assert.False(File.Exists(kubeconfigPath));

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        await Task.Delay(300, cts.Token);

        await using var server = await StartServerAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesInitializationTimeout_WhenKubeconfigAppearsAfterApiRetryBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromSeconds(1));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        // The kubeconfig appears after the normal API retry budget. Initial DCP connection establishment
        // needs its own startup budget because DCP has not exposed an API endpoint yet.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        await using var server = await StartServerAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesInitializationTimeout_UntilFirstApiOperationSucceeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // The fourth API request succeeds after exponential retry delays of 100, 200, and 400 milliseconds.
        // It is reachable under the initialization budget but not the 500-millisecond steady-state budget.
        await using var server = await StartServerAsync(cts.Token, successfulRequestNumber: 4);
        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesApiRetryDuration_AfterFirstApiOperationSucceeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await StartServerAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.ListAsync<Container>(cancellationToken: cts.Token);
        Assert.Empty(result);

        server.FailNextRequests(3);
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ExecuteWithRetry_CancelsApiRequest_WhenRetryDurationExpires()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await StartServerAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);
        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));

        server.DelayResponses(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
        await server.WaitForRequestCancellationAsync(cts.Token);
    }

    [Fact]
    public async Task WatchAsync_DoesNotMarkApiReady_WhileHttpResponseIsPending()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await StartServerAsync(cts.Token);
        server.BlockWatchResponses();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var watchEnumerator = service.WatchAsync<Container>(cancellationToken: watchCts.Token).GetAsyncEnumerator();
        var watchTask = watchEnumerator.MoveNextAsync().AsTask();
        await server.WaitForWatchRequestAsync(cts.Token);

        // Three conflict retries take longer than the steady-state budget but remain within the initialization budget.
        server.FailNextRequests(3);
        try
        {
            Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));
        }
        finally
        {
            watchCts.Cancel();
            await server.WaitForRequestCancellationAsync(cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
        }
    }

    [Fact]
    public async Task WatchAsync_RemainsActiveBeyondApiRetryTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var (service, kubeconfigPath, fileSystem) = CreateService(
            maxRetryDuration: TimeSpan.FromMilliseconds(100),
            kubernetesInitializationTimeout: TimeSpan.FromMilliseconds(250));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await StartServerAsync(cts.Token);
        server.BlockWatchResponses();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var watchEnumerator = service.WatchAsync<Container>(cancellationToken: watchCts.Token).GetAsyncEnumerator();
        var watchTask = watchEnumerator.MoveNextAsync().AsTask();
        await server.WaitForWatchRequestAsync(cts.Token);

        await server.WaitForWatchRequestCountAsync(2, cts.Token);
        Assert.False(watchTask.IsCompleted);

        watchCts.Cancel();
        await server.WaitForRequestCancellationAsync(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
    }

    // Verifies that establishing the connection survives a partially-written kubeconfig: when the file exists
    // but DCP has only flushed part of it (so it does not yet parse as a valid kubeconfig), the read is retried
    // and the operation succeeds once the complete, valid kubeconfig is written.
    [Fact]
    public async Task ExecuteWithRetry_EstablishesConnection_WhenKubeconfigInitiallyPartiallyWritten()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // Simulate DCP having flushed only the first part of the kubeconfig
        WritePartialKubeconfig(kubeconfigPath);

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        // Give the read pipeline time to observe and retry the partial file before we finish writing it.
        await Task.Delay(300, cts.Token);

        await using var server = await StartServerAsync(cts.Token);

        // Finish the write by appending the remainder onto the same file. In-flight DCP calls will now succeed.
        CompleteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    private static (KubernetesService Service, string KubeconfigPath, IDisposable FileSystem) CreateService(
        TimeSpan? maxRetryDuration = null,
        TimeSpan? kubernetesInitializationTimeout = null)
    {
        var configuration = new ConfigurationBuilder().Build();

        // Decouple the kubeconfig location from the production FileSystemService
        var fileSystem = new TestFileSystemService();
        try
        {
            var locations = new Locations(fileSystem);

            var dcpOptions = Options.Create(new DcpOptions
            {
                // Poll quickly so the kubeconfig file-wait/read retries react promptly in tests.
                KubernetesConfigReadRetryIntervalMilliseconds = 50,
                KubernetesConfigReadRetryCount = 300,
            });

            var service = new KubernetesService(NullLogger<KubernetesService>.Instance, dcpOptions, locations, configuration)
            {
                // Generous enough that the test can flip the kubeconfig before the retry budget is exhausted.
                MaxRetryDuration = maxRetryDuration ?? TimeSpan.FromSeconds(30),
                KubernetesInitializationTimeout = kubernetesInitializationTimeout ?? TimeSpan.FromSeconds(60),
            };

            return (service, locations.DcpKubeconfigPath, fileSystem);
        }
        catch
        {
            // Don't orphan the temp directory if wiring up the service fails before the test takes ownership.
            fileSystem.Dispose();
            throw;
        }
    }

    private static Task WriteStatusAsync(HttpContext context, int statusCode, string reason)
    {
        // Match Kubernetes API errors, for example:
        // {"kind":"Status","apiVersion":"v1","status":"Failure","reason":"Timeout","code":504}
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"{{reason}}","code":{{statusCode}}}""",
            context.RequestAborted);
    }

    private static void WriteKubeconfig(string path, int port)
    {
        // The fixture's self-signed certificate needs no machine trust installation.
        var content = string.Format(CultureInfo.InvariantCulture, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: dcp
              cluster:
                server: https://127.0.0.1:{0}
                insecure-skip-tls-verify: true
            contexts:
            - name: dcp
              context:
                cluster: dcp
                user: dcp
            current-context: dcp
            users:
            - name: dcp
              user:
                token: dcp-test-token
            """, port);

        // Write atomically (temp file + move on the same volume) so a concurrent read by the service never
        // observes a half-written file.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void WritePartialKubeconfig(string path)
    {
        // A genuine prefix of the final kubeconfig that stops in the middle of the double-quoted server value.
        // The unterminated quote makes this deterministically fail YAML parsing, which models DCP having only
        // flushed part of the file. There is intentionally no trailing newline, so appending the remainder in
        // CompleteKubeconfig closes the quote and yields exactly-valid YAML.
        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: dcp
              cluster:
                insecure-skip-tls-verify: true
                server: "https://127.0.0.1:
            """);
    }

    private static void CompleteKubeconfig(string path, int port)
    {
        // Append (do not rewrite) the remainder of the kubeconfig that WritePartialKubeconfig left unfinished.
        // The remainder begins by closing the open server scalar ({port}") so the combined file parses as a
        // valid kubeconfig pointing at the HTTPS loopback fake server.
        File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture, """
            {0}"
            contexts:
            - name: dcp
              context:
                cluster: dcp
                user: dcp
            current-context: dcp
            users:
            - name: dcp
              user:
                token: dcp-test-token
            """, port));
    }

    // A self-contained IFileSystemService for these tests. It hands Locations a single, uniquely-suffixed temp
    // directory that the test owns, decoupling the kubeconfig location from the production FileSystemService so
    // concurrent runs on the same machine never share a path. Every file a test writes lives under this root, so
    // disposing the fake (always, via `using`) removes the kubeconfig and any partial/temp files regardless of the
    // test outcome.
    private sealed class TestFileSystemService : IFileSystemService, IDisposable
    {
        private readonly TestTempFileSystemService _tempDirectory = new();

        public ITempFileSystemService TempDirectory => _tempDirectory;

        public void Dispose() => _tempDirectory.Dispose();

        private sealed class TestTempFileSystemService : ITempFileSystemService, IDisposable
        {
            private string? _root;

            public TempDirectory CreateTempSubdirectory(string? prefix = null)
            {
                // Created lazily (Locations calls this exactly once) so the directory can't be orphaned if the
                // test fails to take ownership, and with a random suffix so each test instance is isolated.
                _root ??= Directory.CreateTempSubdirectory("test-kubeconfig-").FullName;
                return new TestTempDirectory(_root);
            }

            public TempFile CreateTempFile(string? fileName = null)
                => throw new NotSupportedException("The kubeconfig tests only allocate a temp subdirectory.");

            public void Dispose()
            {
                if (_root is null)
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(_root))
                    {
                        Directory.Delete(_root, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup; a teardown failure must never mask the test result.
                }
            }
        }

        // The owning TestTempFileSystemService deletes the root recursively on Dispose, 
        // so this handle has nothing of its own to release.
        private sealed class TestTempDirectory(string path) : TempDirectory
        {
            public override string Path => path;

            public override void Dispose()
            {
            }
        }
    }

    // A minimal stand-in for the DCP API server. It can return conflicts for a configured number of requests,
    // then answers with an empty Kubernetes list that ListAsync<Container>() can deserialize successfully.
    //
    // It runs a real Kestrel server bound to port 0 so the OS assigns a free port that Kestrel actually binds and
    // holds for the lifetime of the server. The bound port is read back after startup. This avoids the classic
    // "probe a free port then release it and hope nobody grabs it before we rebind" race.
    private sealed class TestDcpApiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ResponseState _responseState;

        private TestDcpApiServer(WebApplication app, int port, ResponseState responseState)
        {
            _app = app;
            _responseState = responseState;
            Port = port;
        }

        public int Port { get; }

        public void FailNextRequests(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Volatile.Write(
                ref _responseState.SuccessfulRequestNumber,
                Volatile.Read(ref _responseState.RequestCount) + count + 1);
        }

        public void DelayResponses(TimeSpan delay)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
            Interlocked.Exchange(ref _responseState.ResponseDelayTicks, delay.Ticks);
        }

        public void BlockWatchResponses()
        {
            Volatile.Write(ref _responseState.BlockWatchResponses, true);
        }

        public Task WaitForWatchRequestAsync(CancellationToken cancellationToken)
        {
            return _responseState.WatchRequestArrived.Task.WaitAsync(cancellationToken);
        }

        public async Task WaitForWatchRequestCountAsync(int expectedCount, CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _responseState.WatchRequestCount) < expectedCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }

        public Task WaitForRequestCancellationAsync(CancellationToken cancellationToken)
        {
            return _responseState.RequestCancellationObserved.Task.WaitAsync(cancellationToken);
        }

        public static async Task<TestDcpApiServer> StartAsync(
            X509Certificate2 certificate,
            CancellationToken cancellationToken = default,
            int successfulRequestNumber = 1,
            Func<HttpContext, Task>? requestHandler = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            // Keep the test output clean; the fake server's logs are noise.
            builder.Logging.ClearProviders();
            // Port 0 lets the OS pick a free port that Kestrel binds and holds. After StartAsync the addresses
            // feature (exposed via app.Urls) is rewritten with the resolved address, so we can read the real port.
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint =>
            {
                endpoint.Protocols = HttpProtocols.Http2;
                endpoint.UseHttps(certificate);
            }));

            var app = builder.Build();
            var responseState = new ResponseState
            {
                SuccessfulRequestNumber = successfulRequestNumber,
            };

            app.Run(async context =>
            {
                Assert.True(context.Request.IsHttps);
                Assert.Equal("HTTP/2", context.Request.Protocol);
                if (requestHandler is not null)
                {
                    await requestHandler(context);
                    return;
                }

                var isWatchRequest = context.Request.Query.TryGetValue("watch", out var watchValues)
                    && string.Equals(watchValues.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                if (isWatchRequest && Volatile.Read(ref responseState.BlockWatchResponses))
                {
                    Interlocked.Increment(ref responseState.WatchRequestCount);
                    responseState.WatchRequestArrived.TrySetResult(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        responseState.RequestCancellationObserved.TrySetResult(true);
                        throw;
                    }
                }

                var responseDelayTicks = Interlocked.Read(ref responseState.ResponseDelayTicks);
                if (responseDelayTicks > 0)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromTicks(responseDelayTicks), context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        responseState.RequestCancellationObserved.TrySetResult(true);
                        throw;
                    }
                }

                if (Interlocked.Increment(ref responseState.RequestCount) < Volatile.Read(ref responseState.SuccessfulRequestNumber))
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
            });

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // e.g. "https://127.0.0.1:54321" -> 54321
            var address = app.Urls.First();
            var port = new Uri(address).Port;

            return new TestDcpApiServer(app, port, responseState);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class ResponseState
        {
            public bool BlockWatchResponses;
            public int RequestCount;
            public int WatchRequestCount;
            public TaskCompletionSource<bool> RequestCancellationObserved { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public long ResponseDelayTicks;
            public int SuccessfulRequestNumber;
            public TaskCompletionSource<bool> WatchRequestArrived { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
