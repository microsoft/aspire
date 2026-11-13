// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Utils;

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
        var outputPrefixRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorPrefixRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputPump = DefaultProcessRunner.ReadStandardOutputAsync(output.Reader, channel.Writer, cancellation.Token);
        var errorPump = DefaultProcessRunner.ReadStandardErrorAsync(error.Reader, channel.Writer, cancellation.Token);
        await using var events = channel.Reader.ReadAllAsync(cancellation.Token)
            .OnStandardOutput(bytes => SignalPartialCharacter(bytes, outputPrefixRead))
            .OnStandardError(bytes => SignalPartialCharacter(bytes, errorPrefixRead))
            .DecodeText(Encoding.UTF8).GetAsyncEnumerator(cancellation.Token);
        try
        {
            var first = errorFirst ? error : output;
            var second = errorFirst ? output : error;

            // Read the visible prefixes separately so their order does not depend on
            // which pipe publisher runs first.
            await first.Writer.WriteAsync(new byte[] { (byte)'x', 0xE2 }, cancellation.Token);
            Assert.True(await events.MoveNextAsync());
            AssertText(events.Current, isError: errorFirst, "x");
            await second.Writer.WriteAsync(new byte[] { (byte)'y', 0xE2 }, cancellation.Token);
            Assert.True(await events.MoveNextAsync());
            AssertText(events.Current, isError: !errorFirst, "y");
            Assert.False(outputPump.IsCompleted);
            Assert.False(errorPump.IsCompleted);

            // A pipe read can split the prefix before its trailing 0xE2. Keep decoding
            // until both incomplete UTF-8 characters have entered the pipeline, before
            // allowing either publisher to report EOF.
            var firstFlush = events.MoveNextAsync().AsTask();
            await Task.WhenAll(outputPrefixRead.Task, errorPrefixRead.Task).WaitAsync(cancellation.Token);
            Assert.False(firstFlush.IsCompleted);
            first.Writer.Dispose();

            Assert.True(await firstFlush);
            AssertText(events.Current, isError: errorFirst, "�");
            Assert.True(await events.MoveNextAsync());
            AssertCompleted(events.Current, isError: errorFirst);
            await (errorFirst ? errorPump : outputPump);
            Assert.False((errorFirst ? outputPump : errorPump).IsCompleted);

            // The other decoder must retain its incomplete character across the first EOF.
            await second.Writer.WriteAsync(new byte[] { 0x82, 0xAC }, cancellation.Token);
            second.Writer.Dispose();
            await Task.WhenAll(outputPump, errorPump);
            channel.Writer.Complete();

            Assert.True(await events.MoveNextAsync());
            AssertText(events.Current, isError: !errorFirst, "€");
            Assert.True(await events.MoveNextAsync());
            AssertCompleted(events.Current, isError: !errorFirst);
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
        var reading = isError
            ? DefaultProcessRunner.ReadStandardErrorAsync(pipe.Reader, channel.Writer, cancellation.Token)
            : DefaultProcessRunner.ReadStandardOutputAsync(pipe.Reader, channel.Writer, cancellation.Token);
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

    private static void SignalPartialCharacter(ReadOnlyMemory<byte> bytes, TaskCompletionSource prefixRead)
    {
        if (bytes.Span.Contains((byte)0xE2))
        {
            prefixRead.TrySetResult();
        }
    }

    private static void AssertText(ProcessEvent<ReadOnlyMemory<char>> item, bool isError, string expected)
    {
        var content = isError
            ? Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardError>(item).Content
            : Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutput>(item).Content;

        Assert.Equal(expected, content.ToString());
    }

    private static void AssertCompleted(ProcessEvent<ReadOnlyMemory<char>> item, bool isError)
    {
        if (isError)
        {
            Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted>(item);
        }
        else
        {
            Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted>(item);
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
