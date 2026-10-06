// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // IFileSystemService is for evaluation purposes only.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Aspire.Hosting.Tests.Dcp;

public class KubernetesServiceTests
{
    [Fact]
    public async Task CreateAsync_DoesNotReadOrRetrySuccessfulCreate()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            var created = await ReadContainerAsync(context);
            created.Metadata.Uid = "created-object";
            await WriteContainerAsync(context, created, StatusCodes.Status201Created);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("created-object", result.Metadata.Uid);
        Assert.Equal(["POST"], requests.ToArray());
    }

    [Fact]
    public async Task DcpKubernetesClient_UsesCallerDeadlineInsteadOfSdkTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            var created = await ReadContainerAsync(context);
            created.Metadata.Uid = "created-object";
            await Task.Delay(TimeSpan.FromMilliseconds(500), context.RequestAborted);
            await WriteContainerAsync(context, created, StatusCodes.Status201Created);
        });
        using var client = new DcpKubernetesClient(new KubernetesClientConfiguration
        {
            Host = $"http://127.0.0.1:{server.Port}",
            HttpClientTimeout = TimeSpan.FromMilliseconds(100),
        });

        using var response = await client.CustomObjects.CreateClusterCustomObjectWithHttpMessagesAsync(
            Container.Create("frontend", "test-image"),
            "usvc-dev.developer.microsoft.com",
            "v1",
            "containers",
            cancellationToken: cts.Token);
        var result = KubernetesJson.Deserialize<Container>(response.Body.ToString());

        Assert.Equal("created-object", result.Metadata.Uid);
        Assert.Equal(["POST"], requests.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test-namespace")]
    public async Task CreateAsync_ReconcilesGatewayTimeoutAfterCommit(string? namespaceParameter)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        Container? stored = null;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var created = await ReadContainerAsync(context);
                created.Metadata.Uid = "created-object";
                Volatile.Write(ref stored, created);
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                return;
            }

            await WriteContainerAsync(context, Volatile.Read(ref stored)!);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);
        var container = Container.Create("frontend", "test-image");
        container.Metadata.NamespaceProperty = namespaceParameter;

        var result = await service.CreateAsync(container, cts.Token);

        Assert.Equal("created-object", result.Metadata.Uid);
        Assert.Equal("frontend", result.Metadata.Name);
        Assert.Equal(namespaceParameter, result.Metadata.NamespaceProperty);
        var collectionPath = namespaceParameter is null
            ? "/apis/usvc-dev.developer.microsoft.com/v1/containers"
            : $"/apis/usvc-dev.developer.microsoft.com/v1/namespaces/{namespaceParameter}/containers";
        Assert.Equal([$"POST {collectionPath}", $"GET {collectionPath}/frontend"], requests.ToArray());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task CreateAsync_RetriesTransientFailureAfterConfirmingObjectAbsent(int statusCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var postedObjects = new ConcurrentQueue<Container>();
        var postCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
                return;
            }

            var created = await ReadContainerAsync(context);
            postedObjects.Enqueue(created);
            if (Interlocked.Increment(ref postCount) == 1)
            {
                await WriteStatusAsync(context, statusCode, "TransientFailure");
                return;
            }

            created.Metadata.Uid = "retried-object";
            await WriteContainerAsync(context, created, StatusCodes.Status201Created);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);
        var container = Container.Create("frontend", "test-image");
        container.Annotate("existing-annotation", "preserved");

        var result = await service.CreateAsync(container, cts.Token);

        Assert.Equal("retried-object", result.Metadata.Uid);
        Assert.Equal(["POST", "GET", "POST"], requests.ToArray());
        Assert.Collection(postedObjects,
            first =>
            {
                Assert.Equal("frontend", first.Metadata.Name);
                Assert.Equal("test-image", first.Spec.Image);
                Assert.Equal(container.Metadata.Annotations, first.Metadata.Annotations);
            },
            second =>
            {
                Assert.Equal("frontend", second.Metadata.Name);
                Assert.Equal("test-image", second.Spec.Image);
                Assert.Equal(container.Metadata.Annotations, second.Metadata.Annotations);
            });
    }

    [Fact]
    public async Task CreateAsync_ReconcilesAlreadyExistsWhenOriginalCreateCommitsAfterNotFound()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        Container? original = null;
        var postCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var created = await ReadContainerAsync(context);
                if (Interlocked.Increment(ref postCount) == 1)
                {
                    created.Metadata.Uid = "late-original";
                    Volatile.Write(ref original, created);
                    await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                }
                else
                {
                    Assert.Equal(Volatile.Read(ref original)!.Metadata.Name, created.Metadata.Name);
                    Assert.Equal(Volatile.Read(ref original)!.Spec.Image, created.Spec.Image);
                    await WriteStatusAsync(context, StatusCodes.Status409Conflict, "AlreadyExists");
                }

                return;
            }

            if (Volatile.Read(ref postCount) == 1)
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
                return;
            }

            await WriteContainerAsync(context, Volatile.Read(ref original)!);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("late-original", result.Metadata.Uid);
        Assert.Equal(["POST", "GET", "POST", "GET"], requests.ToArray());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task CreateAsync_RetriesCreateAfterTransientConfirmationFailure(int confirmationStatusCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        Container? stored = null;
        var postCount = 0;
        var getCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var created = await ReadContainerAsync(context);
                if (Interlocked.Increment(ref postCount) == 1)
                {
                    created.Metadata.Uid = "original-object";
                    Volatile.Write(ref stored, created);
                    await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                }
                else
                {
                    await WriteStatusAsync(context, StatusCodes.Status409Conflict, "AlreadyExists");
                }
            }
            else if (Interlocked.Increment(ref getCount) == 1)
            {
                await WriteStatusAsync(context, confirmationStatusCode, "TransientFailure");
            }
            else
            {
                await WriteContainerAsync(context, Volatile.Read(ref stored)!);
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("original-object", result.Metadata.Uid);
        Assert.Equal(["POST", "GET", "POST", "GET"], requests.ToArray());
    }

    [Theory]
    [InlineData("""{"kind":"Status","reason":"Conflict","code":409}""")]
    [InlineData("""{"kind":"Status","reason":409,"code":409}""")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task CreateAsync_DoesNotConfirmOtherConflictsAfterAmbiguousCreate(string errorBody)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var postCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
            }
            else if (Interlocked.Increment(ref postCount) == 1)
            {
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(errorBody, context.RequestAborted);
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var exception = await Assert.ThrowsAsync<HttpOperationException>(
            () => service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token));

        Assert.Equal(HttpStatusCode.Conflict, exception.Response.StatusCode);
        Assert.Equal(["POST", "GET", "POST"], requests.ToArray());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task CreateAsync_DoesNotRetryPermanentFailureOrInitialConflict(int statusCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            await WriteStatusAsync(context, statusCode, statusCode == 409 ? "AlreadyExists" : "PermanentFailure");
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var exception = await Assert.ThrowsAsync<HttpOperationException>(
            () => service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token));

        Assert.Equal((HttpStatusCode)statusCode, exception.Response.StatusCode);
        Assert.Equal(["POST"], requests.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_ReconcilesExistingObjectByName(bool hasAnnotations)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                return;
            }

            var existing = Container.Create("frontend", "test-image");
            existing.Metadata.Uid = "existing-object";
            if (hasAnnotations)
            {
                existing.Annotate("existing-annotation", "preserved");
            }

            await WriteContainerAsync(context, existing);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("existing-object", result.Metadata.Uid);
        if (hasAnnotations)
        {
            Assert.Equal("preserved", result.Metadata.Annotations["existing-annotation"]);
        }

        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_RecoversAfterAttemptTimeout(bool committedBeforeTimeout)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4.5),
            createRecoveryTimeout: TimeSpan.FromSeconds(10));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var canceledRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Container? stored = null;
        var postCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/containers", StringComparison.Ordinal) &&
                HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
                return;
            }

            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (Volatile.Read(ref stored) is { } existing)
                {
                    await WriteContainerAsync(context, existing);
                }
                else
                {
                    await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
                }

                return;
            }

            var created = await ReadContainerAsync(context);
            if (Interlocked.Increment(ref postCount) == 1)
            {
                if (committedBeforeTimeout)
                {
                    Volatile.Write(ref stored, created);
                }

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    canceledRequest.TrySetResult();
                    throw;
                }
            }
            else
            {
                await WriteContainerAsync(context, created, StatusCodes.Status201Created);
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);
        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        await canceledRequest.Task.WaitAsync(cts.Token);
        Assert.Equal("frontend", result.Metadata.Name);
        Assert.Equal(committedBeforeTimeout ? ["POST", "GET"] : ["POST", "GET", "POST"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_ReconcilesLostResponseAfterCommit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        Container? stored = null;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                Volatile.Write(ref stored, await ReadContainerAsync(context));
                context.Abort();
                return;
            }

            await WriteContainerAsync(context, Volatile.Read(ref stored)!);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("frontend", result.Metadata.Name);
        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_RetriesCreateAfterConfirmationTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            createRecoveryTimeout: TimeSpan.FromSeconds(10));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Container? stored = null;
        var postCount = 0;
        var getCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Value!.EndsWith("/containers", StringComparison.Ordinal))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
                return;
            }

            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var created = await ReadContainerAsync(context);
                if (Interlocked.Increment(ref postCount) == 1)
                {
                    created.Metadata.Uid = "original-object";
                    Volatile.Write(ref stored, created);
                    await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                }
                else
                {
                    await WriteStatusAsync(context, StatusCodes.Status409Conflict, "AlreadyExists");
                }

                return;
            }

            if (Interlocked.Increment(ref getCount) == 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    requestCanceled.TrySetResult();
                    throw;
                }

                return;
            }

            await WriteContainerAsync(context, Volatile.Read(ref stored)!);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);
        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        await requestCanceled.Task.WaitAsync(cts.Token);
        Assert.Equal("original-object", result.Metadata.Uid);
        Assert.Equal(["POST", "GET", "POST", "GET"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_RetriesWhenAlreadyExistsCannotBeConfirmed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var postCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
                return;
            }

            var created = await ReadContainerAsync(context);
            switch (Interlocked.Increment(ref postCount))
            {
                case 1:
                    await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                    break;
                case 2:
                    await WriteStatusAsync(context, StatusCodes.Status409Conflict, "AlreadyExists");
                    break;
                default:
                    created.Metadata.Uid = "retried-object";
                    await WriteContainerAsync(context, created, StatusCodes.Status201Created);
                    break;
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("retried-object", result.Metadata.Uid);
        Assert.Equal(["POST", "GET", "POST", "GET", "POST"], requests.ToArray());
    }

    [Theory]
    [InlineData(false, 3600)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, 0)]
    public async Task CreateAsync_RecoveryBudgetIncludesServerRequestedRetryDelay(bool useHttpDate, int retryAfterSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var recoveryTimeout = TimeSpan.FromMilliseconds(500);
        var (service, kubeconfigPath, fileSystem) = CreateService(createRecoveryTimeout: recoveryTimeout);
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
                return;
            }

            context.Response.Headers.RetryAfter = useHttpDate
                ? DateTimeOffset.UtcNow.AddHours(1).ToString("r", CultureInfo.InvariantCulture)
                : retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token));

        Assert.Equal(recoveryTimeout, exception.Timeout);
        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Theory]
    [InlineData(false, 3600)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, 0)]
    public async Task CreateAsync_RecoveryBudgetIncludesConfirmationRetryDelay(bool useHttpDate, int retryAfterSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var recoveryTimeout = TimeSpan.FromMilliseconds(500);
        var (service, kubeconfigPath, fileSystem) = CreateService(createRecoveryTimeout: recoveryTimeout);
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                return;
            }

            context.Response.Headers.RetryAfter = useHttpDate
                ? DateTimeOffset.UtcNow.AddHours(1).ToString("r", CultureInfo.InvariantCulture)
                : retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await WriteStatusAsync(context, StatusCodes.Status429TooManyRequests, "TooManyRequests");
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token));

        Assert.Equal(recoveryTimeout, exception.Timeout);
        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_ConfirmationTimeCountsTowardRetryAfter(bool useHttpDate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(createRecoveryTimeout: TimeSpan.FromSeconds(4.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var postCount = 0;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Value!.EndsWith("/containers", StringComparison.Ordinal))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""");
                return;
            }

            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                // Both retry hints expire before confirmation finishes. Restarting the original delay
                // after this read would exhaust the recovery budget before the second POST.
                await Task.Delay(TimeSpan.FromSeconds(3.2), context.RequestAborted);
                await WriteStatusAsync(context, StatusCodes.Status404NotFound, "NotFound");
                return;
            }

            var created = await ReadContainerAsync(context);
            if (Interlocked.Increment(ref postCount) == 1)
            {
                context.Response.Headers.RetryAfter = useHttpDate
                    ? DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3).ToString("r", CultureInfo.InvariantCulture)
                    : "2";
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                return;
            }

            created.Metadata.Uid = "retried-object";
            await WriteContainerAsync(context, created, StatusCodes.Status201Created);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);
        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));

        var result = await service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        Assert.Equal("retried-object", result.Metadata.Uid);
        Assert.Equal(["POST", "GET", "POST"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_InitializationTimeoutDoesNotConfirmOrAdoptExistingObject()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromMilliseconds(500),
            createRecoveryTimeout: TimeSpan.FromSeconds(5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                var existing = Container.Create("frontend", "test-image");
                existing.Metadata.Uid = "pre-existing-object";
                await WriteContainerAsync(context, existing);
            }
            else
            {
                await WriteStatusAsync(context, StatusCodes.Status409Conflict, "AlreadyExists");
            }
        });

        var createTask = service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);

        // Let the first initialization budget expire before making kubeconfig available.
        await Task.Delay(TimeSpan.FromSeconds(1.2), cts.Token);
        Assert.Empty(requests);
        WriteKubeconfig(kubeconfigPath, server.Port);
        var exception = await Assert.ThrowsAsync<HttpOperationException>(() => createTask);

        Assert.Equal(HttpStatusCode.Conflict, exception.Response.StatusCode);
        Assert.Equal(["POST"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_RecoveryBudgetCancelsStalledReconciliation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var recoveryTimeout = TimeSpan.FromSeconds(2);
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromSeconds(1),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4),
            createRecoveryTimeout: recoveryTimeout);
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
                throw;
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token));

        await requestCanceled.Task.WaitAsync(cts.Token);
        Assert.Equal(recoveryTimeout, exception.Timeout);
        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_DoesNotRetryPermanentReconciliationFailure()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            await WriteStatusAsync(context,
                HttpMethods.IsPost(context.Request.Method) ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status403Forbidden,
                "Failure");
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var exception = await Assert.ThrowsAsync<HttpOperationException>(
            () => service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token));

        Assert.Equal(HttpStatusCode.Forbidden, exception.Response.StatusCode);
        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Fact]
    public async Task CreateAsync_CallerCancellationDoesNotReconcileOrRetry()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var createCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var requestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            requestArrived.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
                throw;
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var createTask = service.CreateAsync(Container.Create("frontend", "test-image"), createCts.Token);
        await requestArrived.Task.WaitAsync(cts.Token);
        createCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => createTask);
        await requestCanceled.Task.WaitAsync(cts.Token);
        Assert.Equal(["POST"], requests.ToArray());
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

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesInitializationTimeout_WhenKubeconfigAppearsAfterApiRetryBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromSeconds(1));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);

        // Kubeconfig availability is not proof of API readiness, so the operation starts with
        // the initialization budget rather than the shorter steady-state budget.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteWithRetry_InitializationBudgetIncludesApiTimeoutPlusAdditionalTime()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromSeconds(1),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(1));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        server.DelayResponses(TimeSpan.FromSeconds(1.5));
        WriteKubeconfig(kubeconfigPath, server.Port);

        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
        await server.WaitForRequestCancellationAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteWithRetry_ZeroInitializationAllowanceUsesApiBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromSeconds(1),
            kubernetesInitializationAdditionalTimeout: TimeSpan.Zero);
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        server.DelayResponses(TimeSpan.FromSeconds(2));
        WriteKubeconfig(kubeconfigPath, server.Port);

        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
        await server.WaitForRequestCancellationAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteWithRetry_ClientSetupAndApiRequestShareInitializationBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromSeconds(1),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(3));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        server.DelayResponses(TimeSpan.FromSeconds(3));

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        // Setup and the HTTP response each fit individually, but their sum exceeds the shared budget.
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => listTask);
        await server.WaitForRequestCancellationAsync(cts.Token);
    }

    [Fact]
    public async Task CreateAsync_ConfirmsTimedOutWrite_WhenSetupConsumesInitializationBudget()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromSeconds(1),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(3),
            createRecoveryTimeout: TimeSpan.FromSeconds(10));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Container? stored = null;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue(context.Request.Method);
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await WriteContainerAsync(context, Volatile.Read(ref stored)!);
                return;
            }

            var created = await ReadContainerAsync(context);
            created.Metadata.Uid = "committed-object";
            Volatile.Write(ref stored, created);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);
                await WriteContainerAsync(context, created, StatusCodes.Status201Created);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
                throw;
            }
        });

        var createTask = service.CreateAsync(Container.Create("frontend", "test-image"), cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await createTask;

        await requestCanceled.Task.WaitAsync(cts.Token);
        Assert.Equal("committed-object", result.Metadata.Uid);
        Assert.Equal(["POST", "GET"], requests.ToArray());
    }

    [Fact]
    public async Task ExecuteWithRetry_UsesInitializationTimeout_UntilFirstApiOperationSucceeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        // The fourth API request succeeds after exponential retry delays of 100, 200, and 400 milliseconds.
        // It is reachable under the initialization budget but not the 500-millisecond steady-state budget.
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, successfulRequestNumber: 4);
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
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);

        var result = await service.ListAsync<Container>(cancellationToken: cts.Token);
        Assert.Empty(result);

        server.FailNextRequests(3);
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData(nameof(KubernetesService.ListAsync))]
    [InlineData(nameof(KubernetesService.GetAsync))]
    [InlineData(nameof(KubernetesService.PatchAsync))]
    [InlineData(nameof(KubernetesService.DeleteAsync))]
    public async Task ExecuteWithRetry_CancelsApiRequest_WhenRetryDurationExpires(string operationName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        WriteKubeconfig(kubeconfigPath, server.Port);
        Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));

        server.DelayResponses(TimeSpan.FromSeconds(2));
        Func<Task> operation = operationName switch
        {
            nameof(KubernetesService.ListAsync) => () => service.ListAsync<Container>(cancellationToken: cts.Token),
            nameof(KubernetesService.GetAsync) => () => service.GetAsync<Container>("frontend", cancellationToken: cts.Token),
            nameof(KubernetesService.PatchAsync) => () => service.PatchAsync(Container.Create("frontend", "test-image"), new V1Patch("[]", V1Patch.PatchType.JsonPatch), cts.Token),
            nameof(KubernetesService.DeleteAsync) => () => service.DeleteAsync<Container>("frontend", cancellationToken: cts.Token),
            _ => throw new InvalidOperationException($"Unknown API operation {operationName}."),
        };

        await Assert.ThrowsAsync<TimeoutRejectedException>(operation);
        await server.WaitForRequestCancellationAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteWithRetry_ReadinessDoesNotShortenInFlightOperation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            if (context.Request.Path.Value!.EndsWith("/readiness-probe", StringComparison.Ordinal))
            {
                await WriteContainerAsync(context, Container.Create("readiness-probe", "test-image"));
                return;
            }

            requestArrived.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"apiVersion":"usvc-dev.developer.microsoft.com/v1","kind":"ContainerList","items":[]}""", context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
                throw;
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var listTask = service.ListAsync<Container>(cancellationToken: cts.Token);
        await requestArrived.Task.WaitAsync(cts.Token);
        var probe = await service.GetAsync<Container>("readiness-probe", cancellationToken: cts.Token);

        Assert.Equal("readiness-probe", probe.Metadata.Name);
        Assert.Empty(await listTask);
        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => service.ListAsync<Container>(cancellationToken: cts.Token));
        await requestCanceled.Task.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task WatchAsync_DoesNotMarkApiReady_WhileHttpResponseIsPending()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(4.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
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
    public async Task WatchAsync_ReconnectUsesApiTimeout_AfterAnotherOperationConfirmsReadiness()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(250),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(2.75));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
        server.BlockWatchResponses();
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var watchEnumerator = service.WatchAsync<Container>(cancellationToken: watchCts.Token).GetAsyncEnumerator();
        var watchTask = watchEnumerator.MoveNextAsync().AsTask();
        try
        {
            await server.WaitForWatchRequestAsync(cts.Token);
            Assert.Empty(await service.ListAsync<Container>(cancellationToken: cts.Token));
            await server.WaitForWatchRequestCountAsync(2, cts.Token);

            // The next stalled connection must use the shorter budget rather than another startup budget.
            using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            reconnectCts.CancelAfter(TimeSpan.FromSeconds(1.5));
            await server.WaitForWatchRequestCountAsync(3, reconnectCts.Token);
            Assert.False(watchTask.IsCompleted);
        }
        finally
        {
            watchCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
        }
    }

    [Fact]
    public async Task WatchAsync_ConnectedStreamOutlivesApiTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(1.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            var container = Container.Create("frontend", "test-image");
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                $$"""{"type":"ADDED","object":{{KubernetesJson.Serialize(container)}}}""" + "\n",
                context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                requestCanceled.TrySetResult();
                throw;
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        await using var watchEnumerator = service.WatchAsync<Container>(cancellationToken: watchCts.Token).GetAsyncEnumerator();
        Assert.True(await watchEnumerator.MoveNextAsync());
        Assert.Equal(WatchEventType.Added, watchEnumerator.Current.Item1);
        Assert.Equal("frontend", watchEnumerator.Current.Item2.Metadata.Name);
        var watchTask = watchEnumerator.MoveNextAsync().AsTask();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
            Assert.False(watchTask.IsCompleted);
        }
        finally
        {
            watchCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchTask);
        }

        await requestCanceled.Task.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task GetLogStreamAsync_ConnectedStreamOutlivesApiTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(500),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromSeconds(1.5));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var continueStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await TestDcpApiServer.StartAsync(cts.Token, requestHandler: async context =>
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("first\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await continueStream.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("second\n", context.RequestAborted);
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        try
        {
            await using var stream = await service.GetLogStreamAsync(Container.Create("frontend", "test-image"), "stdout", cts.Token);
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal("first", await reader.ReadLineAsync(cts.Token));
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
            continueStream.TrySetResult();
            Assert.Equal("second", await reader.ReadLineAsync(cts.Token));
        }
        finally
        {
            continueStream.TrySetResult();
        }
    }

    [Fact]
    public async Task WatchAsync_RemainsActiveBeyondApiRetryTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        var (service, kubeconfigPath, fileSystem) = CreateService(
            kubernetesApiTimeout: TimeSpan.FromMilliseconds(100),
            kubernetesInitializationAdditionalTimeout: TimeSpan.FromMilliseconds(150));
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);
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

        await using var server = await TestDcpApiServer.StartAsync(cts.Token);

        // Finish the write by appending the remainder onto the same file. In-flight DCP calls will now succeed.
        CompleteKubeconfig(kubeconfigPath, server.Port);

        var result = await listTask;
        Assert.Empty(result);
    }

    private static (KubernetesService Service, string KubeconfigPath, IDisposable FileSystem) CreateService(
        TimeSpan? kubernetesApiTimeout = null,
        TimeSpan? kubernetesInitializationAdditionalTimeout = null,
        TimeSpan? createRecoveryTimeout = null)
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
                KubernetesApiTimeout = kubernetesApiTimeout ?? TimeSpan.FromSeconds(40),
                KubernetesInitializationAdditionalTimeout = kubernetesInitializationAdditionalTimeout ?? TimeSpan.FromSeconds(20),
                KubernetesCreateRecoveryTimeout = createRecoveryTimeout ?? TimeSpan.FromMinutes(2),
            });

            var service = new KubernetesService(NullLogger<KubernetesService>.Instance, dcpOptions, locations, configuration);

            return (service, locations.DcpKubeconfigPath, fileSystem);
        }
        catch
        {
            // Don't orphan the temp directory if wiring up the service fails before the test takes ownership.
            fileSystem.Dispose();
            throw;
        }
    }

    private static async Task<Container> ReadContainerAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        return KubernetesJson.Deserialize<Container>(await reader.ReadToEndAsync(context.RequestAborted));
    }

    private static Task WriteContainerAsync(HttpContext context, Container container, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(KubernetesJson.Serialize(container), context.RequestAborted);
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
        // Minimal kubeconfig pointing at a plain-HTTP loopback endpoint with no auth, which is all the
        // DcpKubernetesClient needs to issue custom-object requests against the fake server.
        var content = string.Format(CultureInfo.InvariantCulture, """
            apiVersion: v1
            kind: Config
            clusters:
            - name: dcp
              cluster:
                server: http://127.0.0.1:{0}
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
                server: "http://127.0.0.1:
            """);
    }

    private static void CompleteKubeconfig(string path, int port)
    {
        // Append (do not rewrite) the remainder of the kubeconfig that WritePartialKubeconfig left unfinished.
        // The remainder begins by closing the open server scalar ({port}") so the combined file parses as a
        // valid kubeconfig pointing at the loopback fake server with no auth.
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
            CancellationToken cancellationToken = default,
            int successfulRequestNumber = 1,
            Func<HttpContext, Task>? requestHandler = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            // Keep the test output clean; the fake server's logs are noise.
            builder.Logging.ClearProviders();
            // Port 0 lets the OS pick a free port that Kestrel binds and holds. After StartAsync the addresses
            // feature (exposed via app.Urls) is rewritten with the resolved address, so we can read the real port.
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            var responseState = new ResponseState
            {
                SuccessfulRequestNumber = successfulRequestNumber,
            };

            app.Run(async context =>
            {
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

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            // e.g. "http://127.0.0.1:54321" -> 54321
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
