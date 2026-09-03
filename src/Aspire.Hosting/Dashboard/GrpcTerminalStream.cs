// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Google.Protobuf;
using Grpc.Core;

namespace Aspire.Hosting.Dashboard;

/// <summary>
/// Presents a bidirectional <c>AttachTerminal</c> gRPC call as a duplex <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// Hex1b's HMP1 server consumes plain streams (see <c>WithHmp1Server</c>), so tunneling a terminal session over gRPC
/// only requires adapting the call's message pairs back into a byte stream. HMP1 framing is preserved end to end and
/// is never interpreted here: gRPC message boundaries are unrelated to HMP1 frame boundaries, so reads hand back
/// whatever bytes are available and keep the unread remainder of a message for the next read.
/// </remarks>
internal sealed class GrpcTerminalStream : Stream
{
    private readonly IAsyncStreamReader<TerminalClientFrame> _requestStream;
    private readonly IServerStreamWriter<TerminalServerFrame> _responseStream;
    // gRPC response streams do not support concurrent writes. Hex1b writes terminal output from its own pump, so
    // serialize here rather than relying on the caller to do it.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ReadOnlyMemory<byte> _remainder;
    private bool _completed;

    public GrpcTerminalStream(
        IAsyncStreamReader<TerminalClientFrame> requestStream,
        IServerStreamWriter<TerminalServerFrame> responseStream)
    {
        _requestStream = requestStream;
        _responseStream = responseStream;
    }

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
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_remainder.IsEmpty)
        {
            if (_completed)
            {
                return 0;
            }

            if (!await _requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                _completed = true;
                return 0;
            }

            // The selector frame is consumed by AttachTerminal before this stream is created, but a client is free to
            // send further frames with no payload; those must not be reported as end of stream.
            _remainder = _requestStream.Current.Data.Memory;
        }

        var count = Math.Min(buffer.Length, _remainder.Length);
        _remainder[..count].CopyTo(buffer);
        _remainder = _remainder[count..];
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        // Copy rather than UnsafeWrap: the caller owns the buffer and may reuse it as soon as this method returns,
        // and gRPC does not guarantee the payload is serialized before the write task completes.
        var frame = new TerminalServerFrame { Data = ByteString.CopyFrom(buffer.Span) };

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // gRPC flushes per message, so there is nothing to flush here.
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writeLock.Dispose();
        }

        base.Dispose(disposing);
    }
}
