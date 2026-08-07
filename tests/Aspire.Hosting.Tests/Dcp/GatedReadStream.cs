// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Hosting.Tests.Dcp;

internal sealed class GatedReadStream : Stream
{
    private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ReadOnlyMemory<byte>> _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _offset;

    public Task ReadStarted => _readStarted.Task;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Release(string content = "")
    {
        if (!TryRelease(content))
        {
            throw new InvalidOperationException("The stream has already been released.");
        }
    }

    public bool TryRelease(string content = "")
    {
        return _content.TrySetResult(Encoding.UTF8.GetBytes(content));
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _readStarted.TrySetResult();

        // The test controls completion explicitly so it can reproduce a DCP stream that finishes after
        // its cancellation request and after a replacement stream has installed new deduplication state.
        var content = await _content.Task.ConfigureAwait(false);
        if (_offset == content.Length)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, content.Length - _offset);
        content.Span.Slice(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
