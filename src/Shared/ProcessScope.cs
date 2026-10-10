// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Retains a contained execution and its root-exit signal for a supervising owner.
/// </summary>
internal sealed class ProcessScope : IAsyncDisposable
{
    private readonly IChildProcess _execution;
    private readonly ILogger _logger;
    private readonly string _description;
    private int _disposed;

    internal ProcessScope(IChildProcess execution, ILogger logger, string description)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(execution.ProcessId);
        if (execution.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("A supervised child process scope cannot own its caller.");
        }
        _execution = execution;
        _logger = logger;
        _description = description;
        ProcessId = execution.ProcessId;
        Exit = execution.WaitForRootExitAsync(CancellationToken.None);
        _logger.LogDebug("Process scope '{Name}' started with supervisor PID {Pid}.", description, ProcessId);
    }

    public int ProcessId { get; }

    public Task<int> Exit { get; }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        => _execution.WaitForExitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            // ChildProcess owns the common shutdown ladder, containment verification, and
            // output drain. This owner wrapper must not implement another termination policy.
            await _execution.DisposeAsync().ConfigureAwait(false);
            _logger.LogDebug("Process scope '{Name}' (supervisor PID {Pid}) terminated and cleanup verified.", _description, ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up process scope '{Name}' (supervisor PID {Pid}).", _description, ProcessId);
            throw;
        }
    }

    internal async ValueTask DisposeAsync(Exception? operationFailure)
    {
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (operationFailure is not null)
        {
            throw new AggregateException(
                $"Operation '{_description}' failed and its process scope could not be cleaned up.",
                operationFailure, cleanupFailure);
        }
    }
}
