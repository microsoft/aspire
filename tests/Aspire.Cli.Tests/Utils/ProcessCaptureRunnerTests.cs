// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Utils;
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

    private sealed class ThreadPoolBacklog(TimeSpan stall) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Stopwatch _stopwatch = new();
        private Thread? _releaser;

        public TimeSpan? ReleasedAfter { get; private set; }

        public void Start(CancellationToken abandoned)
        {
            _stopwatch.Start();
            // Occupy every pool thread, with enough spare blockers for threads injected during the stall.
            var blockers = ThreadPool.ThreadCount + (Environment.ProcessorCount * 4) + 256;
            for (var i = 0; i < blockers; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static release => release.Wait(), _release, preferLocal: false);
            }

            // Release from a dedicated thread: when the runner abandons the capture, or once the stall elapses.
            _releaser = new Thread(() =>
            {
                abandoned.WaitHandle.WaitOne(stall);
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
            ReleasedAfter ??= _stopwatch.Elapsed;
            _release.Set();
        }

        public void Dispose()
        {
            Release();
            _releaser?.Join();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolBacklogCollection
{
    public const string Name = nameof(ThreadPoolBacklogCollection);
}
