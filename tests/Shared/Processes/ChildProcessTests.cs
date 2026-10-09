// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Aspire.TestUtilities;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Shared.Tests;

[RequiresTools(["dotnet"])]
[Collection(ProcessTestCollection.Name)]
public class ChildProcessTests(ProcessTestFixture fixture)
{
    [Fact]
    public async Task StartAsync_CancellationAndDisposalDoNotLaunch()
    {
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "0"), new ChildProcessOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.StartAsync(cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => process.ProcessId);
        await process.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => process.StartAsync(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => process.ProcessId);
    }

    [Fact]
    public async Task WaitForExitAsync_ThrowingCallbackStillDrainsBothStreams()
    {
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "23"),
            new ChildProcessOptions
            {
                TimeProvider = new FakeTimeProvider(),
                StandardOutputCallback = line =>
                {
                    stdout.Enqueue(line);
                    if (line == "stdout:0")
                    {
                        throw new InvalidOperationException("Test callback failure.");
                    }
                },
                StandardErrorCallback = stderr.Enqueue
            });
        await process.StartAsync(TestContext.Current.CancellationToken);

        var exitCode = await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(23, exitCode);
        Assert.Equal([$"runtime:{ProcessTestFixture.RuntimeMajor}", "stdin:0", .. Enumerable.Range(0, 256).Select(i => $"stdout:{i}")], stdout.ToArray());
        Assert.Equal(Enumerable.Range(0, 256).Select(i => $"stderr:{i}"), stderr);
    }

    [Fact]
    public async Task WaitForExitAsync_DrainsBufferedTailAfterLongIdlePeriod()
    {
        var clock = new FakeTimeProvider();
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "0"),
            new ChildProcessOptions
            {
                TimeProvider = clock,
                StandardOutputCallback = line =>
                {
                    if (line.StartsWith("runtime:", StringComparison.Ordinal))
                    {
                        firstLine.TrySetResult();
                        releaseConsumer.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    }
                    stdout.Enqueue(line);
                },
                StandardErrorCallback = stderr.Enqueue
            });
        await process.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await firstLine.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await process.WaitForRootExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            // Exceed the five-second idle window before observing exit. Exit must reset that
            // budget, and completing the reader must wake it without advancing the clock again.
            clock.Advance(TimeSpan.FromSeconds(10));
            var exit = process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.False(exit.IsCompleted);
            releaseConsumer.TrySetResult();
            Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal([$"runtime:{ProcessTestFixture.RuntimeMajor}", "stdin:0", .. Enumerable.Range(0, 256).Select(i => $"stdout:{i}")], stdout.ToArray());
            Assert.Equal(Enumerable.Range(0, 256).Select(i => $"stderr:{i}"), stderr);
        }
        finally
        {
            releaseConsumer.TrySetResult();
        }
    }

    [Fact]
    public async Task StartAsync_RejectsSecondLaunch()
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name), new ChildProcessOptions());
        await process.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => process.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForExitAsync_CancellationTerminatesStartedProcess()
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name), new ChildProcessOptions());
        await process.StartAsync(TestContext.Current.CancellationToken);
        var identity = await readiness.ReadRuntimeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            process.WaitForExitAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));

        await ProcessTestFixture.AssertExitedAsync(identity);
    }

    [Fact]
    public async Task DisposeAsync_TerminatesStartedProcessWithoutAnExitWait()
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name), new ChildProcessOptions());
        await process.StartAsync(TestContext.Current.CancellationToken);
        var identity = await readiness.ReadRuntimeAsync();
        using var observedProcess = Process.GetProcessById(identity.ProcessId);

        await process.DisposeAsync();

        await observedProcess.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(observedProcess.HasExited);
        await process.DisposeAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task OwnedTree_CommandExitPreservesStatusAndReapsOrphanedWorkers(int exitCode)
    {
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(
            fixture.CreateStartInfo("tree-exit", readiness.Name, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new ChildProcessOptions
            {
                Lifetime = ChildProcessLifetime.OwnedTree,
                CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo
            });
        ProcessTestIdentity[] identities = [];
        try
        {
            await process.StartAsync(TestContext.Current.CancellationToken);
            identities = await readiness.ReadTreeAsync();

            Assert.Equal(exitCode, await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30)));
            await process.DisposeAsync();
            await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
        }
        finally
        {
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedTree_ShutdownReapsWorkersWhenRootExitsBeforeThem(bool dispose)
    {
        using var readiness = new ProcessTestReadiness();
        ProcessTestIdentity[] identities = [];
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("tree", readiness.Name),
            new ChildProcessOptions
            {
                Lifetime = ChildProcessLifetime.OwnedTree,
                CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo,
                BeginGracefulShutdown = () => CancellationToken.None,
                RequestGracefulShutdownAsync = async (pid, token) =>
                {
                    Assert.NotEqual(pid, identities[0].ProcessId);
                    using var root = Process.GetProcessById(identities[0].ProcessId);
                    root.Kill(entireProcessTree: false);
                    await root.WaitForExitAsync(token);
                }
            });
        try
        {
            await process.StartAsync(TestContext.Current.CancellationToken);
            identities = await readiness.ReadTreeAsync();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            if (dispose)
            {
                await process.DisposeAsync();
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitForExitAsync(cancellation.Token));
            }
            await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
        }
        finally
        {
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }

    [Fact]
    public async Task OwnedTree_ForwardsGracefulSignalAndReapsWorkersAfterRuntimeExit()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "POSIX signals require Unix.");
        using var readiness = new ProcessTestReadiness();
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("tree-graceful", readiness.Name),
            new ChildProcessOptions
            {
                Lifetime = ChildProcessLifetime.OwnedTree,
                CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo
            });
        ProcessTestIdentity[] identities = [];
        try
        {
            await process.StartAsync(TestContext.Current.CancellationToken);
            identities = await readiness.ReadTreeAsync();

            ProcessTestFixture.RequestGracefulShutdown(process.ProcessId);

            Assert.Equal(23, await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30)));
            await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
        }
        finally
        {
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Shutdown_UsesSharedGracefulBudgetAndEscalates(bool dispose, bool signalFailure, bool ownedTree)
    {
        using var readiness = new ProcessTestReadiness();
        using var gracefulBudget = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new TestSink();
        var signalCount = 0;
        var budgetCount = 0;
        var signaledPid = 0;
        var signalToken = CancellationToken.None;
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo(ownedTree ? "tree" : "wait", readiness.Name),
            new TestLogger("shared shutdown", sink, enabled: true),
            new ChildProcessOptions
            {
                Lifetime = ownedTree ? ChildProcessLifetime.OwnedTree : ChildProcessLifetime.CallerManaged,
                CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo,
                BeginGracefulShutdown = () =>
                {
                    Interlocked.Increment(ref budgetCount);
                    return gracefulBudget.Token;
                },
                RequestGracefulShutdownAsync = (pid, token) =>
                {
                    Interlocked.Increment(ref signalCount);
                    signaledPid = pid;
                    signalToken = token;
                    signaled.TrySetResult();
                    return signalFailure
                        ? Task.FromException(new InvalidOperationException("test signal failure"))
                        : Task.CompletedTask;
                }
            });
        await process.StartAsync(TestContext.Current.CancellationToken);
        ProcessTestIdentity[] identities = ownedTree ? await readiness.ReadTreeAsync() : [await readiness.ReadRuntimeAsync()];
        cancellation.Cancel();
        var shutdown = dispose
            ? process.DisposeAsync().AsTask()
            : Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitForExitAsync(cancellation.Token));
        try
        {
            await signaled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, budgetCount);
            Assert.Equal(1, signalCount);
            Assert.Equal(process.ProcessId, signaledPid);
            Assert.Equal(gracefulBudget.Token, signalToken);
        }
        finally
        {
            gracefulBudget.Cancel();
        }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
        if (signalFailure)
        {
            var entry = Assert.Single(sink.Writes, entry => entry.Exception?.Message == "test signal failure");
            Assert.Equal(LogLevel.Warning, entry.LogLevel);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Windows_GracefulTimeoutPreservesAppHostCleanupWorkerButReapsOwnedTrees(bool dispose, bool ownedTree)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DCP-safe AppHost escalation is Windows-specific.");
        using var readiness = new ProcessTestReadiness();
        using var gracefulBudget = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();
        using var cleanup = new NamedPipeServerStream($"{readiness.Name}-cleanup", PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var cleanupPath = Path.Combine(fixture.CreateDirectory().FullName, "cleanup-completed");
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("tree-cleanup", readiness.Name, cleanupPath),
            new ChildProcessOptions
            {
                Lifetime = ownedTree ? ChildProcessLifetime.OwnedTree : ChildProcessLifetime.AppHost,
                KillEntireProcessTreeOnCancel = false,
                CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo,
                BeginGracefulShutdown = () => gracefulBudget.Token,
                RequestGracefulShutdownAsync = (_, _) =>
                {
                    signaled.TrySetResult();
                    return Task.CompletedTask;
                }
            });
        ProcessTestIdentity[] identities = [];
        try
        {
            await process.StartAsync(TestContext.Current.CancellationToken);
            identities = await readiness.ReadTreeAsync();
            cancellation.Cancel();
            var shutdown = dispose
                ? process.DisposeAsync().AsTask()
                : Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitForExitAsync(cancellation.Token));
            await signaled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            gracefulBudget.Cancel();

            await shutdown.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await ProcessTestFixture.AssertExitedAsync(identities[0]);
            if (ownedTree)
            {
                await ProcessTestFixture.AssertExitedAsync(identities[1]);
            }
            else
            {
                using var worker = Process.GetProcessById(identities[1].ProcessId);
                Assert.False(worker.HasExited);
                await cleanup.WaitForConnectionAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
                using var writer = new StreamWriter(cleanup, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("cleanup");
                await worker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(0, worker.ExitCode);
                Assert.Equal("completed", await File.ReadAllTextAsync(cleanupPath, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            gracefulBudget.Cancel();
            await process.DisposeAsync();
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }

    [Fact]
    public async Task WaitForExitAsync_PreservesCancellationAndShutdownFailure()
    {
        using var readiness = new ProcessTestReadiness();
        using var cancellation = new CancellationTokenSource();
        var shutdownFailure = new InvalidOperationException("test graceful window failure");
        var sink = new TestSink();
        var budgetCount = 0;
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("wait", readiness.Name),
            new TestLogger("failed shutdown", sink, enabled: true),
            new ChildProcessOptions
            {
                BeginGracefulShutdown = () => Interlocked.Increment(ref budgetCount) == 1
                    ? throw shutdownFailure
                    : null,
                RequestGracefulShutdownAsync = (_, _) => Task.CompletedTask
            });
        await process.StartAsync(TestContext.Current.CancellationToken);
        var identity = await readiness.ReadRuntimeAsync();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<AggregateException>(() => process.WaitForExitAsync(cancellation.Token));

        Assert.Collection(exception.InnerExceptions,
            failure => Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken),
            failure => Assert.Same(shutdownFailure, failure));
        var entry = Assert.Single(sink.Writes, entry => ReferenceEquals(entry.Exception, shutdownFailure));
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        await process.DisposeAsync();
        await ProcessTestFixture.AssertExitedAsync(identity);
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task StartAsync_DetachedExecutionUsesNullStandardHandles()
    {
        var startInfo = fixture.CreateStartInfo("output", "0");
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        var forwardedLines = new ConcurrentQueue<string>();
        await using var process = ProcessTestFixture.CreateProcess(startInfo, new ChildProcessOptions
        {
            Detached = true,
            StandardOutputCallback = forwardedLines.Enqueue,
            StandardErrorCallback = forwardedLines.Enqueue
        });

        await process.StartAsync(TestContext.Current.CancellationToken);
        var exitCode = await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, exitCode);
        Assert.Empty(forwardedLines);
    }
#else
    [Fact]
    public async Task StartAsync_DetachedExecutionIsExplicitlyUnsupported()
    {
        await using var process = ProcessTestFixture.CreateProcess(fixture.CreateStartInfo("output", "0"),
            new ChildProcessOptions { Detached = true });

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => process.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Detached process execution requires .NET 11.", exception.Message);
        Assert.Throws<InvalidOperationException>(() => process.ProcessId);
    }
#endif
}
