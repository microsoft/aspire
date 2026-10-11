// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Tracks execution and gates cleanup without substituting the native model or runtime services.</summary>
internal sealed class WorkloadTestExecutor : IWorkloadExecutor
{
    private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<WorkloadIdentity> Started { get; } = new();
    public ConcurrentQueue<WorkloadIdentity> Removed { get; } = new();
    public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseCleanup() => _cleanup.TrySetResult();
    public void FailCleanup() => _cleanup.TrySetException(new InvalidOperationException("Test workload cleanup failed."));

    public Task<IWorkloadLease> StartAsync(WorkloadIdentity identity, WorkloadPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Started.Enqueue(identity);

        return Task.FromResult<IWorkloadLease>(new Lease(this, identity, plan));
    }

    private sealed class Lease(WorkloadTestExecutor executor, WorkloadIdentity identity, WorkloadPlan plan) : IWorkloadLease
    {
        public WorkloadEndpoint Endpoint { get; } = new("127.0.0.1",
            plan is ContainerWorkload { TargetPort: 0 } ? 0 : 5050, "test-instance");
        public Task<WorkloadStatus> ReadStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkloadStatus(true, null));
        public Task<ImmutableArray<WorkloadLog>> ReadLogsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<WorkloadLog>.Empty);
        public async ValueTask DisposeAsync()
        {
            executor.CleanupStarted.TrySetResult();
            await executor._cleanup.Task.ConfigureAwait(false);
            executor.Removed.Enqueue(identity);
        }
    }
}
