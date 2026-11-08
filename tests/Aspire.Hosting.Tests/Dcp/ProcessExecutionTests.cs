// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ProcessExecutionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("")]
    [InlineData("NUL\0ü日本語\r\n\nno final newline")]
    public async Task InputIsOwnedAndCapturePreservesExactText(string text)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            var bytes = input.ToArray();
            await Console.OpenStandardOutput().WriteAsync(bytes);
            await Console.OpenStandardError().WriteAsync(bytes);
            """);
        var bytes = Encoding.UTF8.GetBytes(text);
        var input = ProcessInput.FromBytes(bytes);
        Assert.Equal(bytes, input.Bytes.ToArray());
        Array.Fill(bytes, (byte)'X');
        Assert.Equal(Encoding.UTF8.GetBytes(text), input.Bytes.ToArray());

        var result = await CreateExecution(child, input).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessCaptureResult(0, text, text), result);
        Assert.Equal(Encoding.UTF8.GetBytes(text), ProcessInput.FromText(text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)).Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransfersBothOutputsWhileLargeInputIsBlocked(bool errorFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            var output = Console.OpenStandardOutput();
            var error = Console.OpenStandardError();
            var prefix = Encoding.UTF8.GetBytes(new string('ü', 200000));
            var first = args[0] == "True" ? error : output;
            var second = args[0] == "True" ? output : error;
            await Task.WhenAll(first.WriteAsync(prefix).AsTask(), second.WriteAsync(prefix).AsTask());
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            await Task.WhenAll(output.WriteAsync(input.ToArray()).AsTask(), error.WriteAsync(input.ToArray()).AsTask());
            """);
        var input = new string('日', 200000);
        Assert.True(Encoding.UTF8.GetByteCount(input) > 400000);
        using var cancellation = CreateCancellation();

        var result = await CreateExecution(child, ProcessInput.FromText(input, Encoding.UTF8), errorFirst.ToString()).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellation.Token);

        Assert.Equal(new ProcessCaptureResult(0, new string('ü', 200000) + input, new string('ü', 200000) + input), result);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(0)]
    public async Task RejectedInputPreservesNonzeroDiagnosticsButIsNotSuccessfulDelivery(int exitCode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            if (OperatingSystem.IsWindows())
            {
                if (!CloseHandle(GetStdHandle(-10))) { throw new Exception("Closing stdin failed."); }
            }
            else if (Close(0) != 0) { throw new Exception("Closing stdin failed."); }
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("output\0no newline"));
            await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes(new string('E', 220000) + "late ü\0\r\n"));
            Environment.Exit(int.Parse(args[0]));
            [DllImport("kernel32.dll", SetLastError = true)] static extern nint GetStdHandle(int value);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
            [DllImport("libc", EntryPoint = "close", SetLastError = true)] static extern int Close(int descriptor);
            """);
        using var cancellation = CreateCancellation();
        var capture = CreateExecution(child, ProcessInput.FromBytes(new byte[2000000]), exitCode.ToString()).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellation.Token);
        if (exitCode == 0)
        {
            await Assert.ThrowsAsync<IOException>(() => capture);
        }
        else
        {
            Assert.Equal(new ProcessCaptureResult(exitCode, "output\0no newline", new string('E', 220000) + "late ü\0\r\n"), await capture);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachOutputFlushesAtItsOwnEofBeforeRootExit(bool errorFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            var isError = args[1] == "True";
            await (isError ? Console.OpenStandardError() : Console.OpenStandardOutput()).WriteAsync(new byte[] { (byte)'x', 0xE2 });
            if (OperatingSystem.IsWindows())
            {
                if (!CloseHandle(GetStdHandle(isError ? -12 : -11))) { throw new Exception("Closing output failed."); }
            }
            else if (Close(isError ? 2 : 1) != 0) { throw new Exception("Closing output failed."); }
            await pipe.ReadExactlyAsync(new byte[1]);
            [DllImport("kernel32.dll", SetLastError = true)] static extern nint GetStdHandle(int value);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
            [DllImport("libc", EntryPoint = "close", SetLastError = true)] static extern int Close(int descriptor);
            """);
        var name = "process-eof-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = CreateCancellation();
        await using var events = CreateExecution(child, arguments: [name, errorFirst.ToString()])
            .DecodeText(Encoding.UTF8).ReadLines().GetAsyncEnumerator(cancellation.Token);
        Assert.True(await events.MoveNextAsync());
        var started = Assert.IsType<ProcessEvent<string>.Started>(events.Current);
        using var process = Process.GetProcessById(started.ProcessId);
        await pipe.WaitForConnectionAsync(cancellation.Token);
        Assert.False(process.HasExited);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(errorFirst ? new ProcessEvent<string>.StandardError("x�") : new ProcessEvent<string>.StandardOutput("x�"), events.Current);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(errorFirst ? (ProcessEvent<string>)new ProcessEvent<string>.StandardErrorCompleted() : new ProcessEvent<string>.StandardOutputCompleted(), events.Current);
        Assert.False(process.HasExited);
        await pipe.WriteAsync(new byte[] { 1 }, cancellation.Token);

        var remaining = new List<ProcessEvent<string>>();
        while (await events.MoveNextAsync()) { remaining.Add(events.Current); }
        Assert.Equal(2, remaining.Count);
        Assert.Equal(errorFirst ? (ProcessEvent<string>)new ProcessEvent<string>.StandardOutputCompleted() : new ProcessEvent<string>.StandardErrorCompleted(), remaining[0]);
        Assert.Equal(new ProcessEvent<string>.Exited(0), remaining[1]);
        Assert.True(process.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDisposalStopsChildWithoutReadingAnotherEvent(bool cancel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            await pipe.WriteAsync(new byte[] { 1 });
            await Console.OpenStandardOutput().WriteAsync(new byte[2000000]);
            await pipe.ReadExactlyAsync(new byte[1]);
            """);
        var name = "process-stop-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = CreateCancellation();
        var execution = CreateExecution(child, ProcessInput.FromBytes(new byte[2000000]), name);
        var events = execution.DiscardStandardError()
            .DecodeText(Encoding.UTF8)
            .ReadLines()
            .OnStandardOutput(_ => { })
            .GetAsyncEnumerator(cancellation.Token);
        try
        {
            Assert.True(await events.MoveNextAsync());
            var started = Assert.IsType<ProcessEvent<string>.Started>(events.Current);
            using var process = Process.GetProcessById(started.ProcessId);
            await pipe.WaitForConnectionAsync(cancellation.Token);
            await pipe.ReadExactlyAsync(new byte[1], cancellation.Token);
            Assert.False(process.HasExited);

            if (cancel)
            {
                cancellation.Cancel();
                // No MoveNextAsync or DisposeAsync drives this exit.
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(TestConstants.LongTimeoutDuration));
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.DisposeAsync().AsTask());
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                await events.DisposeAsync();
            }
            Assert.True(process.HasExited);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator());
        }
        finally
        {
            cancellation.Cancel();
            await events.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnvironmentOverlayPreservesArgumentsAndWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        Assert.NotNull(inheritedPath);
        var child = await BuildChildAsync(workspace.Path, """
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("TF_BUILD") + "|" + Environment.GetEnvironmentVariable("PATH") + "|" + Environment.GetEnvironmentVariable("CAPTURE_OVERLAY") + "|" + Environment.CurrentDirectory + "|" + string.Join("|", args)));
            """);
        string[] arguments = ["space value", "quote\"value", "", "日本語"];

        var result = await new DefaultProcessRunner().CreateExecution(new ProcessCommand(DotnetFileAppProcess.ResolvedExecutablePath)
        {
            Arguments = [child, .. arguments],
            WorkingDirectory = workspace.Path,
            EnvironmentVariables = new Dictionary<string, string> { ["CAPTURE_OVERLAY"] = "ü", ["TF_BUILD"] = "overridden" }
        }).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessCaptureResult(0, "overridden|" + inheritedPath + "|ü|" + workspace.Path + "|" + string.Join("|", arguments), ""), result);
        Assert.Equal(inheritedPath, Environment.GetEnvironmentVariable("PATH"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationHonorsRootOrTreeScopeAndSettlesBlockedInput(bool killEntireProcessTree)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var name = "process-tree-" + Guid.NewGuid().ToString("N");
        await using var parentPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var childPipe = new NamedPipeServerStream(name + "-child", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var app = await BuildChildAsync(workspace.Path, """
            if (args.Length == 1)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add(args[0] + "-child");
                start.ArgumentList.Add("child");
                Process.Start(start)!.Dispose();
            }
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(Environment.ProcessId.ToString());
            await pipe.ReadExactlyAsync(new byte[1]);
            """);
        using var cancellation = CreateCancellation();
        var capture = new DefaultProcessRunner().CreateExecution(new ProcessCommand(DotnetFileAppProcess.ResolvedExecutablePath)
        {
            Arguments = [app, name],
            WorkingDirectory = workspace.Path,
            Input = ProcessInput.FromBytes(new byte[2000000]),
            KillEntireProcessTree = killEntireProcessTree
        }).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellation.Token);
        Process? child = null;
        try
        {
            await Task.WhenAll(parentPipe.WaitForConnectionAsync(cancellation.Token), childPipe.WaitForConnectionAsync(cancellation.Token));
            using var parentReader = new StreamReader(parentPipe, leaveOpen: true);
            using var childReader = new StreamReader(childPipe, leaveOpen: true);
            using var parent = Process.GetProcessById(int.Parse((await parentReader.ReadLineAsync(cancellation.Token))!));
            child = Process.GetProcessById(int.Parse((await childReader.ReadLineAsync(cancellation.Token))!));
            Assert.False(parent.HasExited);
            Assert.False(child.HasExited);
            Assert.False(capture.IsCompleted);

            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture).DefaultTimeout(TestConstants.LongTimeoutDuration);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.True(parent.HasExited);
            if (!killEntireProcessTree)
            {
                Assert.False(child.HasExited);
                await childPipe.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
            }
            // A killed Unix descendant can remain a zombie; pipe closure proves it stopped.
            using var eofCancellation = CreateCancellation();
            Assert.Equal("", await childReader.ReadToEndAsync(eofCancellation.Token));
        }
        finally
        {
            await cancellation.CancelAsync();
            if (child is { HasExited: false })
            {
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(TestConstants.LongTimeoutDuration));
            }
            child?.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture).DefaultTimeout(TestConstants.LongTimeoutDuration);
        }
    }

    [Fact]
    public async Task OutputPayloadsRemainValidAfterLaterReadsAndDisposal()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            var bytes = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
            await Console.OpenStandardOutput().WriteAsync(bytes);
            """);
        using var cancellation = CreateCancellation();
        var retained = new List<ReadOnlyMemory<byte>>();
        var lifecycle = new List<Type>();
        await foreach (var item in CreateExecution(child).WithCancellation(cancellation.Token))
        {
            if (item is ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput data) { retained.Add(data.Content); }
            else { lifecycle.Add(item.GetType()); }
        }
        Assert.True(retained.Count > 1);
        Assert.Equal(Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)), retained.SelectMany(memory => memory.ToArray()));
        Assert.Equal(typeof(ProcessEvent<ReadOnlyMemory<byte>>.Started), lifecycle[0]);
        Assert.Equal(typeof(ProcessEvent<ReadOnlyMemory<byte>>.Exited), lifecycle[^1]);
        Assert.Equal(1, lifecycle.Count(type => type == typeof(ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted)));
        Assert.Equal(1, lifecycle.Count(type => type == typeof(ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionCannotBeConsumedAgain(bool complete)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var child = await BuildChildAsync(workspace.Path, """
            await File.AppendAllTextAsync(args[0], "launched\n");
            """);
        var ledger = Path.Combine(workspace.Path, "launches.txt");
        var execution = CreateExecution(child, arguments: [ledger]);
        Assert.False(File.Exists(ledger));

        await using (var enumerator = execution.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.False(File.Exists(ledger));
            if (complete)
            {
                while (await enumerator.MoveNextAsync()) { }
                Assert.Equal("launched\n", await File.ReadAllTextAsync(ledger, TestContext.Current.CancellationToken));
            }
        }

        Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator());
        Assert.Equal(complete, File.Exists(ledger));
    }

    [Fact]
    public async Task ConcurrentAcquisitionsAcceptExactlyOneConsumer()
    {
        var execution = new DefaultProcessRunner().CreateExecution(new ProcessCommand("must-not-launch"));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return (Enumerator: execution.GetAsyncEnumerator(), Failure: (Exception?)null);
            }
            catch (InvalidOperationException exception)
            {
                return (Enumerator: (IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>>?)null, Failure: (Exception?)exception);
            }
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);
        foreach (var result in results)
        {
            if (result.Enumerator is { } enumerator) { await enumerator.DisposeAsync(); }
        }

        Assert.Single(results, result => result.Enumerator is not null);
        Assert.Equal(15, results.Count(result => result.Failure is InvalidOperationException));
        Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAndCanceledInvocationsCannotBeConsumedAgain(bool cancel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "not-an-executable");
        Assert.False(File.Exists(path));
        var events = new DefaultProcessRunner().CreateExecution(new ProcessCommand(path));
        using var cancellation = new CancellationTokenSource();
        if (cancel) { cancellation.Cancel(); }
        await using (var enumeration = events.GetAsyncEnumerator(cancellation.Token))
        {
            if (cancel)
            {
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration.MoveNextAsync().AsTask());
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<Win32Exception>(() => enumeration.MoveNextAsync().AsTask());
            }
        }
        Assert.Throws<InvalidOperationException>(() => events.GetAsyncEnumerator());
    }

    [Fact]
    public async Task FakeExecutionIsDeferredAndDerivedPipelinesShareItsClaim()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: ["first"], error: ["warning"]);
        runner.EnqueueResult(output: ["second"]);
        var command = new ProcessCommand("scripted-command");
        var execution = runner.CreateExecution(command);
        var lines = execution.DecodeText(Encoding.UTF8).ReadLines();
        Assert.Empty(runner.Commands);
        Assert.Empty(runner.Disposables);
        Assert.False(runner.RunStarted.Task.IsCompleted);

        await using (var enumerator = lines.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Empty(runner.Commands);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new ProcessEvent<string>.Started(1), enumerator.Current);
            using var rejectedCancellation = new CancellationTokenSource();
            rejectedCancellation.Cancel();
            Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator(rejectedCancellation.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.DiscardStandardError().ExecuteAsync());

            var remaining = new List<ProcessEvent<string>>();
            while (await enumerator.MoveNextAsync()) { remaining.Add(enumerator.Current); }
            Assert.Equal(
                [
                    new ProcessEvent<string>.StandardOutput("first"),
                    new ProcessEvent<string>.StandardError("warning"),
                    new ProcessEvent<string>.StandardOutputCompleted(),
                    new ProcessEvent<string>.StandardErrorCompleted(),
                    new ProcessEvent<string>.Exited(0)
                ], remaining);
        }
        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);

        var second = await runner.CreateExecution(command).DecodeText(Encoding.UTF8)
            .ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new ProcessCaptureResult(0, "second\n", ""), second);
        Assert.Equal([command, command], runner.Commands);
        Assert.All(runner.Disposables, disposable => Assert.Equal(1, disposable.DisposeCallCount));
    }

    [Fact]
    public async Task ObserversForwardEveryEventUnchangedAndPreserveObserverOrder()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(outputEvents:
        [
            new TestProcessOutput(false, "first"),
            new TestProcessOutput(true, "warning"),
            new TestProcessOutput(false, "last")
        ]);
        var observed = new List<string>();
        var originals = new List<ProcessEvent<string>>();
        var pipeline = runner.CreateExecution(new ProcessCommand("scripted-command"))
            .DecodeText(Encoding.UTF8).ReadLines()
            .Select(item => { originals.Add(item); return item; })
            .OnStarted(id => observed.Add($"started:{id}"))
            .OnStandardOutput(line => observed.Add("output-1:" + line))
            .OnStandardOutput(line => observed.Add("output-2:" + line))
            .OnStandardError(line => observed.Add("error:" + line));
        Assert.Empty(observed);

        var events = await CollectAsync(pipeline);

        Assert.Equal(7, events.Count);
        Assert.Equal(originals.Count, events.Count);
        for (var index = 0; index < events.Count; index++) { Assert.Same(originals[index], events[index]); }
        Assert.Equal(
            ["started:1", "output-1:first", "output-2:first", "error:warning", "output-1:last", "output-2:last"],
            observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardBeforeDecodingRemovesOnlySelectedData(bool discardError)
    {
        var strictUtf8 = new UTF8Encoding(false, true);
        var control = new FailingEventStream([0xFF], isError: discardError);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => control.DecodeText(strictUtf8).ExecuteAsync());

        var source = new FailingEventStream([0xFF], isError: discardError);
        var discarded = discardError ? source.DiscardStandardError() : source.DiscardStandardOutput();
        var events = await CollectAsync(discarded.DecodeText(strictUtf8).ReadLines());

        Assert.Equal(
            [
                new ProcessEvent<string>.Started(1),
                new ProcessEvent<string>.StandardOutputCompleted(),
                new ProcessEvent<string>.StandardErrorCompleted(),
                new ProcessEvent<string>.Exited(0)
            ], events);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(17)]
    public async Task TerminalRequiresKnownExitAfterCleanup(int? exitCode)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode: exitCode, output: ["drain this"]);
        var execution = runner.CreateExecution(new ProcessCommand("scripted-command"));
        Assert.Empty(runner.Disposables);

        if (exitCode is { } known)
        {
            Assert.Equal(known, await execution.ExecuteAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.ExecuteAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalsAwaitDisposalEvenAfterAnExitEvent(bool capture)
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FailingEventStream([(byte)'x'], cleanup: cleanup.Task);
        Assert.Equal(0, source.DisposeCount);
        Task completion = capture
            ? source.DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken)
            : source.ExecuteAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, source.DisposeCount);
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            cleanup.SetResult();
        }
        await completion;
    }

    [Theory]
    [InlineData("started")]
    [InlineData("bytes")]
    [InlineData("text")]
    [InlineData("lines")]
    public async Task ObserverAndCleanupFailuresSurviveComposition(string stage)
    {
        var observerFailure = new InvalidOperationException("Observation failed.");
        var cleanupFailure = new IOException("Disposal failed.");
        var source = new FailingEventStream(Encoding.UTF8.GetBytes("line\n"), cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        var pipeline = stage switch
        {
            "started" => source.OnStarted(_ => throw observerFailure).DecodeText(Encoding.UTF8).ReadLines(),
            "bytes" => source.OnStandardOutput(_ => throw observerFailure).DecodeText(Encoding.UTF8).ReadLines(),
            "text" => source.DecodeText(Encoding.UTF8).OnStandardOutput(_ => throw observerFailure).ReadLines(),
            "lines" => source.DecodeText(Encoding.UTF8).ReadLines().OnStandardOutput(_ => throw observerFailure),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };

        var failure = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Equal([observerFailure, cleanupFailure], failure.Flatten().InnerExceptions);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesObserverAndCleanupFailuresAndPartialDiagnostics(bool capture)
    {
        using var cancellation = new CancellationTokenSource();
        var observerFailure = new IOException("Observation failed while canceled.");
        var cleanupFailure = new InvalidOperationException("Disposal failed.");
        var source = new FailingEventStream([(byte)'x'], cleanupFailure: cleanupFailure);
        var retained = new StringBuilder();
        var pipeline = source.DiscardStandardError().DecodeText(Encoding.UTF8)
            .OnStandardOutput(text =>
            {
                retained.Append(text.Span);
                cancellation.Cancel();
                throw observerFailure;
            });
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, retained.Length);
        Task completion = capture
            ? pipeline.ExecuteAndCaptureTextAsync(cancellation.Token)
            : pipeline.ExecuteAsync(cancellation.Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal([observerFailure, cleanupFailure], Assert.IsType<AggregateException>(failure.InnerException).Flatten().InnerExceptions);
        Assert.Equal("x", retained.ToString());
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\nA\rB\r\n日本語\0last")]
    public async Task LinesDecodeSplitCharactersAndCrLfWithoutDroppingEmptyLines(string text)
    {
        var lines = new List<string>();
        var completionCount = 0;
        await foreach (var item in FragmentedEvents().DecodeText(Encoding.UTF8).ReadLines())
        {
            if (item is ProcessEvent<string>.StandardOutput output) { lines.Add(output.Content); }
            if (item is ProcessEvent<string>.StandardOutputCompleted) { completionCount++; }
        }
        using var expected = new StringReader(text);
        var expectedLines = new List<string>();
        while (expected.ReadLine() is { } line) { expectedLines.Add(line); }
        Assert.Equal(expectedLines, lines);
        Assert.Equal(1, completionCount);

        async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> FragmentedEvents()
        {
            await Task.CompletedTask;
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.Started(1);
            foreach (var value in Encoding.UTF8.GetBytes(text))
            {
                yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(new byte[] { value });
            }
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted();
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted();
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.Exited(0);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DecoderAndCleanupFailuresAreBothPreserved(bool invalidEncoding, bool failedCleanup)
    {
        var cleanupFailure = failedCleanup ? new IOException("Disposal failed.") : null;
        var source = new FailingEventStream([0xFF], cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        var decoded = source.DecodeText(new UTF8Encoding(false, invalidEncoding));
        if (invalidEncoding && failedCleanup)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => CollectAsync(decoded));
            Assert.IsType<DecoderFallbackException>(failure.InnerExceptions[0]);
            Assert.Same(cleanupFailure, failure.InnerExceptions[1]);
        }
        else if (invalidEncoding)
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => CollectAsync(decoded));
        }
        else if (failedCleanup)
        {
            Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => CollectAsync(decoded)));
        }
        else
        {
            var events = await CollectAsync(decoded);
            Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), events[^1]);
        }
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("lines")]
    [InlineData("capture")]
    [InlineData("observer")]
    [InlineData("discard")]
    [InlineData("execute")]
    public async Task UpstreamAndCleanupFailuresSurviveAdapters(string adapter)
    {
        var readFailure = new IOException("Reading failed.");
        var cleanupFailure = new InvalidOperationException("Disposal failed.");
        var source = new FailingEventStream([(byte)'x'], readFailure, cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        Func<Task> read = adapter switch
        {
            "text" => () => CollectAsync(source.DecodeText(Encoding.UTF8)),
            "lines" => () => CollectAsync(source.DecodeText(Encoding.UTF8).ReadLines()),
            "capture" => () => source.DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken),
            "observer" => () => source.DecodeText(Encoding.UTF8).ReadLines().OnStandardOutput(_ => { }).ExecuteAsync(TestContext.Current.CancellationToken),
            "discard" => () => source.DiscardStandardOutput().DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken),
            "execute" => () => source.ExecuteAsync(TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(adapter))
        };

        var failure = await Assert.ThrowsAsync<AggregateException>(read);

        Assert.Equal([readFailure, cleanupFailure], failure.Flatten().InnerExceptions);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsBatchShimPreservesLiteralArguments(bool inheritEnvironment)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows batch commands require cmd.exe.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = Path.Combine(workspace.Path, "literal %TEMP%! with spaces");
        Directory.CreateDirectory(directory);
        await BuildChildAsync(directory, "Console.Write(string.Join('\\0', args));");
        var shim = Path.Combine(directory, "compiler.cmd");
        await File.WriteAllTextAsync(shim, $"@echo off\r\n\"{DotnetFileAppProcess.ResolvedExecutablePath}\" \"%~dp0compiled\\child.dll\" %*\r\n", TestContext.Current.CancellationToken);
        string[] arguments = ["literal %PATH%! & value", "", "a^b|c", "a \"quoted\" value", "quoted \"& text\" remains literal", "backslash\\\"quote", @"C:\tools\trailing\"];
        using var cancellation = CreateCancellation();

        var result = await new DefaultProcessRunner().CreateExecution(new ProcessCommand(shim)
        {
            Arguments = arguments,
            InheritEnvironmentVariables = inheritEnvironment
        }).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellation.Token);

        Assert.True(result.ExitCode == 0, $"Exit code {result.ExitCode}: {result.StandardOutput}{result.StandardError}");
        Assert.Equal(string.Join('\0', arguments), result.StandardOutput);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }
        return result;
    }

    private sealed class FailingEventStream(byte[] content, Exception? readFailure = null, Exception? cleanupFailure = null,
        bool isError = false, Task? cleanup = null)
        : IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>>
    {
        public int DisposeCount { get; private set; }

        public IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(this, content, readFailure, cleanupFailure, isError, cleanup, cancellationToken);

        private sealed class Enumerator(FailingEventStream owner, byte[] content, Exception? readFailure,
            Exception? cleanupFailure, bool isError, Task? cleanup, CancellationToken cancellationToken) : IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>>
        {
            private int _index;
            public ProcessEvent<ReadOnlyMemory<byte>> Current { get; private set; } = new ProcessEvent<ReadOnlyMemory<byte>>.Started(1);

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                Current = _index++ switch
                {
                    0 => new ProcessEvent<ReadOnlyMemory<byte>>.Started(1),
                    1 when isError => new ProcessEvent<ReadOnlyMemory<byte>>.StandardError(content),
                    1 => new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(content),
                    2 when readFailure is not null => throw readFailure,
                    2 => new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted(),
                    3 => new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted(),
                    _ => new ProcessEvent<ReadOnlyMemory<byte>>.Exited(0)
                };
                return ValueTask.FromResult(_index <= 5);
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                if (cleanup is not null) { return new ValueTask(cleanup); }
                return cleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanupFailure);
            }
        }
    }

    private static IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(string child, ProcessInput? input = null,
        params string[] arguments)
        => new DefaultProcessRunner().CreateExecution(new ProcessCommand(DotnetFileAppProcess.ResolvedExecutablePath)
        {
            Arguments = [child, .. arguments],
            Input = input
        });

    private static CancellationTokenSource CreateCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.LongTimeoutDuration);
        return cancellation;
    }

    private static async Task<string> BuildChildAsync(string directory, string body)
    {
        var source = DotnetFileAppProcess.WriteApp(directory, "child.cs", """
            #:sdk Microsoft.NET.Sdk
            #:property TargetFramework=net10.0
            using System.Diagnostics;
            using System.IO.Pipes;
            using System.Text;
            using System.Runtime.InteropServices;
            if (Environment.Version.Major != 10) { throw new Exception("The child must run on .NET 10."); }
            """ + Environment.NewLine + body);
        var output = Path.Combine(directory, "compiled");
        var build = await new DefaultProcessRunner().CreateExecution(new ProcessCommand(DotnetFileAppProcess.ResolvedExecutablePath)
        {
            Arguments = ["build", source, "--output", output, "--nologo", "--verbosity", "quiet"]
        }).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken);
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
        var child = Path.Combine(output, "child.dll");
        Assert.True(File.Exists(child));
        return child;
    }
}
