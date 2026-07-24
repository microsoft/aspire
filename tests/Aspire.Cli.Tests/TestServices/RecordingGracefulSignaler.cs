// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class RecordingGracefulSignaler : IProcessTreeGracefulShutdownSignaler
{
    private readonly object _lock = new();
    private readonly Func<int, Task<bool>>? _onSignal;
    private readonly List<int> _pids = new();

    public RecordingGracefulSignaler(Func<int, Task<bool>>? onSignal = null)
    {
        _onSignal = onSignal;
    }

    public IReadOnlyList<int> Pids
    {
        get
        {
            lock (_lock)
            {
                return _pids.ToArray();
            }
        }
    }

    public Task<bool> RequestProcessTreeGracefulShutdownAsync(
        int pid,
        DateTimeOffset? startTime,
        bool includeStartTimeForDcp,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _pids.Add(pid);
        }

        return _onSignal?.Invoke(pid) ?? Task.FromResult(true);
    }
}
