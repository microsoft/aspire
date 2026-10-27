// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Sessions;
using Aspire.Shared;
using Microsoft.Extensions.Time.Testing;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeTimeProviderTests
{
    [Fact]
    public void RpcStaticSessionAndWorkspaceDiagnosticsUseTheSameHostClock()
    {
        var clock = new FakeTimeProvider();
        var options = new NativeServerOptions { TimeProvider = clock };
        options.Validate();
        options.Validate();
        Assert.Same(clock, options.Runtime.TimeProvider);
        var server = new NativeApplicationServer(UnavailableWorkloadExecutor.Instance, options.Runtime);
        using var capture = new DiagnosticCapture(sampleActivities: false);
        using var client = new RpcTestClient(server, options);
        var session = client.CreateSession();
        var composition = client.StartGeneration(session);
        client.AddResource(composition, "cache");
        var root = Result(client.Call("getApplicationServer", null));
        var workspace = Result(client.Invoke("createApplicationWorkspace", Arguments(("context", root))));
        var revision = Result(client.Invoke("beginRevision", Arguments(("context", workspace))));
        client.AddResource(revision, "worker");
        Assert.Null(client.Invoke("commitRevision", Arguments(("context", workspace), ("revision", revision)))["error"]);

        var events = capture.Events.ToArray();
        Assert.Single(events, value => value.Operation == "session.generation.start" && value.Outcome == OperationOutcome.Ok);
        Assert.Single(events, value => value.Operation == "runtime.revision.commit" && value.Outcome == OperationOutcome.Ok);
        Assert.All(events, value => Assert.Equal(clock.GetUtcNow(), value.Timestamp));
        server.Close();
    }

    [Fact]
    public void DiagnosticDurationsAndSessionTimestampsUseTheInjectedClock()
    {
        var clock = new FakeTimeProvider();
        using var capture = new DiagnosticCapture(sampleActivities: false);
        var started = clock.GetUtcNow();
        using (var operation = new NativeDiagnostics.Operation("test.clock", null, null, clock))
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            operation.Succeeded = true;
        }
        var events = capture.Events.Where(value => value.Operation == "test.clock").ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(started, events[0].Timestamp);
        Assert.Equal(started.AddSeconds(3), events[1].Timestamp);
        Assert.Equal(3, events[1].DurationSeconds);
        using var session = new ApplicationSession(clock);
        session.StartGeneration().AddResource("cache", "example/Cache");
        Assert.All(capture.Events.Where(value => value.Operation != "test.clock"),
            value => Assert.Equal(clock.GetUtcNow(), value.Timestamp));
    }

    [Fact]
    public async Task RuntimeMetadataLogsAndAllWaitBudgetsUseTheInjectedClock()
    {
        var clock = new FakeTimeProvider();
        using var model = new ApplicationModel(clock);
        var resource = model.AddResource("cache", "example/Cache");
        using var runtime = new RuntimeGeneration(model.Seal(), UnavailableWorkloadExecutor.Instance,
            new NativeRuntimeOptions { TimeProvider = clock });
        using var owner = runtime.ClaimResource(resource);
        Assert.Equal(clock.GetUtcNow(), runtime.CreatedAt);
        clock.Advance(TimeSpan.FromSeconds(3));
        owner.AppendLog(LogStream.Stdout, "ready");
        Assert.Equal(clock.GetUtcNow(), Assert.Single(runtime.ReadLogs(resource.ResourceId, 0).Entries).Timestamp);
        var configuration = owner.ReadConfiguration();
        var waitConfiguration = owner.WaitConfigurationAsync(configuration.Revision, 1000);
        var waitCommands = owner.WaitCommandsAsync(1000);
        var version = runtime.Read().Version;
        var waitObservation = runtime.WaitForChangeAsync(version, 1000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(waitConfiguration.IsCompleted);
        Assert.False(waitCommands.IsCompleted);
        Assert.False(waitObservation.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(configuration, await waitConfiguration);
        Assert.Empty(await waitCommands);
        Assert.Equal(version, (await waitObservation).Version);
    }

    [Fact]
    public void InvitationsUseMonotonicExpiryInsteadOfWallClockChanges()
    {
        var clock = new AdjustableFakeTimeProvider();
        var invitations = new RuntimeInvitations(UnavailableWorkloadExecutor.Instance, new NativeRuntimeOptions { TimeProvider = clock });
        var workspace = invitations.CreateWorkspace();
        var first = invitations.Issue(workspace, () => workspace);
        clock.UtcOffset = TimeSpan.FromDays(-1);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<ArgumentException>(() => invitations.Claim<ApplicationWorkspace>(first));
        var second = invitations.Issue(workspace, () => workspace);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Same(workspace, invitations.Claim<ApplicationWorkspace>(second));
        Assert.Throws<ArgumentException>(() => invitations.Claim<ApplicationWorkspace>(second));
        invitations.Close();
    }

    [Fact]
    public async Task WorkloadStartupTimeoutUsesTheInjectedClock()
    {
        var clock = new FakeTimeProvider();
        var executor = new WorkloadTestExecutor
        {
            BeforeStart = token => Task.Delay(Timeout.InfiniteTimeSpan, clock, token)
        };
        executor.ReleaseCleanup();
        using var model = new ApplicationModel(clock);
        var handle = model.AddResource("cache", "example/Cache");
        using var runtime = new RuntimeGeneration(model.Seal(), executor,
            new NativeRuntimeOptions { TimeProvider = clock, WorkloadStartupTimeout = TimeSpan.FromSeconds(2) });
        using var owner = runtime.ClaimResource(handle);
        var started = owner.StartWorkload(new ContainerWorkload("example:1", 0, [], []));
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(started.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => started);
        Assert.Equal(ResourceState.Failed, Assert.Single(runtime.Read().Resources).State);
        runtime.Dispose();
        await runtime.Completion;
    }

    [Fact]
    public async Task SuccessfulStartupStopsItsBudgetButRetainsParentCancellation()
    {
        var clock = new FakeTimeProvider();
        CancellationToken startupToken = default;
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new WorkloadTestExecutor
        {
            BeforeStart = token =>
            {
                startupToken = token;

                return Task.CompletedTask;
            },
            BeforeReadStatus = _ =>
            {
                observing.TrySetResult();

                return releaseObservation.Task;
            }
        };
        executor.ReleaseCleanup();
        using var model = new ApplicationModel(clock);
        var handle = model.AddResource("cache", "example/Cache");
        using var runtime = new RuntimeGeneration(model.Seal(), executor,
            new NativeRuntimeOptions { TimeProvider = clock, WorkloadStartupTimeout = TimeSpan.FromSeconds(2) });
        using var owner = runtime.ClaimResource(handle);
        await owner.StartWorkload(new ContainerWorkload("example:1", 0, [], []));
        try
        {
            await observing.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.False(startupToken.IsCancellationRequested);
            runtime.Dispose();
            // Assert while the startup scope is alive. If observation unwinds
            // during parent cancellation, disposing its linked CTS can remove
            // the startup callback before cancellation reaches it.
            Assert.True(startupToken.IsCancellationRequested);
        }
        finally
        {
            releaseObservation.TrySetResult();
        }
        await runtime.Completion;
    }

    [Fact]
    public async Task DependencyReadinessDoesNotConsumeTheWorkloadStartupBudget()
    {
        var clock = new FakeTimeProvider();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new WorkloadTestExecutor
        {
            BeforeStart = token =>
            {
                attempted.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, clock, token);
            }
        };
        executor.ReleaseCleanup();
        using var model = new ApplicationModel(clock);
        var dependency = model.AddResource("cache", "example/Cache");
        var worker = model.AddResource("worker", "example/Worker");
        model.AddDependency(worker, dependency);
        using var runtime = new RuntimeGeneration(model.Seal(), executor,
            new NativeRuntimeOptions { TimeProvider = clock, WorkloadStartupTimeout = TimeSpan.FromSeconds(2) });
        using var cache = runtime.ClaimResource(dependency);
        using var owner = runtime.ClaimResource(worker);
        var started = owner.StartWorkload(new ContainerWorkload("example:1", 0, [], []));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(attempted.Task.IsCompleted);
        Assert.False(started.IsCompleted);
        cache.Publish(ResourceState.Running, healthy: true, []);
        await attempted.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(started.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => started);
        runtime.Dispose();
        await runtime.Completion;
    }

    [Fact]
    public async Task ObservationIntervalsAndFailedStartsCannotBeSuccessfulExits()
    {
        var clock = new FakeTimeProvider();
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var model = new ApplicationModel(clock);
        var handle = model.AddResource("cache", "example/Cache");
        using var runtime = new RuntimeGeneration(model.Seal(), executor,
            new NativeRuntimeOptions { TimeProvider = clock, ObservationInterval = TimeSpan.FromSeconds(1) });
        using var owner = runtime.ClaimResource(handle);
        await owner.StartWorkload(new ContainerWorkload("example:1", 0, [], []));
        executor.Phase = WorkloadPhase.Failed;
        executor.ExitCode = 0;
        var version = runtime.Read().Version;
        var changed = runtime.WaitForChangeAsync(version, 5000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(changed.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ResourceState.Failed, Assert.Single((await changed).Resources).State);
        await runtime.Completion;
    }

    [Fact]
    public async Task ProviderBackedAuthenticationTimeoutCanBeStoppedWithoutStoppingTheParent()
    {
        var clock = new FakeTimeProvider();
        using var parent = new CancellationTokenSource();
        using var timeout = new TimeProviderCancellation(TimeSpan.FromSeconds(2), clock, parent.Token);
        timeout.StopTimeout();
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(timeout.IsCancellationRequested);
        await parent.CancelAsync();
        Assert.True(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DcpHeaderAndLogRequestsUseProviderBackedBudgets(bool logs)
    {
        var clock = new FakeTimeProvider();
        var options = new NativeServerOptions
        {
            TimeProvider = clock, ControllerRequestTimeout = TimeSpan.FromSeconds(2),
            ControllerLogReadTimeout = TimeSpan.FromSeconds(1)
        };
        using var handler = new DcpCleanupTestHandler
        {
            BeforeResponse = token => Task.Delay(Timeout.InfiniteTimeSpan, clock, token)
        };
        await using var client = handler.CreateClient(options);
        var request = logs
            ? (Task)client.ReadLogsAsync(DcpResourceCollection.Containers, "cache", LogStream.Stdout, 0, TestContext.Current.CancellationToken)
            : client.GetAsync(DcpResourceCollection.Containers, "cache", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(logs ? 999 : 1999));
        Assert.False(request.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task SharedConnectionMaterialRetriesUseTheInjectedClockAndRetainCancellation()
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = DcpConnectionMaterial.WaitAsync("missing-kubeconfig-" + Guid.NewGuid(), () => false, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2), _ =>
            {
                if (++attempts == 2)
                {
                    retried.TrySetResult();
                }
            }, clock, cancellation.Token);
        Assert.Equal(1, attempts);
        clock.Advance(TimeSpan.FromSeconds(2));
        await retried.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
