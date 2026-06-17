// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Hex1b;
using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// A minimal HMP v1 client workload adapter dedicated to talking to DCP's
/// single-peer HMP1 server. Plugged into Hex1bTerminal via
/// <c>WithWorkload(adapter)</c> in place of the multi-head-aware
/// <c>Hex1b.Hmp1WorkloadAdapter</c>.
/// </summary>
/// <remarks>
/// <para>
/// DCP runs a deliberately tiny HMP1 server (Go,
/// <c>internal/hmp1/server.go</c>) that only handles <c>FrameInput</c> (0x04)
/// and <c>FrameResize</c> (0x05). It never sends a multi-head Hello with a
/// <c>PrimaryPeerId</c>, never broadcasts <c>RoleChange</c>, and silently
/// ignores <c>FrameRequestPrimary</c>. As a consequence, Hex1b's stock
/// <c>Hmp1WorkloadAdapter</c> would observe <c>IsPrimary == false</c> for the
/// entire connection lifetime and silently no-op every <c>ResizeAsync</c>
/// call, leaving the upstream PTY stuck at the size DCP started it with —
/// regardless of how the dashboard or CLI consumer is sized. That broke
/// resize forwarding end-to-end (REPL <c>resize</c> always reported its
/// initial dims).
/// </para>
/// <para>
/// This adapter takes the opposite tradeoff: there is exactly one peer on
/// this connection (us, talking to DCP), so there is no role to negotiate.
/// <see cref="ResizeAsync"/> writes a raw <c>FrameResize</c> upstream
/// unconditionally; the consumer-side multi-head server in the same
/// terminal host owns the consumer-facing role state.
/// </para>
/// <para>
/// Frame format (HMP v1):
/// <c>[type:1B][length:4B LE][payload:N bytes]</c>, max payload 16 MiB.
/// Frames originating from the producer (DCP) that we consume:
/// <c>FrameHello</c> (0x01) — JSON producer-info, payload kept opaque;
/// <c>FrameStateSync</c> (0x02) and <c>FrameOutput</c> (0x03) — both raw
/// terminal output bytes, fed into the output channel verbatim;
/// <c>FrameExit</c> (0x06) — workload exit signal, optionally carrying a
/// little-endian int32 exit code. Anything else is logged and ignored.
/// </para>
/// </remarks>
internal sealed class DcpUpstreamAdapter : IHex1bTerminalWorkloadAdapter
{
    private const byte FrameHello = 0x01;
    private const byte FrameStateSync = 0x02;
    private const byte FrameOutput = 0x03;
    private const byte FrameInput = 0x04;
    private const byte FrameResize = 0x05;
    private const byte FrameExit = 0x06;
    private const int MaxPayloadLength = 16 * 1024 * 1024;
    private const int FrameHeaderLength = 5;

    private readonly Func<CancellationToken, Task<Stream>> _streamFactory;
    private readonly ILogger<DcpUpstreamAdapter> _logger;
    private readonly Channel<ReadOnlyMemory<byte>> _outputChannel;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly TaskCompletionSource _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _pendingResizeGate = new();

    private Stream? _stream;
    private Task? _readPump;
    private int _completed;
    private int _pendingResizeWidth;
    private int _pendingResizeHeight;
    private volatile bool _disposed;

    /// <inheritdoc />
    public event Action? Disconnected;

    public DcpUpstreamAdapter(
        Func<CancellationToken, Task<Stream>> streamFactory,
        ILogger<DcpUpstreamAdapter> logger)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _outputChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        try
        {
            if (!await _outputChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                return ReadOnlyMemory<byte>.Empty;
            }
            if (!_outputChannel.Reader.TryRead(out var first))
            {
                return ReadOnlyMemory<byte>.Empty;
            }
            return first;
        }
        catch (ChannelClosedException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.IsEmpty || _disposed)
        {
            return;
        }

        if (!_connectedTcs.Task.IsCompletedSuccessfully)
        {
            try
            {
                await _connectedTcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await WriteFrameAsync(FrameInput, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }
        if (width <= 0 || height <= 0)
        {
            _logger.LogDebug(
                "DcpUpstreamAdapter: ignoring invalid resize ({Width}x{Height}).",
                width, height);
            return;
        }

        // Coalesce: if we haven't accepted DCP's dial yet, stash the latest dims and let
        // EnsureConnectedAsync apply them once the upstream stream is live. Avoids
        // blocking the consumer-side OnResized callback indefinitely waiting for the
        // upstream to dial in (which can take an arbitrary amount of time during
        // recycle / DCP restart).
        if (!_connectedTcs.Task.IsCompletedSuccessfully)
        {
            lock (_pendingResizeGate)
            {
                _pendingResizeWidth = width;
                _pendingResizeHeight = height;
            }
            return;
        }

        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), height);
        await WriteFrameAsync(FrameResize, payload, ct).ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connectedTcs.Task.IsCompletedSuccessfully)
        {
            return;
        }

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connectedTcs.Task.IsCompletedSuccessfully)
            {
                return;
            }
            if (_disposed)
            {
                _connectedTcs.TrySetCanceled(_disposeCts.Token);
                throw new ObjectDisposedException(nameof(DcpUpstreamAdapter));
            }

            // Always pass disposal CT to the factory so the listener can be torn
            // down even when the adapter consumer's per-call CT has not fired.
            // Otherwise a long-lived listen could outlive the surrounding terminal.
            Stream stream;
            try
            {
                stream = await _streamFactory(_disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                _connectedTcs.TrySetCanceled(_disposeCts.Token);
                Complete();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DcpUpstreamAdapter: streamFactory failed.");
                _connectedTcs.TrySetException(ex);
                Complete(ex);
                throw;
            }

            _stream = stream;
            _logger.LogInformation("DcpUpstreamAdapter: upstream stream established.");
            // Use the disposal CT for the pump's lifetime; the per-call CT (`ct`) here
            // is just for waiting on the connect lock and the streamFactory call.
            // The pump must outlive any single caller's token.
            _readPump = Task.Run(() => ReadPumpAsync(_disposeCts.Token), _disposeCts.Token);
            _connectedTcs.TrySetResult();

            // Apply any pending resize that arrived before we connected. Fire-and-forget
            // because EnsureConnectedAsync may itself be on the read path; we don't want
            // to make our caller wait on a write.
            int pendingW, pendingH;
            lock (_pendingResizeGate)
            {
                pendingW = _pendingResizeWidth;
                pendingH = _pendingResizeHeight;
                _pendingResizeWidth = 0;
                _pendingResizeHeight = 0;
            }
            if (pendingW > 0 && pendingH > 0)
            {
                _ = ApplyPendingResizeAsync(pendingW, pendingH);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ApplyPendingResizeAsync(int width, int height)
    {
        try
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), height);
            await WriteFrameAsync(FrameResize, payload, _disposeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "DcpUpstreamAdapter: applying coalesced post-connect resize ({Width}x{Height}) failed.",
                width, height);
        }
    }

    private async Task WriteFrameAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException(
                $"Payload length {payload.Length} exceeds maximum {MaxPayloadLength}.",
                nameof(payload));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (stream is null || _disposed)
            {
                return;
            }

            var header = new byte[FrameHeaderLength];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1, 4), payload.Length);

            // Once the writeLock is held, the FULL frame must be emitted atomically.
            // Honoring the caller's per-call CancellationToken between header and
            // payload writes would corrupt the upstream stream — DCP would parse the
            // next bytes as the previous frame's payload. Use the disposal CT only.
            try
            {
                await stream.WriteAsync(header, _disposeCts.Token).ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    await stream.WriteAsync(payload, _disposeCts.Token).ConfigureAwait(false);
                }

                // Count both header + payload because they're real socket bytes on the
                // producer (DCP-facing) UDS. Direction = "out" from the host's POV.
                TerminalHostTelemetry.Bytes.Add(
                    FrameHeaderLength + payload.Length,
                    new TagList { { "socket", "producer" }, { "direction", "out" } });

                if (type == FrameResize)
                {
                    TerminalHostTelemetry.ResizeRequests.Add(1, new TagList
                    {
                        { "direction", "upstream" },
                        { "result", "ok" },
                    });
                }
            }
            catch (Exception ex) when (ex is IOException
                                          or ObjectDisposedException
                                          or OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "DcpUpstreamAdapter: upstream write failed (type=0x{Type:X2}); marking disconnected.",
                    type);
                if (type == FrameResize)
                {
                    TerminalHostTelemetry.ResizeRequests.Add(1, new TagList
                    {
                        { "direction", "upstream" },
                        { "result", "failed" },
                    });
                }
                Complete(ex);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadPumpAsync(CancellationToken ct)
    {
        try
        {
            var header = new byte[FrameHeaderLength];
            while (!ct.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream is null)
                {
                    break;
                }

                if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
                {
                    break;
                }

                var type = header[0];
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
                if (length < 0 || length > MaxPayloadLength)
                {
                    _logger.LogError(
                        "DcpUpstreamAdapter: malformed frame length {Length} (type 0x{Type:X2}); aborting.",
                        length, type);
                    break;
                }

                var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
                {
                    break;
                }

                // Count header + payload as producer-direction bytes received. Done after the
                // full frame is in hand so partial reads (which short-circuit out of the loop)
                // are not double-counted.
                TerminalHostTelemetry.Bytes.Add(
                    FrameHeaderLength + payload.Length,
                    new TagList { { "socket", "producer" }, { "direction", "in" } });

                switch (type)
                {
                    case FrameOutput:
                    case FrameStateSync:
                        if (payload.Length > 0)
                        {
                            try
                            {
                                await _outputChannel.Writer.WriteAsync(payload, ct).ConfigureAwait(false);
                            }
                            catch (ChannelClosedException)
                            {
                                return;
                            }
                        }
                        break;
                    case FrameHello:
                        // Producer info; opaque to us.
                        break;
                    case FrameExit:
                        if (payload.Length >= 4)
                        {
                            var exitCode = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
                            _logger.LogDebug(
                                "DcpUpstreamAdapter: producer reported exit code {ExitCode}.", exitCode);
                        }
                        return;
                    default:
                        _logger.LogDebug(
                            "DcpUpstreamAdapter: ignoring unexpected frame type 0x{Type:X2} (length {Length}).",
                            type, length);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposal-driven shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DcpUpstreamAdapter: read pump terminated unexpectedly.");
        }
        finally
        {
            Complete();
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }

    private void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }
        _outputChannel.Writer.TryComplete(error);
        try
        {
            Disconnected?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DcpUpstreamAdapter: Disconnected handler threw (ignored).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            await _disposeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already cancelled.
        }

        if (_readPump is { } pump)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort wait. The CTS cancellation should unwind the pump shortly;
                // if it doesn't, we still proceed with disposal so we don't hang the host.
            }
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Already in dispose path; ignore.
        }

        Complete();

        _disposeCts.Dispose();
        _writeLock.Dispose();
        _connectLock.Dispose();
    }
}
