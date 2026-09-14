// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class GatedTerminalWorkloadAdapter : IHex1bTerminalWorkloadAdapter
{
    private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadStarted => _readStarted.Task;
    public Task DisposeStarted => _disposeStarted.Task;
    public bool IsDisposed { get; private set; }
    public Exception? DisposalException { get; init; }
    public event Action? Disconnected;

    public void ReleaseDispose() => _releaseDispose.TrySetResult();

    public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
    {
        _readStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return ReadOnlyMemory<byte>.Empty;
    }

    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _disposeStarted.TrySetResult();
        await _releaseDispose.Task;
        if (DisposalException is { } exception)
        {
            throw exception;
        }

        IsDisposed = true;
        Disconnected?.Invoke();
    }
}
