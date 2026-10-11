// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Runtime;

/// <summary>Describes a standard execution primitive without integration clients or transport-specific data.</summary>
internal abstract record WorkloadPlan(ImmutableArray<WorkloadEnvironment> Environment, ImmutableArray<string> Arguments);
internal sealed record ContainerWorkload(string Image, int TargetPort, ImmutableArray<WorkloadEnvironment> Variables,
    ImmutableArray<string> Args) : WorkloadPlan(Variables, Args);
internal sealed record ExecutableWorkload(string ExecutablePath, string WorkingDirectory,
    string PortEnvironmentVariable, ImmutableArray<WorkloadEnvironment> Variables, ImmutableArray<string> Args) : WorkloadPlan(Variables, Args);
internal sealed record WorkloadEnvironment(string Name, string Value);
internal sealed record WorkloadIdentity(Guid GenerationId, Guid ResourceId);
internal sealed record WorkloadEndpoint(string Host, int Port, string InstanceId);
internal sealed record WorkloadStatus(bool Running, int? ExitCode);
internal sealed record WorkloadLog(string Stream, string Message);

/// <summary>Preserves startup console diagnostics separately from classified host errors.</summary>
internal sealed class WorkloadStartupException(string message, ImmutableArray<WorkloadLog> logs) : InvalidOperationException(message)
{
    public ImmutableArray<WorkloadLog> Logs { get; } = logs;
}

/// <summary>Separates orchestration I/O from execution ownership and observations.</summary>
internal interface IWorkloadExecutor
{
    Task<IWorkloadLease> StartAsync(WorkloadIdentity identity, WorkloadPlan plan, CancellationToken cancellationToken);
}

/// <summary>Represents one externally running workload and its cleanup obligation.</summary>
internal interface IWorkloadLease : IAsyncDisposable
{
    WorkloadEndpoint Endpoint { get; }
    Task<WorkloadStatus> ReadStatusAsync(CancellationToken cancellationToken);
    Task<ImmutableArray<WorkloadLog>> ReadLogsAsync(CancellationToken cancellationToken);
}

/// <summary>Rejects execution explicitly in model-only fixtures.</summary>
internal sealed class UnavailableWorkloadExecutor : IWorkloadExecutor
{
    public static UnavailableWorkloadExecutor Instance { get; } = new();
    public Task<IWorkloadLease> StartAsync(WorkloadIdentity identity, WorkloadPlan plan, CancellationToken cancellationToken) =>
        throw new NotSupportedException("No workload executor is configured for this native server.");
}

/// <summary>Owns startup, observation, cancellation, and cleanup for one resource execution.</summary>
internal sealed class ResourceWorkload(
    WorkloadIdentity identity,
    WorkloadPlan plan,
    IWorkloadExecutor executor,
    NativeRuntimeOptions options,
    Func<CancellationToken, Task> waitForDependencies,
    Action<string, WorkloadEndpoint?> publish,
    Action<string, string> log) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<WorkloadEndpoint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completion;
    private readonly Lock _lifetimeGate = new();
    private bool _finished;
    public Task Completion => _completion ?? Task.CompletedTask;

    public Task<WorkloadEndpoint> Start()
    {
        if (_completion is not null)
        {
            throw new InvalidOperationException("The resource workload has already started.");
        }
        _completion = RunAsync();

        return _ready.Task;
    }

    private async Task RunAsync()
    {
        using var operation = new NativeDiagnostics.Operation("runtime.workload", null, identity.GenerationId);
        operation.SetResource(identity.ResourceId);
        IWorkloadLease? lease = null;
        try
        {
            await waitForDependencies(_lifetime.Token).ConfigureAwait(false);
            // Dependency readiness has its own lifetime. Start this workload's
            // startup budget only after dependencies are ready.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            startup.CancelAfter(options.WorkloadStartupTimeout);
            lease = await executor.StartAsync(identity, plan, startup.Token).ConfigureAwait(false);
            publish("Running", lease.Endpoint);
            _ready.TrySetResult(lease.Endpoint);
            while (true)
            {
                foreach (var line in await lease.ReadLogsAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    log(line.Stream, line.Message);
                }
                var status = await lease.ReadStatusAsync(_lifetime.Token).ConfigureAwait(false);
                if (!status.Running)
                {
                    publish(status.ExitCode == 0 ? "Stopped" : "Failed", null);
                    break;
                }
                await Task.Delay(options.ObservationInterval, _lifetime.Token).ConfigureAwait(false);
            }
            operation.Succeeded = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _ready.TrySetCanceled(_lifetime.Token);
            operation.Succeeded = true;
        }
        catch (Exception exception)
        {
            // Record only the classification. Executor failures can carry
            // credentials from tools; callers retain the original exception.
            operation.ErrorType = exception.GetType().FullName;
            if (exception is WorkloadStartupException startupFailure)
            {
                foreach (var line in startupFailure.Logs)
                {
                    log(line.Stream, line.Message);
                }
            }
            log("stderr", $"Workload failed: {exception.GetType().Name}.");
            publish("Failed", null);
            _ready.TrySetException(exception);
            throw;
        }
        finally
        {
            try
            {
                if (lease is not null)
                {
                    try
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        operation.Succeeded = false;
                        operation.ErrorType = exception.GetType().FullName;
                        throw;
                    }
                }
            }
            finally
            {
                lock (_lifetimeGate)
                {
                    _finished = true;
                    _lifetime.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (!_finished)
            {
                _lifetime.Cancel();
            }
        }
    }
}
