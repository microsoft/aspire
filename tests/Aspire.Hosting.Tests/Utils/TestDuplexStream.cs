// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestDuplexStream(Stream reader, Stream writer) : Stream
{
    public static (TestDuplexStream First, TestDuplexStream Second) CreatePair()
    {
        var firstToSecond = new Pipe();
        var secondToFirst = new Pipe();
        return (
            new TestDuplexStream(secondToFirst.Reader.AsStream(), firstToSecond.Writer.AsStream()),
            new TestDuplexStream(firstToSecond.Reader.AsStream(), secondToFirst.Writer.AsStream()));
    }

    public bool Disposed { get; private set; }

    public override bool CanRead => reader.CanRead;
    public override bool CanWrite => writer.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => reader.ReadAsync(buffer, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => writer.WriteAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => reader.ReadAsync(buffer, offset, count, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => writer.WriteAsync(buffer, offset, count, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
    public override void Flush() => writer.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            Disposed = true;
            reader.Dispose();
            writer.Dispose();
        }

        base.Dispose(disposing);
    }
}
