// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Reads bounded UTF-8 JSON-RPC frames without allocating an untrusted declared payload length.</summary>
internal sealed class NativeRpcFraming(Stream stream, int maximumRequestBytes)
{
    public NativeRpcFraming(Stream stream) : this(stream, NativeRpcConnection.MaximumRequestBytes)
    {
    }
    private readonly byte[] _buffer = new byte[8192];
    private int _position;
    private int _length;

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8192];
        var count = 0;
        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                if (count == 0)
                {
                    return null;
                }
                throw new EndOfStreamException("The JSON-RPC frame header is incomplete.");
            }
            if (count == header.Length)
            {
                throw new InvalidDataException("The JSON-RPC frame header exceeds the limit.");
            }
            if (next > 127)
            {
                throw new InvalidDataException("The JSON-RPC frame header must be ASCII.");
            }
            header[count++] = (byte)next;
            if (count >= 4 && header[count - 4] == '\r' && header[count - 3] == '\n' &&
                header[count - 2] == '\r' && header[count - 1] == '\n')
            {
                break;
            }
        }

        // StreamJsonRpc and vscode-jsonrpc use LSP-style frames:
        //   Content-Length: 42\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{...}
        // Content-Length counts UTF-8 bytes, not characters. Reject duplicate
        // lengths and over-limit frames before allocating or reading the body.
        int? contentLength = null;
        foreach (var line in Encoding.ASCII.GetString(header, 0, count - 4).Split("\r\n", StringSplitOptions.None))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("The JSON-RPC frame header is malformed.");
            }
            var name = line[..separator];
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (contentLength is not null ||
                    !int.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                    length > maximumRequestBytes)
                {
                    throw new InvalidDataException("The JSON-RPC frame length is invalid.");
                }
                contentLength = length;
            }
            else if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The JSON-RPC frame header is not supported.");
            }
        }
        if (contentLength is null)
        {
            throw new InvalidDataException("The JSON-RPC frame has no content length.");
        }
        var body = new byte[contentLength.Value];
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
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position == _length)
        {
            _length = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;
            if (_length == 0)
            {
                return -1;
            }
        }

        return _buffer[_position++];
    }
}
