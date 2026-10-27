// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Text;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Reads bounded UTF-8 JSON-RPC frames without allocating an untrusted declared payload length.</summary>
internal sealed class NativeRpcFraming(Stream stream, int maximumRequestBytes)
{
    public NativeRpcFraming(Stream stream) : this(stream, NativeServerOptions.DefaultMaximumRequestBytes)
    {
    }
    private readonly byte[] _buffer = new byte[8192];
    private readonly byte[] _header = new byte[NativeRpcFrameProtocol.MaximumHeaderBytes];
    private int _position;
    private int _length;

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var header = _header;
        var count = 0;
        while (true)
        {
            if (_position == _length)
            {
                _length = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    if (count == 0)
                    {
                        return null;
                    }
                    throw new EndOfStreamException("The JSON-RPC frame header is incomplete.");
                }
            }
            var available = _buffer.AsSpan(_position, _length - _position);
            var newline = available.IndexOf((byte)'\n');
            var copied = newline < 0 ? available.Length : newline + 1;
            if (copied > header.Length - count)
            {
                throw new InvalidDataException("The JSON-RPC frame header exceeds the limit.");
            }
            var chunk = available[..copied];
            if (chunk.IndexOfAnyInRange((byte)128, byte.MaxValue) >= 0)
            {
                throw new InvalidDataException("The JSON-RPC frame header must be ASCII.");
            }
            chunk.CopyTo(header.AsSpan(count));
            count += copied;
            _position += copied;
            if (count >= 4 && header.AsSpan(count - 4, 4).SequenceEqual("\r\n\r\n"u8))
            {
                break;
            }
        }

        var contentLength = NativeRpcFrameProtocol.ReadContentLength(header.AsSpan(0, count - 4), maximumRequestBytes);
        var body = new byte[contentLength];
        for (var index = 0; index < body.Length;)
        {
            if (_position < _length)
            {
                var copied = Math.Min(body.Length - index, _length - _position);
                _buffer.AsMemory(_position, copied).CopyTo(body.AsMemory(index));
                _position += copied;
                index += copied;
            }
            else
            {
                await stream.ReadExactlyAsync(body.AsMemory(index), cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        return body;
    }

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = ArrayPool<byte>.Shared.Rent(32);
        try
        {
            // Sixteen prefix bytes, at most ten Int32 digits and four delimiter
            // bytes fit in this lease. Keep it until both writes and flush complete.
            "Content-Length: "u8.CopyTo(header);
            Utf8Formatter.TryFormat(payload.Length, header.AsSpan(16), out var digits);
            "\r\n\r\n"u8.CopyTo(header.AsSpan(16 + digits));
            await stream.WriteAsync(header.AsMemory(0, 20 + digits), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }
}
