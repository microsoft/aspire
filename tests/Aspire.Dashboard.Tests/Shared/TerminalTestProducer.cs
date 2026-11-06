// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Reflow;

namespace Aspire.Dashboard.Tests.Shared;

internal sealed class TerminalTestProducer : IAsyncDisposable
{
    private readonly ConcurrentBag<Task<Hmp1ClientHandle>> _connections = [];
    private readonly ConcurrentBag<ProducerOutputStream> _outputs = [];
    private readonly ConcurrentBag<Task> _disconnections = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Hex1bTerminal _terminal;
    private int _disposed;
    private long _outputDelayTicks;

    public TerminalTestProducer(int width, int height, int scrollback)
    {
        Workload = new Hex1bAppWorkloadAdapter();
        Presentation = new Hmp1PresentationAdapter(width, height)
            .WithReflow(GhosttyReflowStrategy.Instance);
        _terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(Workload)
            .WithPresentation(Presentation)
            .WithDimensions(width, height)
            .WithScrollback(scrollback)
            .Build();
    }

    public Hex1bAppWorkloadAdapter Workload { get; }
    public Hmp1PresentationAdapter Presentation { get; }
    public int Width => Workload.Width;
    public int Height => Workload.Height;
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Delays each later producer write to viewer transports, widening the window in which
    /// the producer has applied output that its HMP write pumps have not yet delivered.
    /// </summary>
    public TimeSpan OutputDelay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _outputDelayTicks));
        set => Interlocked.Exchange(ref _outputDelayTicks, value.Ticks);
    }

    public Stream Connect()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var toClient = new Pipe();
        var toServer = new Pipe();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ProducerOutputStream(toClient.Writer.AsStream(), () => OutputDelay);
        _outputs.Add(output);
        var server = new DuplexStream(toServer.Reader.AsStream(), output, onDispose: null);
        var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream(), () => disconnected.TrySetResult());
        _disconnections.Add(disconnected.Task);
        _connections.Add(Presentation.AddClient(server, _stopping.Token));
        return client;
    }

    public async Task WaitForProducerTextAsync(string text, CancellationToken cancellationToken)
    {
        using var snapshot = await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(snapshot => snapshot.ContainsText(text), TimeSpan.FromSeconds(10), "Terminal producer output was not applied.")
            .Build()
            .ApplyAsync(_terminal, cancellationToken);
    }

    public Task WaitForPeerHandshakesAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_connections).WaitAsync(cancellationToken);

    /// <summary>
    /// Completes the producer terminal and waits until every viewer transport has received
    /// HMP Exit. Hex1b queues Exit behind all output already queued for each viewer, whereas
    /// disposing the presentation discards undelivered output.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        Presentation.TerminalCompleted(exitCode: 0);
        await Task.WhenAll(_outputs.Select(output => output.ExitWrittenOrClosed)).WaitAsync(cancellationToken);
    }

    public Task WaitForAttachmentsReleasedAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_disconnections).WaitAsync(cancellationToken);

    internal Task CancelConnectionsAsync() => _stopping.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync();
        try
        {
            foreach (var connection in _connections)
            {
                try
                {
                    await using var handle = await connection;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // A cancelled request can end during the producer's initial handshake.
                }
            }
        }
        finally
        {
            await _terminal.DisposeAsync();
            _stopping.Dispose();
        }
    }

    private sealed class DuplexStream(Stream input, Stream output, Action? onDispose) : Stream
    {
        private int _disposed;

        public override bool CanRead => input.CanRead;
        public override bool CanWrite => output.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                input.Dispose();
                output.Dispose();
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ProducerOutputStream(Stream inner, Func<TimeSpan> delay) : Stream
    {
        private const byte ExitFrameType = 0x06;
        private const int FrameHeaderLength = 5;
        private readonly TaskCompletionSource _exitWrittenOrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _header = new byte[FrameHeaderLength];
        private int _headerLength;
        private long _payloadRemaining;
        private bool _inExitFrame;

        // HMP frames are a one-byte type, a four-byte little-endian payload length, then the payload.
        // https://github.com/mitchdenny/hex1b/blob/798b26c/docs/muxer-protocol.md
        public Task ExitWrittenOrClosed => _exitWrittenOrClosed.Task;

        public override bool CanRead => false;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay() is { Ticks: > 0 } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
            Observe(buffer.Span);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _exitWrittenOrClosed.TrySetResult();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        private void Observe(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                if (_payloadRemaining == 0)
                {
                    var copied = Math.Min(FrameHeaderLength - _headerLength, data.Length);
                    data[..copied].CopyTo(_header.AsSpan(_headerLength));
                    _headerLength += copied;
                    data = data[copied..];
                    if (_headerLength < FrameHeaderLength)
                    {
                        return;
                    }

                    _headerLength = 0;
                    _inExitFrame = _header[0] == ExitFrameType;
                    _payloadRemaining = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(1));
                }
                else
                {
                    var skipped = (int)Math.Min(_payloadRemaining, data.Length);
                    _payloadRemaining -= skipped;
                    data = data[skipped..];
                }

                if (_payloadRemaining == 0 && _inExitFrame)
                {
                    _exitWrittenOrClosed.TrySetResult();
                }
            }
        }
    }
}
