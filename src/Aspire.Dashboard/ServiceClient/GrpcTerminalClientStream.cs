// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Google.Protobuf;
using Grpc.Core;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Client-side counterpart of the AppHost's <c>GrpcTerminalStream</c>: presents an <c>AttachTerminal</c> call as a
/// duplex <see cref="Stream"/> carrying opaque HMP1 bytes.
/// </summary>
/// <remarks>
/// The dashboard is a byte-level relay between the browser WebSocket and the AppHost, so it never interprets HMP1
/// framing. gRPC message boundaries are unrelated to HMP1 frame boundaries: reads hand back whatever bytes are
/// available and keep the unread remainder of a message for the next read.
/// </remarks>
internal sealed class GrpcTerminalClientStream : Stream
{
    private readonly AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame> _call;
    private readonly int _interactionId;
    private readonly string _inputName;
    // The linked CTS that scopes the call outlives the method that created it, so the stream owns its disposal.
    private readonly IDisposable? _callScope;
    // gRPC request streams do not support concurrent writes, and the WebSocket pump is not guaranteed to be the only
    // writer, so serialize here rather than relying on the caller.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ReadOnlyMemory<byte> _remainder;
    private bool _completed;
    private bool _disposed;

    public GrpcTerminalClientStream(
        AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame> call,
        int interactionId,
        string inputName,
        IDisposable? callScope = null)
    {
        _call = call;
        _interactionId = interactionId;
        _inputName = inputName;
        _callScope = callScope;
    }

    /// <summary>
    /// Sends the selector frame that tells the AppHost which interaction input this call is attaching to. The AppHost
    /// reads exactly one such frame before handing the call to Hex1b, so this must happen before any payload.
    /// </summary>
    public Task SendSelectorAsync(CancellationToken cancellationToken)
    {
        var frame = new TerminalClientFrame
        {
            InteractionId = _interactionId,
            InputName = _inputName
        };

        return _call.RequestStream.WriteAsync(frame, cancellationToken);
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

            if (!await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                _completed = true;
                return 0;
            }

            // A zero-length payload is not end of stream; keep waiting for real bytes.
            _remainder = _call.ResponseStream.Current.Data.Memory;
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
        //
        // The interaction id and input name are only set on the selector frame; the AppHost ignores them afterwards.
        var frame = new TerminalClientFrame { Data = ByteString.CopyFrom(buffer.Span) };

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _call.RequestStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
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
        if (disposing && !_disposed)
        {
            _disposed = true;

            // Disposing the call is what tears the tunnel down: the AppHost sees the request stream end and releases
            // the terminal session's attachment. Best effort because the call may already be faulted or cancelled.
            try
            {
                _call.Dispose();
            }
            catch
            {
                // Nothing useful to do; the connection is going away regardless.
            }

            _writeLock.Dispose();
            _callScope?.Dispose();
        }

        base.Dispose(disposing);
    }
}
