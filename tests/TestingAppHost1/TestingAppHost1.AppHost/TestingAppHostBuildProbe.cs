// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class TestingAppHostBuildProbe
{
    private static readonly ConcurrentDictionary<string, ProbeState> s_probes = new();

    public static Probe Create()
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new ProbeState();
        if (!s_probes.TryAdd(id, state))
        {
            throw new InvalidOperationException($"Could not create build probe '{id}'.");
        }

        return new Probe(id, state);
    }

    internal static void Configure(IDistributedApplicationBuilder builder, string id)
    {
        if (!s_probes.TryGetValue(id, out var state))
        {
            throw new InvalidOperationException($"Build probe '{id}' was not registered.");
        }

        // The testing factory pauses in its OnBuilding callback. Blocking the first resource-name read after that
        // callback creates a deterministic window where BuildAsync has released the AppHost but Build has not finished.
        builder.Resources.Add(new BlockingResource($"build-probe-{id}", builder.Services, state));
        state.Arm();
    }

    internal static void SignalEntryPointFailure(string id)
    {
        if (!s_probes.TryGetValue(id, out var state))
        {
            throw new InvalidOperationException($"Build probe '{id}' was not registered.");
        }

        state.EntryPointFailure.TrySetResult();
    }

    public sealed class Probe : IDisposable
    {
        private readonly ProbeState _state;

        internal Probe(string id, ProbeState state)
        {
            Id = id;
            _state = state;
        }

        public string Id { get; }

        public Task BuildEntered => _state.BuildEntered.Task;

        public Task EntryPointFailure => _state.EntryPointFailure.Task;

        public Task ApplicationDisposed => _state.ApplicationDisposed.Task;

        public void ContinueBuilding()
        {
            _state.ContinueBuilding.TrySetResult();
        }

        public void Dispose()
        {
            ContinueBuilding();
            s_probes.TryRemove(Id, out _);
        }
    }

    internal sealed class ProbeState
    {
        private int _armed;
        private int _buildObserved;

        public TaskCompletionSource BuildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueBuilding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EntryPointFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ApplicationDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm()
        {
            Volatile.Write(ref _armed, 1);
        }

        public bool TryObserveBuild()
        {
            return Volatile.Read(ref _armed) == 1 &&
                Interlocked.CompareExchange(ref _buildObserved, 1, 0) == 0;
        }
    }

    private sealed class BlockingResource(
        string name,
        IServiceCollection services,
        ProbeState state) : Resource(name)
    {
        public override string Name
        {
            get
            {
                if (state.TryObserveBuild())
                {
                    var hostDescriptor = services.Single(
                        descriptor => descriptor.ServiceType == typeof(IHost) && descriptor.ServiceKey is null);
                    var hostFactory = hostDescriptor.ImplementationFactory
                        ?? throw new InvalidOperationException("The AppHost IHost registration did not use a factory.");

                    // Resolve the tracker as part of IHost creation so disposal proof does not depend on the
                    // application reaching StartAsync after a canceled build.
                    services.Remove(hostDescriptor);
                    services.AddSingleton(_ => new ProbeDisposalTracker(state));
                    services.Add(ServiceDescriptor.Describe(
                        typeof(IHost),
                        serviceProvider =>
                        {
                            _ = serviceProvider.GetRequiredService<ProbeDisposalTracker>();
                            return hostFactory(serviceProvider);
                        },
                        hostDescriptor.Lifetime));

                    state.BuildEntered.TrySetResult();
                    state.ContinueBuilding.Task.GetAwaiter().GetResult();
                }

                return base.Name;
            }
        }
    }

    private sealed class ProbeDisposalTracker(ProbeState state) : IDisposable
    {
        public void Dispose()
        {
            state.ApplicationDisposed.TrySetResult();
        }
    }
}
