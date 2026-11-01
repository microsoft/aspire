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

        // The caller's cancellation or the deadline fires after the 2s drain bound, while the
        // backlogged pool keeps the runner extending the drain.
        var interruptAfter = TimeSpan.FromSeconds(3);
        using var callerCancel = cancelByCaller ? new CallerCancellation(callerCts, interruptAfter) : null;

        // Start the backlog from the runner's own thread as it enters the post-exit drain. Starting it
        // from the capture could leave the runner's exit continuation queued behind the backlog, which
        // tests the pre-exit wait rather than the drain. Hold it through a caller's cancellation so
        // the runner must report it without waiting for the pool to recover. The deadline is the
        // runner's own timer, which a starved pool can delay, so release the backlog once it fires;
        // both paths then observe the same linked token.
        var captureToken = CancellationToken.None;
        var backlogStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new DrainStartLogger(() =>
        {
            backlog.Start(cancelByCaller ? CancellationToken.None : captureToken);
            backlogStarted.TrySetResult();
        });

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

        // Record completion inline on the completing thread; observing it must not need a pool worker.
        _ = runTask.ContinueWith(
            static (_, state) => ((ThreadPoolBacklog)state!).ObserveCompletion(),
            backlog,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        outputHelper.WriteLine($"Runner completed after {backlog.CompletedAfter}; backlog released after {backlog.ReleasedAfter}.");
        if (cancelByCaller)
        {
            Assert.True(backlog.CompletedWhileHeld, $"The runner completed after {backlog.CompletedAfter}, only once the backlog was released after {backlog.ReleasedAfter}.");
        }
        Assert.Equal(cancelByCaller, result.Cancelled);
        Assert.Equal(cancelByCaller ? null : ProcessCaptureFailureKind.TimedOut, result.FailureKind);
        Assert.Equal(-1, result.ExitCode);
    }

    private sealed class DrainStartLogger(Action onDrainStart) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("exited; draining its output", StringComparison.Ordinal))
            {
                onDrainStart();
            }
        }
    }

    // Cancels from a dedicated thread: timer callbacks can themselves queue behind a starved pool,
    // which would delay the interruption rather than test how the runner handles it.
    private sealed class CallerCancellation : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;

        public CallerCancellation(CancellationTokenSource cts, TimeSpan after)
        {
            _thread = new Thread(() =>
            {
                if (!_stop.Wait(after))
                {
                    cts.Cancel();
                }
            })
            { IsBackground = true, Name = nameof(CallerCancellation) };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
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

        public TimeSpan? CompletedAfter { get; private set; }

        public bool CompletedWhileHeld { get; private set; }

        // Called inline by the completing thread: records whether the pool was still held, then lets it go.
        public void ObserveCompletion()
        {
            lock (_lock)
            {
                CompletedAfter = _stopwatch.Elapsed;
                CompletedWhileHeld = _stopwatch.IsRunning && ReleasedAfter is null;
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

            // Release from a dedicated thread: when the runner abandons the capture or completes, or once the stall elapses.
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
