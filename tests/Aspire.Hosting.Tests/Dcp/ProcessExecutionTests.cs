// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class ProcessExecutionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("")]
    [InlineData("NUL\0ü日本語\r\n\nno final newline")]
    public async Task InputIsOwnedAndCapturePreservesExactText(string text)
    {
        using var child = CreateChild(static async () =>
        {
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            var bytes = input.ToArray();
            await Console.OpenStandardOutput().WriteAsync(bytes);
            await Console.OpenStandardError().WriteAsync(bytes);
            return 0;
        });
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
        using var child = CreateChild(static async (string firstIsError) =>
        {
            var output = Console.OpenStandardOutput();
            var error = Console.OpenStandardError();
            var prefix = Encoding.UTF8.GetBytes(new string('ü', 200000));
            var first = firstIsError == "True" ? error : output;
            var second = firstIsError == "True" ? output : error;
            await Task.WhenAll(first.WriteAsync(prefix).AsTask(), second.WriteAsync(prefix).AsTask());
            using var input = new MemoryStream();
            await Console.OpenStandardInput().CopyToAsync(input);
            await Task.WhenAll(output.WriteAsync(input.ToArray()).AsTask(), error.WriteAsync(input.ToArray()).AsTask());
            return 0;
        });
        var input = new string('日', 200000);
        Assert.True(Encoding.UTF8.GetByteCount(input) > 400000);
        using var cancellation = CreateCancellation();

        var result = await CreateExecution(child, ProcessInput.FromText(input, Encoding.UTF8), errorFirst.ToString()).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellation.Token);

        Assert.Equal(new ProcessCaptureResult(0, new string('ü', 200000) + input, new string('ü', 200000) + input), result);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(0)]
    public async Task UndeliveredInputPreservesNonzeroDiagnosticsButIsNotSuccessfulDelivery(int exitCode)
    {
        using var child = CreateChild(static async (string requestedExitCode) =>
        {
            // Leave stdin unread so the process exits with the large input still pending.
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("output\0no newline"));
            await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes(new string('E', 220000) + "late ü\0\r\n"));
            return int.Parse(requestedExitCode);
        });
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
    public async Task CancellationOrDisposalStopsChildWithoutReadingAnotherEvent(bool cancel)
    {
        using var child = CreateChild(static async (string pipeName) =>
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            await pipe.WriteAsync(new byte[] { 1 });
            await Console.OpenStandardOutput().WriteAsync(new byte[2000000]);
            await pipe.ReadExactlyAsync(new byte[1]);
            return 0;
        });
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
        using var child = CreateChild(static async (string first, string second, string third, string fourth) =>
        {
            var text = Environment.GetEnvironmentVariable("TF_BUILD") + "|" + Environment.GetEnvironmentVariable("PATH") + "|" + Environment.GetEnvironmentVariable("CAPTURE_OVERLAY") + "|" + Environment.CurrentDirectory + "|" + string.Join("|", [first, second, third, fourth]);
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(text));
            return 0;
        });
        string[] arguments = ["space value", "quote\"value", "", "日本語"];

        var result = await new DefaultProcessRunner().CreateExecution(new ProcessCommand(child.StartInfo.FileName)
        {
            Arguments = [.. child.Arguments, .. arguments],
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
        // Keep both invocations' diagnostic files owned by the test process, even
        // when the runner kills the root before it can perform managed cleanup.
        using var descendant = CreateChild(static async (string childPipeName) =>
        {
            await ReportPidAndWaitAsync(childPipeName);
            return 0;
        });
        using var app = CreateChild(static async (string executablePath, string preparedArguments, string parentPipeName) =>
        {
            var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
            foreach (var argument in CommandLineArgsParser.Parse(preparedArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.ArgumentList.Add(parentPipeName + "-child");
            Process.Start(startInfo)!.Dispose();
            await ReportPidAndWaitAsync(parentPipeName);
            return 0;
        });
        using var cancellation = CreateCancellation();
        var capture = new DefaultProcessRunner().CreateExecution(new ProcessCommand(app.StartInfo.FileName)
        {
            Arguments = [.. app.Arguments, descendant.StartInfo.FileName, descendant.StartInfo.Arguments, name],
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
        using var child = CreateChild(static async () =>
        {
            var bytes = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
            await Console.OpenStandardOutput().WriteAsync(bytes);
            return 0;
        });
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
        using var child = CreateChild(static async (string ledgerPath) =>
        {
            await File.AppendAllTextAsync(ledgerPath, "launched\n");
            return 0;
        });
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
        using var child = CreateChild(static async (string first, string second, string third, string fourth, string fifth, string sixth, string seventh) =>
        {
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(string.Join('\0', [first, second, third, fourth, fifth, sixth, seventh])));
            return 0;
        });
        var shim = Path.Combine(directory, "compiler.cmd");
        // RemoteExecutor's compiler-generated names include '<' and '>'. Quote the
        // known invocation prefix so cmd treats those names as arguments, not redirection.
        var prefix = string.Join(" ", child.Arguments.Select(argument => $"\"{argument}\""));
        await File.WriteAllTextAsync(shim, $"@echo off\r\n\"{child.StartInfo.FileName}\" {prefix} %*\r\n", TestContext.Current.CancellationToken);
        string[] arguments = ["literal %PATH%! & value", "", "a^b|c", "a \"quoted\" value", "quoted \"& text\" remains literal", "backslash\\\"quote", @"C:\tools\trailing\"];
        using var cancellation = CreateCancellation();

        var result = await new DefaultProcessRunner().CreateExecution(new ProcessCommand(shim)
        {
            Arguments = arguments,
            InheritEnvironmentVariables = inheritEnvironment,
            // Loading the test assembly initializes Verify, which needs writable temp storage.
            EnvironmentVariables = new Dictionary<string, string> { ["TEMP"] = workspace.Path, ["TMP"] = workspace.Path }
        }).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellation.Token);

        Assert.True(result.ExitCode == 0, $"Exit code {result.ExitCode}: {result.StandardOutput}{result.StandardError}");
        Assert.Equal(string.Join('\0', arguments), result.StandardOutput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("space value")]
    [InlineData("quote\"value")]
    [InlineData("backslash\\\"quote")]
    [InlineData("space and trailing\\")]
    [InlineData("日本語")]
    [InlineData("literal %PATH%! & value")]
    [InlineData("tab\tvalue")]
    public async Task RemoteExecutorArgumentsRoundTripThroughRunner(string argument)
    {
        using var child = new RemoteChild(RemoteExecutor.Invoke(static async (string value) =>
        {
            Assert.Equal(10, Environment.Version.Major);
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(value));
            return 0;
        }, argument, new RemoteInvokeOptions { Start = false }));

        var result = await CreateExecution(child).DecodeText(Encoding.UTF8)
            .ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ProcessCaptureResult(0, argument, ""), result);
    }

    [Theory]
    [InlineData("Child exception:\nSystem.IO.IOException: test child failed.")]
    [InlineData("Child exception:\r\nSystem.InvalidOperationException: 日本語\r\n")]
    public async Task RemoteChildDisposalReportsFailureAndDeletesExceptionFile(string diagnostic)
    {
        var invocation = RemoteExecutor.InvokeRaw((Action)(static () => { }), string.Empty,
            new RemoteInvokeOptions { Start = false });
        var exceptionFile = invocation.Options.ExceptionFile;
        using var child = new RemoteChild(invocation);
        Assert.False(File.Exists(exceptionFile));
        await File.WriteAllTextAsync(exceptionFile, diagnostic, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(exceptionFile));

        var failure = Assert.Throws<RemoteExecutionException>(child.Dispose);

        Assert.Equal("Remote child failed.", failure.Message);
        Assert.Equal(diagnostic, failure.StackTrace);
        Assert.False(File.Exists(exceptionFile));
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

    private static RemoteChild CreateChild(Delegate entryPoint)
        => new(RemoteExecutor.InvokeRaw(entryPoint, string.Empty, new RemoteInvokeOptions { Start = false }));

    private static IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(RemoteChild child, ProcessInput? input = null,
        params string[] arguments)
        => new DefaultProcessRunner().CreateExecution(new ProcessCommand(child.StartInfo.FileName)
        {
            Arguments = [.. child.Arguments, .. arguments],
            Input = input
        });

    private static async Task ReportPidAndWaitAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync();
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(Environment.ProcessId.ToString());
        await pipe.ReadExactlyAsync(new byte[1]);
    }

    private static CancellationTokenSource CreateCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TestConstants.LongTimeoutDuration);
        return cancellation;
    }

    private sealed class RemoteChild : IDisposable
    {
        private readonly RemoteInvokeHandle _invocation;

        public RemoteChild(RemoteInvokeHandle invocation)
        {
            _invocation = invocation;
            StartInfo = invocation.Process.StartInfo;
            // RemoteExecutor supplies launch metadata only. Its unused Process must never
            // start or terminate the actual child, whose lifetime belongs to our runner.
            invocation.Process.Dispose();
            invocation.Process = null;
        }

        public ProcessStartInfo StartInfo { get; }

        // For example, --runtimeconfig "C:\test files\tests.runtimeconfig.json".
        public IReadOnlyList<string> Arguments => CommandLineArgsParser.Parse(StartInfo.Arguments);

        public void Dispose()
        {
            using (_invocation)
            {
                try
                {
                    if (File.Exists(_invocation.Options.ExceptionFile))
                    {
                        throw new RemoteExecutionException("Remote child failed.", File.ReadAllText(_invocation.Options.ExceptionFile));
                    }
                }
                finally
                {
                    File.Delete(_invocation.Options.ExceptionFile);
                }
            }
        }
    }
}
