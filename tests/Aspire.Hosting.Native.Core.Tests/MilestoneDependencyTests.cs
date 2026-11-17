// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Core.Tests;

public class MilestoneDependencyTests
{
    [Fact]
    public async Task MutualAndSelfEndpointReferencesConfigureBeforeEitherWorkloadStarts()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var first = Declare(composition, "first");
        var second = Declare(composition, "second");
        first.WithEnvironment("PEER", composition.CreateEndpointValue(second, "http", "url"));
        first.WithEnvironment("SELF", composition.CreateEndpointValue(first, "http", "url"));
        second.WithEnvironment("PEER", composition.CreateEndpointValue(first, "http", "url"));
        Assert.Empty(first.InspectResource().Dependencies);
        Assert.Empty(second.InspectResource().Dependencies);
        WorkloadExecutionPlan.Validate(composition.Model.Inspect().Resources, allocationBeforeStart: true);
        try
        {
            await composition.StartApplication().WaitApplicationReady(15000);
            Assert.Equal(2, executor.Prepared.Count);
            Assert.Equal(2, executor.Plans.Count);
            Assert.All(executor.Plans, plan => Assert.All(plan.Environment, variable =>
                Assert.Equal("http://127.0.0.1:5050", variable.Value)));
        }
        finally
        {
            await session.RetireGeneration(composition);
        }
        Assert.Equal(2, executor.Removed.Count);
        Assert.Equal(2, executor.ReleasedPreparations.Count);
    }

    [Fact]
    public async Task ConsumerDoesNotStartUntilReferencedAllocationExists()
    {
        var allocated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid producerId = default;
        var executor = new WorkloadTestExecutor
        {
            BeforePrepare = (identity, token) =>
            {
                if (identity.ResourceId != producerId)
                {
                    return Task.CompletedTask;
                }
                allocated.TrySetResult();

                return release.Task.WaitAsync(token);
            }
        };
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var consumer = Declare(composition, "a-consumer");
        var producer = Declare(composition, "z-producer");
        producerId = producer.Handle.ResourceId;
        consumer.WithEnvironment("PEER", composition.CreateEndpointValue(producer, "http", "url"));
        var execution = composition.StartApplication();
        try
        {
            await allocated.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
            Assert.Empty(executor.Started);
            Assert.Single(executor.Prepared);
            release.TrySetResult();
            await execution.WaitApplicationReady(15000);
            Assert.Equal(2, executor.Started.Count);
        }
        finally
        {
            release.TrySetResult();
            await session.RetireGeneration(composition);
        }
    }

    [Fact]
    public async Task ExplicitReadinessWaitRemainsDistinctFromAllocationReferences()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new WorkloadTestExecutor
        {
            BeforeStart = token =>
            {
                blocked.TrySetResult();

                return release.Task.WaitAsync(token);
            }
        };
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var source = Declare(composition, "source");
        var target = Declare(composition, "target");
        target.WithEnvironment("PEER", composition.CreateEndpointValue(source, "http", "url"));
        target.WaitFor(source);
        var execution = composition.StartApplication();
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
            Assert.Equal(2, executor.Prepared.Count);
            Assert.Empty(executor.Plans);
            release.TrySetResult();
            await execution.WaitApplicationReady(15000);
            Assert.Equal([source.Handle.ResourceId, target.Handle.ResourceId],
                executor.Started.Select(identity => identity.ResourceId));
        }
        finally
        {
            release.TrySetResult();
            await session.RetireGeneration(composition);
        }
    }

    [Fact]
    public async Task ConsumerCanStartWhileItsAllocatedProducerHasNotStarted()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid producerId = default;
        var executor = new WorkloadTestExecutor
        {
            BeforeStartResource = (identity, token) =>
            {
                if (identity.ResourceId == producerId)
                {
                    entered.TrySetResult();

                    return release.Task.WaitAsync(token);
                }
                return Task.CompletedTask;
            },
            BeforeReadStatus = _ =>
            {
                consumerStarted.TrySetResult();

                return Task.CompletedTask;
            }
        };
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var producer = Declare(composition, "producer");
        producerId = producer.Handle.ResourceId;
        var consumer = Declare(composition, "consumer");
        consumer.WithEnvironment("PEER", composition.CreateEndpointValue(producer, "http", "url"));
        var execution = composition.StartApplication();
        try
        {
            await Task.WhenAll(entered.Task, consumerStarted.Task).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
            Assert.Equal(consumer.Handle.ResourceId, Assert.Single(executor.Started).ResourceId);
            Assert.Equal("http://127.0.0.1:5050", Assert.Single(Assert.Single(executor.Plans).Environment).Value);
            Assert.False(execution.ReadApplicationState().Resources.Single(resource => resource.Name == "producer").Healthy);
            release.TrySetResult();
            await execution.WaitApplicationReady(15000);
        }
        finally
        {
            release.TrySetResult();
            await session.RetireGeneration(composition);
        }
    }

    [Fact]
    public async Task FailedPeerAllocationFailsConsumersAndReleasesTheirPreparedAddresses()
    {
        Guid failingId = default;
        var executor = new WorkloadTestExecutor
        {
            BeforePrepare = (identity, _) => identity.ResourceId == failingId
                ? Task.FromException(new InvalidOperationException("Allocation failed."))
                : Task.CompletedTask
        };
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var consumer = Declare(composition, "a-consumer");
        var producer = Declare(composition, "z-producer");
        failingId = producer.Handle.ResourceId;
        consumer.WithEnvironment("PEER", composition.CreateEndpointValue(producer, "http", "url"));
        var execution = composition.StartApplication();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            execution.WaitApplicationReady(15000));
        await session.RetireGeneration(composition);
        Assert.Empty(executor.Started);
        Assert.Single(executor.ReleasedPreparations);
    }

    [Fact]
    public async Task WorkspaceRevisionsRestartReferenceCyclesWithoutRetainingOldConfiguration()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        var workspace = new NativeApplicationServer(executor).CreateApplicationWorkspace();
        Composition Revision(string version)
        {
            var composition = workspace.BeginRevision();
            var first = Declare(composition, "first");
            var second = Declare(composition, "second");
            first.WithEnvironment("PEER", composition.CreateEndpointValue(second, "http", "url"));
            first.WithEnvironment("VERSION", composition.CreateLiteralValue(version));
            second.WithEnvironment("PEER", composition.CreateEndpointValue(first, "http", "url"));

            return composition;
        }
        try
        {
            var first = Revision("one");
            await workspace.CommitRevision(first);
            await first.StartApplication().WaitApplicationReady(15000);
            await workspace.CommitRevision(Revision("two"));
            Assert.Equal(4, executor.Started.Count);
            Assert.Equal(2, executor.Removed.Count);
            Assert.Equal(["one", "two"], executor.Plans.SelectMany(plan => plan.Environment)
                .Where(variable => variable.Name == "VERSION").Select(variable => variable.Value));
        }
        finally
        {
            await workspace.RetireApplicationWorkspace();
        }
        Assert.Equal(4, executor.Removed.Count);
        Assert.Equal(4, executor.ReleasedPreparations.Count);
    }

    [Fact]
    public async Task FailedReferenceCycleRevisionRetainsCleanupOwnershipForRetirement()
    {
        var failCleanup = false;
        var executor = new WorkloadTestExecutor
        {
            BeforeCleanup = _ => Volatile.Read(ref failCleanup)
                ? Task.FromException(new InvalidOperationException("Cleanup failed."))
                : Task.CompletedTask
        };
        executor.ReleaseCleanup();
        var workspace = new NativeApplicationServer(executor).CreateApplicationWorkspace();
        Composition Revision(string version)
        {
            var composition = workspace.BeginRevision();
            var first = Declare(composition, "first");
            var second = Declare(composition, "second");
            first.WithEnvironment("PEER", composition.CreateEndpointValue(second, "http", "url"));
            first.WithEnvironment("VERSION", composition.CreateLiteralValue(version));
            second.WithEnvironment("PEER", composition.CreateEndpointValue(first, "http", "url"));

            return composition;
        }
        try
        {
            var initial = Revision("one");
            await workspace.CommitRevision(initial);
            await initial.StartApplication().WaitApplicationReady(15000);
            Volatile.Write(ref failCleanup, true);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => workspace.CommitRevision(Revision("two")));
            Assert.Equal(2, executor.Started.Count);
            Assert.Empty(executor.Removed);
        }
        finally
        {
            Volatile.Write(ref failCleanup, false);
            await workspace.RetireApplicationWorkspace();
        }
        Assert.Equal(2, executor.Removed.Count);
        Assert.Equal(2, executor.ReleasedPreparations.Count);
    }

    [Fact]
    public async Task RejectedExecutionRevisionLeavesTheRunningGraphUnchanged()
    {
        var workspace = new NativeApplicationServer().CreateApplicationWorkspace();
        try
        {
            var initial = workspace.BeginRevision();
            initial.AddResource("existing", "example/external");
            await workspace.CommitRevision(initial);
            var execution = initial.StartApplication();
            await execution.WaitApplicationReady(15000);

            var revision = workspace.BeginRevision();
            revision.AddResource("existing", "example/external");
            var added = Declare(revision, "added");
            added.WithEnvironment("SELF", revision.CreateEndpointValue(added, "http", "url"));
            await Assert.ThrowsAsync<ExecutionPlanException>(() => workspace.CommitRevision(revision));
            Assert.Equal("existing", Assert.Single(execution.ReadApplicationState().Resources).Name);
        }
        finally
        {
            await workspace.RetireApplicationWorkspace();
        }
    }

    [Fact]
    public void MutualReadinessWaitReportsItsMilestoneCycleBeforeAnyExecution()
    {
        using var model = new ApplicationModel();
        var first = model.AddResource("first", "hosting/executable");
        var second = model.AddResource("second", "hosting/executable");
        model.AddDependency(first, second);
        var error = Assert.Throws<ExecutionPlanException>(() => model.AddDependency(second, first));
        Assert.Equal("Execution prerequisite cycle: second.Start -> first.Ready -> first.Start -> second.Ready -> second.Start",
            error.Message);
        Assert.Empty(model.ReadResource(second).Dependencies);
    }

    [Fact]
    public void ReferenceCycleRequiresAnAllocationBeforeStartExecutor()
    {
        var session = new NativeApplicationServer().OpenApplication();
        var composition = session.StartGeneration();
        var first = Declare(composition, "first");
        var second = Declare(composition, "second");
        first.WithEnvironment("PEER", composition.CreateEndpointValue(second, "http", "url"));
        second.WithEnvironment("PEER", composition.CreateEndpointValue(first, "http", "url"));
        var failure = Assert.Throws<ExecutionPlanException>(composition.StartApplication);
        Assert.StartsWith("Execution prerequisite cycle: ", failure.Message);
        Assert.Contains(".Allocated", failure.Message);
        Assert.Contains(".Configured", failure.Message);
        Assert.Contains(".Start", failure.Message);
        session.Close();
    }

    [Fact]
    public async Task RetirementWhileWaitingForPeerAllocationReleasesPreparedOwnership()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid delayedId = default;
        var executor = new WorkloadTestExecutor
        {
            BeforePrepare = async (identity, token) =>
            {
                if (identity.ResourceId == delayedId)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
        };
        executor.ReleaseCleanup();
        var session = new NativeApplicationServer(executor).OpenApplication();
        var composition = session.StartGeneration();
        var first = Declare(composition, "first");
        var second = Declare(composition, "second");
        delayedId = second.Handle.ResourceId;
        first.WithEnvironment("PEER", composition.CreateEndpointValue(second, "http", "url"));
        composition.StartApplication();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
        await session.RetireGeneration(composition).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);
        Assert.Empty(executor.Started);
        Assert.Single(executor.ReleasedPreparations);
    }

    private static DeclaredResource Declare(Composition composition, string name)
    {
        var resource = composition.AddExecutable(name, Environment.ProcessPath!, AppContext.BaseDirectory);
        resource.WithEndpoint(new()
        {
            Name = "http", Protocol = "TCP", UriScheme = "http", TargetPort = 0, HostPort = 0, EnvironmentVariable = "PORT"
        });

        return resource;
    }
}
