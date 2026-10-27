// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Tracks execution and gates cleanup without substituting the native model or runtime services.</summary>
internal sealed class WorkloadTestExecutor : IWorkloadExecutor, IWorkloadPreparationExecutor
{
    private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<WorkloadIdentity> Started { get; } = new();
    public ConcurrentQueue<WorkloadPlan> Plans { get; } = new();
    public ConcurrentQueue<WorkloadIdentity> Prepared { get; } = new();
    public ConcurrentQueue<WorkloadIdentity> ReleasedPreparations { get; } = new();
    public ConcurrentQueue<WorkloadIdentity> Removed { get; } = new();
    public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? StartFailure { get; init; }
    public Func<WorkloadIdentity, CancellationToken, Task>? BeforePrepare { get; init; }
    public Func<CancellationToken, Task>? BeforeStart { get; init; }
    public Func<WorkloadIdentity, CancellationToken, Task>? BeforeStartResource { get; init; }
    public Func<CancellationToken, Task>? BeforeReadStatus { get; init; }
    public Func<WorkloadIdentity, Task>? BeforeCleanup { get; init; }
    public WorkloadPhase Phase { get; set; } = WorkloadPhase.Running;
    public int? ExitCode { get; set; }
    public Func<WorkloadPlan, WorkloadStatus>? StatusForPlan { get; init; }

    public void ReleaseCleanup() => _cleanup.TrySetResult();
    public void FailCleanup() => _cleanup.TrySetException(new InvalidOperationException("Test workload cleanup failed."));

    public async Task<IWorkloadPreparation> PrepareAsync(WorkloadIdentity identity, WorkloadPlan template, CancellationToken cancellationToken)
    {
        if (BeforePrepare is { } before)
        {
            await before(identity, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Prepared.Enqueue(identity);

        return new Preparation(this, identity, template);
    }

    public async Task<IWorkloadLease> StartAsync(WorkloadIdentity identity, WorkloadPlan plan, CancellationToken cancellationToken)
    {
        if (BeforeStartResource is { } resourceCallback)
        {
            await resourceCallback(identity, cancellationToken).ConfigureAwait(false);
        }
        if (BeforeStart is { } before)
        {
            await before(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Started.Enqueue(identity);
        Plans.Enqueue(plan);
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        return new Lease(this, identity, plan);
    }

    private sealed class Lease(WorkloadTestExecutor executor, WorkloadIdentity identity, WorkloadPlan plan) : IWorkloadLease
    {
        public WorkloadEndpoint Endpoint { get; } = new("test-instance",
            plan.Endpoints.Select((endpoint, index) => new AllocatedWorkloadPort(
                endpoint.Name, endpoint.Protocol, endpoint.UriScheme, "127.0.0.1", 5050 + index)).ToImmutableArray());
        public async Task<WorkloadStatus> ReadStatusAsync(CancellationToken cancellationToken)
        {
            if (executor.BeforeReadStatus is { } before)
            {
                await before(cancellationToken).ConfigureAwait(false);
            }

            return executor.StatusForPlan?.Invoke(plan) ?? new WorkloadStatus(executor.Phase, executor.ExitCode);
        }
        public Task<ImmutableArray<WorkloadLog>> ReadLogsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<WorkloadLog>.Empty);
        public async ValueTask DisposeAsync()
        {
            executor.CleanupStarted.TrySetResult();
            await executor._cleanup.Task.ConfigureAwait(false);
            if (executor.BeforeCleanup is { } before)
            {
                await before(identity).ConfigureAwait(false);
            }
            executor.Removed.Enqueue(identity);
        }
    }

    private sealed class Preparation(WorkloadTestExecutor executor, WorkloadIdentity identity, WorkloadPlan template) : IWorkloadPreparation
    {
        private IWorkloadLease? _lease;
        private bool _disposed;
        public WorkloadEndpoint Endpoint => _lease?.Endpoint ?? new("",
            template.Endpoints.Select((endpoint, index) => new AllocatedWorkloadPort(
                endpoint.Name, endpoint.Protocol, endpoint.UriScheme, "127.0.0.1", 5050 + index)).ToImmutableArray());

        public async Task StartAsync(WorkloadPlan plan, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lease is not null)
            {
                throw new InvalidOperationException("The prepared workload has already started.");
            }
            _lease = await executor.StartAsync(identity, plan, cancellationToken).ConfigureAwait(false);
        }
        public Task<WorkloadStatus> ReadStatusAsync(CancellationToken cancellationToken) =>
            (_lease ?? throw new InvalidOperationException("The workload has not started.")).ReadStatusAsync(cancellationToken);
        public Task<ImmutableArray<WorkloadLog>> ReadLogsAsync(CancellationToken cancellationToken) =>
            (_lease ?? throw new InvalidOperationException("The workload has not started.")).ReadLogsAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            if (_lease is not null)
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
            executor.ReleasedPreparations.Enqueue(identity);
            _disposed = true;
        }
    }
}
