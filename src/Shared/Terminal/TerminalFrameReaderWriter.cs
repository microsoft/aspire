// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;

namespace Aspire.Terminal;

/// <summary>
/// Reads Aspire Terminal Protocol frames from a stream.
/// See docs/specs/terminal-protocol.md for the full specification.
/// </summary>
internal sealed class TerminalFrameReader(Stream stream)
{
    private readonly byte[] _headerBuffer = new byte[TerminalProtocol.FrameHeaderSize];

    /// <summary>
    /// Reads the next frame from the stream. Returns <c>null</c> if the stream is closed.
    /// </summary>
    public async ValueTask<TerminalFrame?> ReadFrameAsync(CancellationToken ct = default)
    {
        // Read the 5-byte header
        var bytesRead = await ReadExactAsync(_headerBuffer, ct).ConfigureAwait(false);
        if (bytesRead == 0)
        {
            return null; // Stream closed
        }

        var type = _headerBuffer[0];
        var payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(_headerBuffer.AsSpan(1));

        if (payloadLength > TerminalProtocol.MaxPayloadSize)
        {
            throw new InvalidOperationException(
                $"Terminal protocol error: payload length {payloadLength} exceeds maximum {TerminalProtocol.MaxPayloadSize}.");
        }

        // Read the payload
        byte[] payload;
        if (payloadLength > 0)
        {
            payload = new byte[payloadLength];
            var payloadRead = await ReadExactAsync(payload, ct).ConfigureAwait(false);
            if (payloadRead == 0)
            {
                return null; // Stream closed mid-frame
            }
        }
        else
        {
            payload = [];
        }

        return new TerminalFrame(type, payload);
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes, handling partial reads.
    /// Returns 0 if the stream is closed before any bytes are read.
    /// </summary>
    private async ValueTask<int> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? 0 : throw new InvalidOperationException(
                    "Terminal protocol error: stream closed mid-frame.");
            }

            offset += read;
        }

        return offset;
    }
}

/// <summary>
/// Writes Aspire Terminal Protocol frames to a stream.
/// See docs/specs/terminal-protocol.md for the full specification.
/// </summary>
internal sealed class TerminalFrameWriter(Stream stream)
{
    private readonly byte[] _headerBuffer = new byte[TerminalProtocol.FrameHeaderSize];

    /// <summary>
    /// Writes a frame to the stream.
    /// </summary>
    public async ValueTask WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > TerminalProtocol.MaxPayloadSize)
        {
            throw new ArgumentException(
                $"Payload length {payload.Length} exceeds maximum {TerminalProtocol.MaxPayloadSize}.",
                nameof(payload));
        }

        _headerBuffer[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(_headerBuffer.AsSpan(1), (uint)payload.Length);

        await stream.WriteAsync(_headerBuffer, ct).ConfigureAwait(false);

        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a HELLO frame with the specified dimensions and flags.
    /// </summary>
    public ValueTask WriteHelloAsync(ushort cols, ushort rows, TerminalProtocol.HelloFlags flags, CancellationToken ct = default)
    {
        Span<byte> payload = stackalloc byte[TerminalProtocol.PayloadSize.Hello];
        payload[0] = TerminalProtocol.Version;
        BinaryPrimitives.WriteUInt16BigEndian(payload[1..], cols);
        BinaryPrimitives.WriteUInt16BigEndian(payload[3..], rows);
        payload[5] = (byte)flags;
        return WriteFrameAsync(TerminalProtocol.MessageType.Hello, payload.ToArray(), ct);
    }

    /// <summary>
    /// Writes a DATA frame with the specified raw terminal bytes.
    /// </summary>
    public ValueTask WriteDataAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        return WriteFrameAsync(TerminalProtocol.MessageType.Data, data, ct);
    }

    /// <summary>
    /// Writes a RESIZE frame with the specified dimensions.
    /// </summary>
    public ValueTask WriteResizeAsync(ushort cols, ushort rows, CancellationToken ct = default)
    {
        Span<byte> payload = stackalloc byte[TerminalProtocol.PayloadSize.Resize];
        BinaryPrimitives.WriteUInt16BigEndian(payload, cols);
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..], rows);
        return WriteFrameAsync(TerminalProtocol.MessageType.Resize, payload.ToArray(), ct);
    }

    /// <summary>
    /// Writes an EXIT frame with the specified exit code and reason.
    /// </summary>
    public ValueTask WriteExitAsync(int exitCode, TerminalProtocol.ExitReason reason, CancellationToken ct = default)
    {
        Span<byte> payload = stackalloc byte[TerminalProtocol.PayloadSize.Exit];
        BinaryPrimitives.WriteInt32BigEndian(payload, exitCode);
        payload[4] = (byte)reason;
        return WriteFrameAsync(TerminalProtocol.MessageType.Exit, payload.ToArray(), ct);
    }

    /// <summary>
    /// Writes a CLOSE frame.
    /// </summary>
    public ValueTask WriteCloseAsync(CancellationToken ct = default)
    {
        return WriteFrameAsync(TerminalProtocol.MessageType.Close, ReadOnlyMemory<byte>.Empty, ct);
    }
}

/// <summary>
/// Represents a single frame in the Aspire Terminal Protocol.
/// </summary>
internal readonly record struct TerminalFrame(byte Type, byte[] Payload)
{
    /// <summary>
    /// Parses the payload as a HELLO message.
    /// </summary>
    public (byte Version, ushort Cols, ushort Rows, TerminalProtocol.HelloFlags Flags) ParseHello()
    {
        if (Payload.Length < TerminalProtocol.PayloadSize.Hello)
        {
            throw new InvalidOperationException(
                $"Invalid HELLO payload: expected {TerminalProtocol.PayloadSize.Hello} bytes, got {Payload.Length}.");
        }

        return (
            Payload[0],
            BinaryPrimitives.ReadUInt16BigEndian(Payload.AsSpan(1)),
            BinaryPrimitives.ReadUInt16BigEndian(Payload.AsSpan(3)),
            (TerminalProtocol.HelloFlags)Payload[5]
        );
    }

    /// <summary>
    /// Parses the payload as a RESIZE message.
    /// </summary>
    public (ushort Cols, ushort Rows) ParseResize()
    {
        if (Payload.Length < TerminalProtocol.PayloadSize.Resize)
        {
            throw new InvalidOperationException(
                $"Invalid RESIZE payload: expected {TerminalProtocol.PayloadSize.Resize} bytes, got {Payload.Length}.");
        }

        return (
            BinaryPrimitives.ReadUInt16BigEndian(Payload.AsSpan(0)),
            BinaryPrimitives.ReadUInt16BigEndian(Payload.AsSpan(2))
        );
    }

    /// <summary>
    /// Parses the payload as an EXIT message.
    /// </summary>
    public (int ExitCode, TerminalProtocol.ExitReason Reason) ParseExit()
    {
        if (Payload.Length < TerminalProtocol.PayloadSize.Exit)
        {
            throw new InvalidOperationException(
                $"Invalid EXIT payload: expected {TerminalProtocol.PayloadSize.Exit} bytes, got {Payload.Length}.");
        }

        return (
            BinaryPrimitives.ReadInt32BigEndian(Payload.AsSpan(0)),
            (TerminalProtocol.ExitReason)Payload[4]
        );
    }
}
