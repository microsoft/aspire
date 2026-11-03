// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

// The test deliberately saturates the shared thread pool, so it must not run alongside other tests.
[Collection(ThreadPoolBacklogCollection.Name)]
public class ProcessCaptureRunnerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task RunAsync_ThreadPoolBacklogAfterExitDoesNotDiscardCompletedCapture()
    {
        using var backlog = new ThreadPoolBacklog(stall: TimeSpan.FromSeconds(4));
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/d", "/c", "exit 0"])
            : new ProcessStartInfo("/bin/sh", ["-c", "exit 0"]);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        var stopwatch = Stopwatch.StartNew();

        var result = await ProcessCaptureRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(30),
            async (process, cancellationToken) =>
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                // Let the runner's own exit continuation start its post-exit drain first, then
                // leave the capture's last step waiting behind a pool backlog that outlasts the
                // drain bound, as reads do when CI saturates the pool.
                await Task.Yield();
                backlog.Start(cancellationToken);
                await ThreadPoolBacklog.QueueBehindAsync().ConfigureAwait(false);
                return "captured";
            },
            () => "(empty)",
            NullLogger.Instance,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        outputHelper.WriteLine($"Capture returned after {stopwatch.Elapsed}; backlog released after {backlog.ReleasedAfter}.");
        Assert.Null(result.FailureKind);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("captured", result.Capture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ResponsivePoolAllowsPendingCaptureToFinish(bool cancelDuringRecovery)
    {
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/d", "/c", "exit 0"])
            : new ProcessStartInfo("/bin/sh", ["-c", "exit 0"]);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        var logger = new DrainLogger(
            onDrainStart: () => { },
            onProbingBacklog: () => { },
            onPoolResponsive: () =>
            {
                if (cancelDuringRecovery)
                {
                    callerCts.Cancel();
                }
                recoveryStarted.TrySetResult();
            },
            onDrainInterrupted: () => { });

        var result = await ProcessCaptureRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(30),
            async (process, cancellationToken) =>
            {
                // A responsive probe can overtake a capture that still needs several reads
                // to reach EOF. Keep those reads pending until the probe has run.
                await recoveryStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < 3; i++)
                {
                    await ThreadPoolBacklog.QueueBehindAsync().ConfigureAwait(false);
                }
                return "captured";
            },
            () => "(empty)",
            logger,
            callerCts.Token).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Null(result.FailureKind);
        Assert.Equal(cancelDuringRecovery, result.Cancelled);
        Assert.Equal(cancelDuringRecovery ? -1 : 0, result.ExitCode);
        if (!cancelDuringRecovery)
        {
            Assert.Equal("captured", result.Capture);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RunAsync_InterruptedDuringBackloggedDrainReportsInterruption(bool cancelByCaller, bool captureObservesCancellation)
    {
        using var backlog = new ThreadPoolBacklog(stall: TimeSpan.FromSeconds(20));
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/d", "/c", "exit 0"])
            : new ProcessStartInfo("/bin/sh", ["-c", "exit 0"]);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        // The caller cancels once the drain bound has expired and the runner is waiting for the
        // backlogged pool, so the interruption must be observed by that wait (or by the bound's own
        // wait, if the backlog also starves the bound's timer). The deadline fires
        // after the 2s drain bound.
        var interruptAfter = TimeSpan.FromSeconds(3);
        using var callerCancel = cancelByCaller ? new CallerCancellation(callerCts) : null;

        // Start the backlog from the runner's own thread as it enters the post-exit drain. Starting it
        // from the capture could leave the runner's exit continuation queued behind the backlog, which
        // tests the pre-exit wait rather than the drain. Hold it through a caller's cancellation so
        // the drain must observe it without waiting for the pool to recover; the runner's bounded
        // cleanup afterwards is timer-driven, so the backlog is released once the drain reports the
        // interruption. The deadline is the runner's own timer, which a starved pool can delay, so
        // release the backlog once it fires; both paths then observe the same linked token.
        var captureToken = CancellationToken.None;
        var backlogStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new DrainLogger(
            onDrainStart: () =>
            {
                backlog.Start(cancelByCaller ? CancellationToken.None : captureToken);
                callerCancel?.Arm();
                backlogStarted.TrySetResult();
            },
            onProbingBacklog: () => callerCancel?.Trigger(),
            onPoolResponsive: () => { },
            onDrainInterrupted: backlog.ObserveInterruption);

        var runTask = ProcessCaptureRunner.RunAsync(
            startInfo,
            cancelByCaller ? TimeSpan.FromSeconds(30) : interruptAfter,
            (process, cancellationToken) =>
            {
                captureToken = cancellationToken;
                return CaptureAsync(cancellationToken);
            },
            () => "(empty)",
            logger,
            callerCts.Token);

        async Task<string> CaptureAsync(CancellationToken cancellationToken)
        {
            if (!captureObservesCancellation)
            {
                // A capture that ignores cancellation resumes behind the backlog, so only a drain
                // that observes the interruption itself can return before the pool recovers.
                await backlogStarted.Task.ConfigureAwait(false);
                await ThreadPoolBacklog.QueueBehindAsync().ConfigureAwait(false);
                return "late";
            }

            // Like a reader that returns what it buffered once cancelled, only the interruption
            // ends this capture, so the result must not be reported as a successful run.
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return "partial";
        }

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        outputHelper.WriteLine($"Drain reported the interruption after {backlog.InterruptedAfter}; backlog released after {backlog.ReleasedAfter}.");
        if (cancelByCaller)
        {
            Assert.True(backlog.InterruptedWhileHeld, $"The drain reported the interruption after {backlog.InterruptedAfter}, only once the backlog was released after {backlog.ReleasedAfter}.");
        }
        Assert.Equal(cancelByCaller, result.Cancelled);
        Assert.Equal(cancelByCaller ? null : ProcessCaptureFailureKind.TimedOut, result.FailureKind);
        Assert.Equal(-1, result.ExitCode);
    }

    // Runs the callbacks inline on the runner's thread, so observing the drain needs no pool worker.
    private sealed class DrainLogger(Action onDrainStart, Action onProbingBacklog, Action onPoolResponsive, Action onDrainInterrupted) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("exited; draining its output", StringComparison.Ordinal))
            {
                onDrainStart();
            }
            else if (message.Contains("checking the thread pool for a backlog", StringComparison.Ordinal))
            {
                onProbingBacklog();
            }
            else if (message.Contains("Thread pool is responsive", StringComparison.Ordinal))
            {
                onPoolResponsive();
            }
            else if (message.Contains("was interrupted", StringComparison.Ordinal))
            {
                onDrainInterrupted();
            }
        }
    }

    // Cancels from a dedicated thread once triggered, like a caller pressing Ctrl+C, without
    // depending on timers or pool threads that the backlog starves. The drain bound's own timer
    // needs a pool thread to fire, so if the backlog starves it the probe is never reached; once
    // armed, the thread then cancels after a wall-clock fallback, which lands in the first drain
    // wait and must be observed just as promptly.
    private sealed class CallerCancellation : IDisposable
    {
        private static readonly TimeSpan s_fallback = TimeSpan.FromSeconds(5);

        private readonly ManualResetEventSlim _armed = new();
        private readonly ManualResetEventSlim _trigger = new();
        private volatile bool _stopped;
        private readonly Thread _thread;

        public CallerCancellation(CancellationTokenSource cts)
        {
            _thread = new Thread(() =>
            {
                _armed.Wait();
                _trigger.Wait(s_fallback);
                if (!_stopped)
                {
                    cts.Cancel();
                }
            })
            { IsBackground = true, Name = nameof(CallerCancellation) };
            _thread.Start();
        }

        public void Arm() => _armed.Set();

        public void Trigger()
        {
            _trigger.Set();
            _armed.Set();
        }

        public void Dispose()
        {
            _stopped = true;
            _trigger.Set();
            _armed.Set();
            // Cancel() can run the runner, and the test awaiting it, inline on this thread.
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join();
            }
        }
    }

    private sealed class ThreadPoolBacklog(TimeSpan stall) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly ManualResetEventSlim _done = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly Lock _lock = new();
        private Thread? _releaser;

        public TimeSpan? ReleasedAfter { get; private set; }

        public TimeSpan? InterruptedAfter { get; private set; }

        public bool InterruptedWhileHeld { get; private set; }

        // Called inline by the runner's thread: records whether the pool was still held, then lets it go.
        public void ObserveInterruption()
        {
            lock (_lock)
            {
                InterruptedAfter = _stopwatch.Elapsed;
                InterruptedWhileHeld = _stopwatch.IsRunning && ReleasedAfter is null;
            }
            _done.Set();
        }

        public void Start(CancellationToken abandoned)
        {
            _stopwatch.Start();
            // Occupy every pool thread, with enough spare blockers for threads injected during the stall.
            var blockers = ThreadPool.ThreadCount + (Environment.ProcessorCount * 4) + 256;
            for (var i = 0; i < blockers; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static release => release.Wait(), _release, preferLocal: false);
            }

            // Release from a dedicated thread: when the runner abandons the capture or reports the interruption, or once the stall elapses.
            _releaser = new Thread(() =>
            {
                WaitHandle.WaitAny([abandoned.WaitHandle, _done.WaitHandle], stall);
                Release();
            })
            { IsBackground = true, Name = nameof(ThreadPoolBacklog) };
            _releaser.Start();
        }

        public static Task QueueBehindAsync()
        {
            var ran = new TaskCompletionSource();
            ThreadPool.UnsafeQueueUserWorkItem(static ran => ran.TrySetResult(), ran, preferLocal: false);
            return ran.Task;
        }

        private void Release()
        {
            lock (_lock)
            {
                ReleasedAfter ??= _stopwatch.Elapsed;
            }
            _release.Set();
        }

        public void Dispose()
        {
            _done.Set();
            _releaser?.Join();
            Release();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolBacklogCollection
{
    public const string Name = nameof(ThreadPoolBacklogCollection);
}
