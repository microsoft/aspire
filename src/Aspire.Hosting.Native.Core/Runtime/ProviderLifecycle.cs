// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.Native.Runtime;

internal static class ProviderLifecyclePhases
{
    public const string Initialize = "initialize";
    public const string Allocated = "allocated";
    public const string Ready = "ready";
    public const string Retiring = "retiring";
}

/// <summary>Identifies an offline-validated provider callback without transport or language objects.</summary>
internal sealed record ProviderLifecycleBinding(Guid ProviderId, string ObjectId, string TypeId,
    string Phase, string CapabilityId);

/// <summary>Separates native lifecycle ordering and authority from the provider transport.</summary>
internal interface IProviderLifecycleDispatcher
{
    Task InvokeAsync(ProviderLifecycleBinding binding, ResourceLifecycleAuthority authority, CancellationToken cancellationToken);
}

internal sealed class UnavailableProviderLifecycleDispatcher : IProviderLifecycleDispatcher
{
    public static UnavailableProviderLifecycleDispatcher Instance { get; } = new();
    public Task InvokeAsync(ProviderLifecycleBinding binding, ResourceLifecycleAuthority authority, CancellationToken cancellationToken) =>
        throw new NotSupportedException("No provider lifecycle dispatcher is configured.");
}

/// <summary>Grants one phase native execution with captured inputs, never credential inspection or declaration access.</summary>
internal sealed class ResourceLifecycleAuthority : IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly ImmutableDictionary<string, string> _values;
    private readonly Func<WorkloadPlan, CancellationToken, Task<bool>> _execute;
    private readonly Action<LogStream, string> _log;
    private readonly Lock _gate = new();
    private readonly List<Task<bool>> _executions = [];
    private bool _disposed;

    public ResourceLifecycleAuthority(string phase, ImmutableDictionary<string, string> values,
        Func<WorkloadPlan, CancellationToken, Task<bool>> execute, Action<LogStream, string> log,
        CancellationToken cancellationToken)
    {
        Phase = phase;
        _values = values;
        _execute = execute;
        _log = log;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _lifetime.Token;
    }

    public string Phase { get; }
    public bool IsRevoked => _token.IsCancellationRequested;
    public void AppendLog(LogStream stream, string message)
    {
        _token.ThrowIfCancellationRequested();
        _log(stream, message);
    }

    public Task<bool> RunExecutable(ExecutableWorkload template, SymbolicWorkloadBindings bindings)
    {
        _token.ThrowIfCancellationRequested();
        bindings.Validate(template);
        string Resolve(string key) => _values.TryGetValue(key, out var value) ? value :
            throw new InvalidOperationException("The lifecycle field is unavailable in this phase.");
        var environment = template.Environment.AddRange(bindings.Environment.Select(binding =>
            new WorkloadEnvironment(binding.Name, Resolve(binding.ConfigurationKey))));
        var arguments = template.Arguments.ToBuilder();
        foreach (var binding in bindings.Arguments)
        {
            arguments[binding.Index] = Resolve(binding.ConfigurationKey);
        }
        var plan = SymbolicWorkloadBindings.Copy(template, environment, arguments.ToImmutable());
        WorkloadDefinition.Validate(plan);

        lock (_gate)
        {
            _token.ThrowIfCancellationRequested();
            var execution = _execute(plan, _token);
            _executions.Add(execution);
            _ = execution.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return execution;
        }
    }

    public async Task DrainAsync()
    {
        Task<bool>[] executions;
        lock (_gate)
        {
            executions = _executions.ToArray();
        }
        try
        {
            await Task.WhenAll(executions).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsRevoked)
        {
            // Cancellation revokes the phase, but all native execution cleanup
            // must finish before its provider-local object can be disposed.
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _lifetime.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _lifetime.Cancel();
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }
}
