// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Dcp;

/// <summary>Bounds DCP response reads without buffering or permitting seeks around the limit.</summary>
internal sealed class DcpResponseStream(Stream input, int limit) : Stream
{
    private int _remaining = limit;
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = input.Read(buffer[..Math.Min(buffer.Length, _remaining + 1)]);
        Account(count);

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Read one byte beyond the budget to distinguish exact-limit EOF from
        // an oversized chunked body. Never turn overflow into a truncated success.
        var count = await input.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining + 1)], cancellationToken).ConfigureAwait(false);
        Account(count);

        return count;
    }

    private void Account(int count)
    {
        if (count > _remaining)
        {
            throw new InvalidDataException("The DCP response exceeds the size limit.");
        }
        _remaining -= count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => input.DisposeAsync();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
