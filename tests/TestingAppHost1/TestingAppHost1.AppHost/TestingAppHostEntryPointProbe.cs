// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

public static class TestingAppHostEntryPointProbe
{
    private const string ArgumentPrefix = "--entry-point-exit-probe=";
    private const string BreakpointArgumentPrefix = "--entry-point-breakpoint-probe=";
    private const string BuildArgumentPrefix = "--entry-point-build-probe=";
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> s_probes = new();
    private static readonly ConcurrentDictionary<string, BreakpointSignal> s_breakpoints = new();
    private static readonly ConcurrentDictionary<string, BuildProbe> s_buildProbes = new();

    public static BuildProbe CreateBuildProbe()
    {
        var probe = new BuildProbe(Guid.NewGuid().ToString("N"));
        if (!s_buildProbes.TryAdd(probe.Id, probe))
        {
            throw new InvalidOperationException($"Could not create entry-point build probe '{probe.Id}'.");
        }

        return probe;
    }

    public static DistributedApplication Build(IDistributedApplicationBuilder builder, string[] args)
    {
        // The test passes the probe as:
        //   --entry-point-build-probe=<32-character GUID>
        var argument = args.FirstOrDefault(arg => arg.StartsWith(BuildArgumentPrefix, StringComparison.Ordinal));
        if (argument is null)
        {
            return builder.Build();
        }

        var id = argument[BuildArgumentPrefix.Length..];
        if (!s_buildProbes.TryGetValue(id, out var probe))
        {
            throw new InvalidOperationException($"Entry-point build probe '{id}' was not registered.");
        }

        // AppHosts can build with a SynchronizationContext installed (for example, a UI host). Task continuations
        // completed from inside Build() are then never inlined on this thread, so anything the testing
        // infrastructure publishes through such a continuation is not yet visible when Build() returns.
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ProbeSynchronizationContext());
        probe.EnterBuild();
        try
        {
            return builder.Build();
        }
        finally
        {
            probe.ExitBuild();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    public static Probe Create()
    {
        var id = Guid.NewGuid().ToString("N");
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!s_probes.TryAdd(id, exited))
        {
            throw new InvalidOperationException($"Could not create entry-point probe '{id}'.");
        }

        return new Probe(id, exited.Task);
    }

    public static IDisposable Track(string[] args)
    {
        // The test passes the probe as:
        //   --entry-point-exit-probe=<32-character GUID>
        var argument = args.FirstOrDefault(arg => arg.StartsWith(ArgumentPrefix, StringComparison.Ordinal));
        if (argument is null)
        {
            return EmptyDisposable.Instance;
        }

        var id = argument[ArgumentPrefix.Length..];
        if (!s_probes.TryGetValue(id, out var exited))
        {
            throw new InvalidOperationException($"Entry-point probe '{id}' was not registered.");
        }

        return new ExitSignal(exited);
    }

    public static BreakpointProbe CreateBreakpoint()
    {
        var id = Guid.NewGuid().ToString("N");
        var signal = new BreakpointSignal(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!s_breakpoints.TryAdd(id, signal))
        {
            throw new InvalidOperationException($"Could not create entry-point breakpoint probe '{id}'.");
        }

        return new BreakpointProbe(id, signal.Reached, signal.Continue);
    }

    public static async Task WaitAtBreakpointAsync(string[] args)
    {
        // The test passes the probe as:
        //   --entry-point-breakpoint-probe=<32-character GUID>
        var argument = args.FirstOrDefault(arg => arg.StartsWith(BreakpointArgumentPrefix, StringComparison.Ordinal));
        if (argument is null)
        {
            return;
        }

        var id = argument[BreakpointArgumentPrefix.Length..];
        if (!s_breakpoints.TryGetValue(id, out var signal))
        {
            throw new InvalidOperationException($"Entry-point breakpoint probe '{id}' was not registered.");
        }

        signal.Reached.TrySetResult();
        try
        {
            await signal.Continue.Task.ConfigureAwait(false);
        }
        finally
        {
            s_breakpoints.TryRemove(id, out _);
        }
    }

    public sealed class Probe(string id, Task exited) : IDisposable
    {
        public string Id { get; } = id;

        public Task Exited { get; } = exited;

        public void Dispose()
        {
            s_probes.TryRemove(Id, out _);
        }
    }

    public sealed class BreakpointProbe : IDisposable
    {
        private readonly TaskCompletionSource _continue;

        internal BreakpointProbe(string id, TaskCompletionSource reached, TaskCompletionSource @continue)
        {
            Id = id;
            _continue = @continue;
            Reached = reached.Task;
        }

        public string Id { get; }

        public Task Reached { get; }

        public void Continue()
        {
            _continue.TrySetResult();
        }

        public void Dispose()
        {
            Continue();
            if (Reached.IsCompleted)
            {
                s_breakpoints.TryRemove(Id, out _);
            }
        }
    }

    public sealed class BuildProbe : IDisposable
    {
        private readonly object _lock = new();
        private int _buildThreadId;
        private bool _isBuilding;

        internal BuildProbe(string id)
        {
            Id = id;
        }

        public string Id { get; }

        // True only when called synchronously from within Build() on the thread that is building the application.
        public bool IsBuildingOnCurrentThread
        {
            get
            {
                lock (_lock)
                {
                    return _isBuilding && _buildThreadId == Environment.CurrentManagedThreadId;
                }
            }
        }

        internal void EnterBuild()
        {
            lock (_lock)
            {
                _buildThreadId = Environment.CurrentManagedThreadId;
                _isBuilding = true;
            }
        }

        internal void ExitBuild()
        {
            lock (_lock)
            {
                _isBuilding = false;
            }
        }

        public void Dispose()
        {
            s_buildProbes.TryRemove(Id, out _);
        }
    }

    private sealed class ProbeSynchronizationContext : SynchronizationContext
    {
    }

    private sealed class ExitSignal(TaskCompletionSource exited) : IDisposable
    {
        public void Dispose()
        {
            exited.TrySetResult();
        }
    }

    private sealed record BreakpointSignal(TaskCompletionSource Reached, TaskCompletionSource Continue);

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
