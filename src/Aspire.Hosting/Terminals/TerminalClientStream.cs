// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Limits Hex1b's access to a caller-owned transport to the lifetime of one viewer attachment.
/// </summary>
internal sealed class TerminalClientStream(Stream transport) : Stream
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disconnectCts = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _closing;

    public Task Released => _released.Task;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
            return await transport.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
            await transport.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
            await transport.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void EnterOperation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _operations++;
        }
    }

    private void ExitOperation()
    {
        lock (_gate)
        {
            if (--_operations == 0 && _closing)
            {
                _drained.TrySetResult();
            }
        }
    }

    public override ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closing)
            {
                return new ValueTask(Released);
            }

            _closing = true;
            if (_operations == 0)
            {
                _drained.TrySetResult();
            }
        }

        _ = ReleaseAsync();
        return new ValueTask(Released);
    }

    private async Task ReleaseAsync()
    {
        try
        {
            try
            {
                await _disconnectCts.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                // Hex1b 0.165.0 disposes each session's stream without joining its read/write pumps.
                // OnClientDisconnected also runs in parallel with that disposal, so neither the callback
                // nor the Dispose call alone is a transport-release signal. Reject new operations and
                // drain existing ones before allowing the gRPC handler to dispose its actual transport.
                // https://github.com/mitchdenny/hex1b/blob/39947cb9455dd39b8de6326c643baaf4e4324962/src/Hex1b/Hmp1/Hmp1PresentationAdapter.cs
                await _drained.Task.ConfigureAwait(false);
                _disconnectCts.Dispose();
            }

            _released.TrySetResult();
        }
        catch (Exception ex)
        {
            // Disposal can also be initiated by Hex1b, which suppresses transport cleanup errors.
            // Preserve those errors for the attachment owner instead.
            _released.TrySetException(ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
