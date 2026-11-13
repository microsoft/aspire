// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Aspire.Hosting.Dcp.Process;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ProcessStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachOutputFlushesAtItsOwnEofWhileOtherStreamRemainsOpen(bool errorFirst)
    {
        using var cancellation = CreateCancellation();
        using var output = await PipePair.CreateAsync(cancellation.Token);
        using var error = await PipePair.CreateAsync(cancellation.Token);
        var channel = Channel.CreateUnbounded<ProcessEvent<ReadOnlyMemory<byte>>>();
        var outputPump = DefaultProcessRunner.ReadOutputAsync(output.Reader, channel.Writer, isError: false, cancellation.Token);
        var errorPump = DefaultProcessRunner.ReadOutputAsync(error.Reader, channel.Writer, isError: true, cancellation.Token);
        await using var events = channel.Reader.ReadAllAsync(cancellation.Token)
            .DecodeText(Encoding.UTF8).ReadLines().GetAsyncEnumerator(cancellation.Token);
        try
        {
            var first = errorFirst ? error : output;
            var second = errorFirst ? output : error;
            await first.Writer.WriteAsync(new byte[] { (byte)'x', 0xE2 }, cancellation.Token);
            await second.Writer.WriteAsync(new byte[] { (byte)'y', 0xE2 }, cancellation.Token);
            Assert.False(outputPump.IsCompleted);
            Assert.False(errorPump.IsCompleted);

            first.Writer.Dispose();

            Assert.True(await events.MoveNextAsync());
            Assert.Equal(errorFirst ? new ProcessEvent<string>.StandardError("x�") : new ProcessEvent<string>.StandardOutput("x�"), events.Current);
            Assert.True(await events.MoveNextAsync());
            Assert.Equal(errorFirst ? (ProcessEvent<string>)new ProcessEvent<string>.StandardErrorCompleted() : new ProcessEvent<string>.StandardOutputCompleted(), events.Current);
            await (errorFirst ? errorPump : outputPump);
            Assert.False((errorFirst ? outputPump : errorPump).IsCompleted);

            // The other decoder must retain its incomplete character across the first EOF.
            await second.Writer.WriteAsync(new byte[] { 0x82, 0xAC }, cancellation.Token);
            second.Writer.Dispose();
            await Task.WhenAll(outputPump, errorPump);
            channel.Writer.Complete();

            Assert.True(await events.MoveNextAsync());
            Assert.Equal(errorFirst ? new ProcessEvent<string>.StandardOutput("y€") : new ProcessEvent<string>.StandardError("y€"), events.Current);
            Assert.True(await events.MoveNextAsync());
            Assert.Equal(errorFirst ? (ProcessEvent<string>)new ProcessEvent<string>.StandardOutputCompleted() : new ProcessEvent<string>.StandardErrorCompleted(), events.Current);
            Assert.False(await events.MoveNextAsync());
        }
        finally
        {
            output.Writer.Dispose();
            error.Writer.Dispose();
            await Task.WhenAll(outputPump, errorPump);
            channel.Writer.TryComplete();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledReadPreservesTokenAndDoesNotPublishEof(bool isError)
    {
        using var cancellation = CreateCancellation();
        using var pipe = await PipePair.CreateAsync(cancellation.Token);
        var channel = Channel.CreateUnbounded<ProcessEvent<ReadOnlyMemory<byte>>>();
        var reading = DefaultProcessRunner.ReadOutputAsync(pipe.Reader, channel.Writer, isError, cancellation.Token);
        await pipe.Writer.WriteAsync(new byte[] { (byte)'x' }, cancellation.Token);
        var item = await channel.Reader.ReadAsync(cancellation.Token);
        var content = isError
            ? Assert.IsType<ProcessEvent<ReadOnlyMemory<byte>>.StandardError>(item).Content
            : Assert.IsType<ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput>(item).Content;
        Assert.Equal(new byte[] { (byte)'x' }, content.ToArray());
        Assert.False(reading.IsCompleted);
        Assert.True(pipe.Writer.IsConnected);

        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NUL\0ü日本語\r\nno final newline")]
    public async Task InputIsTransferredExactlyAndClosedAtEof(string text)
    {
        using var cancellation = CreateCancellation();
        using var pipe = await PipePair.CreateAsync(cancellation.Token);
        using var captured = new MemoryStream();
        var reading = pipe.Reader.CopyToAsync(captured, cancellation.Token);
        Assert.True(pipe.Writer.IsConnected);
        Assert.False(reading.IsCompleted);
        try
        {
            var failure = await DefaultProcessRunner.WriteInputAsync(pipe.Writer, Encoding.UTF8.GetBytes(text), cancellation.Token, cancellation.Token);

            Assert.Null(failure);
            await reading;
            Assert.Equal(Encoding.UTF8.GetBytes(text), captured.ToArray());
            Assert.Throws<ObjectDisposedException>(pipe.Writer.Flush);
        }
        finally
        {
            pipe.Writer.Dispose();
            await reading;
        }
    }

    [Theory]
    [InlineData("input")]
    [InlineData("NUL\0ü日本語\r\nno final newline")]
    public async Task ClosedReaderReturnsDeliveryFailureAndClosesInput(string text)
    {
        using var cancellation = CreateCancellation();
        using var pipe = await PipePair.CreateAsync(cancellation.Token);
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.NotEmpty(bytes);
        Assert.True(pipe.Reader.IsConnected);
        pipe.Reader.Dispose();
        Assert.False(pipe.Reader.IsConnected);

        var failure = await DefaultProcessRunner.WriteInputAsync(pipe.Writer, bytes, cancellation.Token, cancellation.Token);

        Assert.IsAssignableFrom<IOException>(failure);
        Assert.Throws<ObjectDisposedException>(pipe.Writer.Flush);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedInputDistinguishesRootExitFromExecutionCancellation(bool cancelExecution)
    {
        using var executionStop = CreateCancellation();
        using var inputStop = CancellationTokenSource.CreateLinkedTokenSource(executionStop.Token);
        using var pipe = await PipePair.CreateAsync(executionStop.Token);
        var writing = DefaultProcessRunner.WriteInputAsync(pipe.Writer, new byte[2000000], inputStop.Token, executionStop.Token);
        try
        {
            Assert.True(pipe.Reader.IsConnected);
            Assert.False(writing.IsCompleted);

            (cancelExecution ? executionStop : inputStop).Cancel();

            var failure = await writing;
            if (cancelExecution)
            {
                Assert.Null(failure);
            }
            else
            {
                Assert.IsType<IOException>(failure);
                Assert.Equal("The process exited before standard input was delivered.", failure.Message);
                var cause = Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
                Assert.Equal(inputStop.Token, cause.CancellationToken);
            }
            Assert.Throws<ObjectDisposedException>(pipe.Writer.Flush);
        }
        finally
        {
            executionStop.Cancel();
            await writing;
        }
    }

    private static CancellationTokenSource CreateCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.LongTimeoutDuration);
        return cancellation;
    }

    private sealed class PipePair : IDisposable
    {
        private PipePair(string name)
        {
            Reader = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
            Writer = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        public NamedPipeServerStream Writer { get; }
        public NamedPipeClientStream Reader { get; }

        public static async Task<PipePair> CreateAsync(CancellationToken cancellationToken)
        {
            var pipe = new PipePair("process-stream-" + Guid.NewGuid().ToString("N"));
            try
            {
                await Task.WhenAll(pipe.Writer.WaitForConnectionAsync(cancellationToken), pipe.Reader.ConnectAsync(cancellationToken));
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Writer.Dispose();
            Reader.Dispose();
        }
    }
}
