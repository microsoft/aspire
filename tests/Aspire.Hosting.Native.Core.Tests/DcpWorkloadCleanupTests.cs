// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Server;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;
using static Aspire.Hosting.Native.Core.Tests.TestServices.WorkspaceRpcTestContext;

namespace Aspire.Hosting.Native.Core.Tests;

public class DcpWorkloadCleanupTests
{
    [Fact]
    public async Task PreparedAllocationOwnsOnlyServicesUntilLaunchAndCleansUpWithoutStarting()
    {
        using var handler = new DcpCleanupTestHandler();
        await using var client = handler.CreateClient(new());
        var executor = new DcpWorkloadExecutor(client);
        var plan = new ContainerWorkload("example:1", 6379, [], []);
        var prepared = await executor.PrepareAsync(new(Guid.NewGuid(), Guid.NewGuid()), plan,
            TestContext.Current.CancellationToken);
        Assert.Equal("127.0.0.1", prepared.Endpoint.Host);
        Assert.Equal(5050, prepared.Endpoint.Port);
        Assert.StartsWith("services/", Assert.Single(handler.Resources.Keys));
        await prepared.DisposeAsync();
        Assert.Empty(handler.Resources);
    }

    [Fact]
    public async Task PreparedAllocationRejectsEndpointChangesAndRepeatedLaunch()
    {
        using var handler = new DcpCleanupTestHandler();
        await using var client = handler.CreateClient(new());
        var executor = new DcpWorkloadExecutor(client);
        var plan = new ContainerWorkload("example:1", 6379, [], []);
        await using var prepared = await executor.PrepareAsync(new(Guid.NewGuid(), Guid.NewGuid()), plan,
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.StartAsync(
            plan with { Ports = [] }, TestContext.Current.CancellationToken));
        Assert.StartsWith("services/", Assert.Single(handler.Resources.Keys));
        await prepared.StartAsync(plan, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Resources.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            prepared.StartAsync(plan, TestContext.Current.CancellationToken));
        await prepared.DisposeAsync();
        Assert.Empty(handler.Resources);
    }

    [Fact]
    public async Task WorkspaceRetirementRetainsOnlyCleanupAuthorityUntilFailedDeletionIsRetried()
    {
        var options = new NativeServerOptions { CleanupTimeout = TimeSpan.FromSeconds(10) };
        using var handler = new DcpCleanupTestHandler();
        var attempts = 0;
        // Observation cleanup and the first StopAsync retry must both fail so
        // the next workspace retirement owns the retained deletion obligation.
        handler.BeforeDelete = (_, _) => Interlocked.Increment(ref attempts) <= 2
            ? Task.FromException(new HttpRequestException("Transient failure"))
            : Task.CompletedTask;
        await using var client = handler.CreateClient(options);
        using var context = new WorkspaceRpcTestContext(new DcpWorkloadExecutor(client, Task.CompletedTask, options));
        EnsureSuccess(await context.Integration.InvokeAsync("startContainer", Arguments(
            ("context", context.Writer), ("container", new JsonObject
            {
                ["image"] = "example:1", ["targetPort"] = 6379,
                ["environment"] = new JsonArray(),
                ["arguments"] = new JsonArray()
            }))));
        var first = await context.Author.InvokeAsync("retireApplicationWorkspace", Arguments(("context", context.Workspace)));
        Assert.Equal("INTERNAL_ERROR", Error(first));
        Assert.Equal(2, handler.Resources.Count);
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("beginRevision", Arguments(("context", context.Workspace)))));
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("inviteApplicationWorkspace", Arguments(("context", context.Workspace)))));
        EnsureSuccess(await context.Author.InvokeAsync("retireApplicationWorkspace", Arguments(("context", context.Workspace))));
        Assert.Empty(handler.Resources);
        Assert.Equal(4, handler.Deletes.Count);
        Assert.Equal("HANDLE_NOT_FOUND", Error(context.Author.Invoke("retireApplicationWorkspace", Arguments(("context", context.Workspace)))));
    }

    [Fact]
    public async Task ConcurrentControllerCleanupSharesOneObligation()
    {
        using var handler = new DcpCleanupTestHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeCleanup = async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        };
        await using var client = handler.CreateClient(new());
        var first = client.CleanupResourcesAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = client.CleanupResourcesAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, second);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal("""{"status":"CleaningResources","shutdownResourceCleanup":"Full"}""",
            Assert.Single(handler.AdministrativeRequests));
        Assert.Equal("application/merge-patch+json", Assert.Single(handler.RequestContents).ContentType);
    }

    [Fact]
    public async Task ControllerCleanupRetriesAFailedObligation()
    {
        using var handler = new DcpCleanupTestHandler();
        var attempts = 0;
        handler.BeforeCleanup = _ => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new HttpRequestException("Transient failure"))
            : Task.CompletedTask;
        await using var client = handler.CreateClient(new());
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CleanupResourcesAsync(TestContext.Current.CancellationToken));
        await client.CleanupResourcesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.AdministrativeRequests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementRetriesFailedDeletionAndRemovesBothResources(bool timeout)
    {
        var clock = new FakeTimeProvider();
        var options = new NativeServerOptions { TimeProvider = clock, CleanupTimeout = TimeSpan.FromSeconds(2) };
        using var handler = new DcpCleanupTestHandler();
        var attempts = 0;
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeDelete = async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                if (timeout)
                {
                    cleanupStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
                }
                throw new HttpRequestException("Transient failure");
            }
        };
        await using var client = handler.CreateClient(options);
        using var model = new ApplicationModel();
        var resource = model.AddResource("cache", "example/Redis");
        using var runtime = new RuntimeGeneration(model.Seal(), new DcpWorkloadExecutor(client, Task.CompletedTask, options));
        using var owner = runtime.ClaimResource(resource);
        await owner.StartWorkload(new ContainerWorkload("example:1", 6379, [], []));
        owner.Dispose();
        if (timeout)
        {
            await cleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(options.CleanupTimeout - TimeSpan.FromTicks(1));
            Assert.False(runtime.Completion.IsCompleted);
            clock.Advance(TimeSpan.FromTicks(1));
        }
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.Completion);
        Assert.Equal(2, handler.Resources.Count);
        runtime.Dispose();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(handler.Resources);
        Assert.Equal(3, handler.Deletes.Count);
    }

    [Fact]
    public async Task RetirementRetriesTheRemovedOwnerAfterRevisionCleanupFails()
    {
        var options = new NativeServerOptions { CleanupTimeout = TimeSpan.FromSeconds(10) };
        using var handler = new DcpCleanupTestHandler();
        var attempts = 0;
        handler.BeforeDelete = (_, _) =>
            Interlocked.Increment(ref attempts) <= 2
                ? Task.FromException(new HttpRequestException("Transient failure"))
                : Task.CompletedTask;
        await using var client = handler.CreateClient(options);
        using var model = new ApplicationModel();
        var resource = model.AddResource("cache", "example/Redis");
        using var runtime = new RuntimeGeneration(model.Seal(), new DcpWorkloadExecutor(client, Task.CompletedTask, options));
        using var owner = runtime.ClaimResource(resource);
        await owner.StartWorkload(new ContainerWorkload("example:1", 6379, [], []));
        using var revision = new ApplicationModel();
        await Assert.ThrowsAsync<HttpRequestException>(() => runtime.ApplyRevision(revision.Seal()));
        Assert.Equal(2, handler.Resources.Count);
        runtime.Dispose();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(handler.Resources);
        Assert.Equal(4, handler.Deletes.Count);
    }

    [Fact]
    public async Task FailedStartupRetainsItsPartiallyCreatedLeaseForRetirement()
    {
        var options = new NativeServerOptions { CleanupTimeout = TimeSpan.FromSeconds(10) };
        using var handler = new DcpCleanupTestHandler { Exited = true };
        var attempts = 0;
        handler.BeforeDelete = (_, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new HttpRequestException("Transient failure"))
                : Task.CompletedTask;
        await using var client = handler.CreateClient(options);
        using var model = new ApplicationModel();
        var resource = model.AddResource("cache", "example/Redis");
        using var runtime = new RuntimeGeneration(model.Seal(), new DcpWorkloadExecutor(client, Task.CompletedTask, options));
        using var owner = runtime.ClaimResource(resource);
        await Assert.ThrowsAsync<WorkloadCleanupException>(() =>
            owner.StartWorkload(new ContainerWorkload("example:1", 6379, [], [])));
        await Assert.ThrowsAsync<WorkloadCleanupException>(() => runtime.Completion);
        Assert.Equal(2, handler.Resources.Count);
        runtime.Dispose();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(handler.Resources);
    }

    [Fact]
    public async Task FailedAllocatedCommandRetainsPrimaryLeaseAndDrainsIndependentCleanupFailures()
    {
        var options = new NativeServerOptions { CleanupTimeout = TimeSpan.FromSeconds(10) };
        using var handler = new DcpCleanupTestHandler();
        var commandAttempts = 0;
        var primaryAttempts = 0;
        handler.BeforeDelete = (key, _) =>
            key.StartsWith("executables/", StringComparison.Ordinal)
                ? Interlocked.Increment(ref commandAttempts) == 1
                    ? Task.FromException(new HttpRequestException("Command cleanup failure"))
                    : Task.CompletedTask
                : Interlocked.Increment(ref primaryAttempts) == 1
                    ? Task.FromException(new HttpRequestException("Primary cleanup failure"))
                    : Task.CompletedTask;
        await using var client = handler.CreateClient(options);
        var executor = new DcpWorkloadExecutor(client, Task.CompletedTask, options);
        var retirementCalls = 0;
        using var workload = new ResourceWorkload(new(Guid.NewGuid(), Guid.NewGuid()),
            () => new ContainerWorkload("example:1", 6379, [], []), executor, options.Runtime,
            _ => Task.CompletedTask, (_, _) => { }, (_, _) => { }, _ => Task.CompletedTask,
            async (_, token) =>
            {
                handler.Exited = true;
                await executor.StartAsync(new(Guid.NewGuid(), Guid.NewGuid()),
                    new ExecutableWorkload(Environment.ProcessPath!, AppContext.BaseDirectory, "", [], []), token);
            },
            () =>
            {
                Interlocked.Increment(ref retirementCalls);
                return Task.CompletedTask;
            }, null, null, null);

        await Assert.ThrowsAsync<WorkloadCleanupException>(workload.Start);
        await Assert.ThrowsAsync<WorkloadCleanupException>(() => workload.Completion);
        Assert.Equal(3, handler.Resources.Count);
        await Assert.ThrowsAsync<HttpRequestException>(workload.StopAsync);
        Assert.Equal(2, handler.Resources.Count);
        Assert.All(handler.Resources.Keys, key =>
            Assert.True(key.StartsWith("containers/", StringComparison.Ordinal) ||
                key.StartsWith("services/", StringComparison.Ordinal)));
        await workload.StopAsync();
        Assert.Empty(handler.Resources);
        Assert.Equal(1, retirementCalls);
        Assert.Equal(2, commandAttempts);
        Assert.Equal(3, primaryAttempts);
    }

    [Fact]
    public async Task PartialDeletionIsRetriedAndConcurrentDisposalsAreSerialized()
    {
        var options = new NativeServerOptions { CleanupTimeout = TimeSpan.FromSeconds(10) };
        using var handler = new DcpCleanupTestHandler();
        await using var client = handler.CreateClient(options);
        var lease = await new DcpWorkloadExecutor(client, Task.CompletedTask, options).StartAsync(
            new WorkloadIdentity(Guid.NewGuid(), Guid.NewGuid()),
            new ContainerWorkload("example:1", 6379, [], []), TestContext.Current.CancellationToken);
        var serviceAttempts = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeDelete = async (key, token) =>
        {
            if (key.StartsWith("services/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref serviceAttempts) == 1)
                {
                    throw new HttpRequestException("Transient service failure");
                }
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => lease.DisposeAsync().AsTask());
        Assert.Single(handler.Resources);
        var retry = lease.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var concurrent = lease.DisposeAsync().AsTask();
        Assert.False(concurrent.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(retry, concurrent);
        await lease.DisposeAsync();
        Assert.Empty(handler.Resources);
        Assert.Equal(4, handler.Deletes.Count);
    }

    private static string Error(JsonObject response) =>
        response["error"]?["data"]?["code"]?.GetValue<string>() ??
        response["result"]?["$error"]?["code"]?.GetValue<string>() ??
        throw new InvalidOperationException("The test expected an RPC error.");
}
