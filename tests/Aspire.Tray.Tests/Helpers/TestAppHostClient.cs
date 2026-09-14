// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestAppHostClient : IAppHostClient
{
    private readonly Channel<AppHostSnapshot> _snapshots = Channel.CreateUnbounded<AppHostSnapshot>();
    private readonly Channel<AppHostId> _started = Channel.CreateUnbounded<AppHostId>();
    private readonly Channel<AppHostId> _finished = Channel.CreateUnbounded<AppHostId>();
    private readonly ConcurrentDictionary<AppHostId, TaskCompletionSource<StopResult>> _stops = new();
    private readonly Channel<string> _starting = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _finishedStarts = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StartResult>> _starts = new(TrayAppHostPath.Comparer);
    public ConcurrentQueue<AppHostId> Requests { get; } = new();
    public ConcurrentQueue<string> StartRequests { get; } = new();
    public TaskCompletionSource WatchFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Publish(AppHostSnapshot snapshot)
    {
        if (!_snapshots.Writer.TryWrite(snapshot))
        {
            throw new InvalidOperationException("The test discovery stream is closed.");
        }
    }

    public async IAsyncEnumerable<AppHostSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return snapshot;
            }
        }
        finally
        {
            WatchFinished.TrySetResult();
        }
    }

    public async Task<StopResult> StopAsync(AppHostId id, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_stops.TryAdd(id, completion))
        {
            throw new InvalidOperationException("The same instance received concurrent stop commands.");
        }
        Requests.Enqueue(id);
        _started.Writer.TryWrite(id);
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stops.TryRemove(id, out _);
            _finished.Writer.TryWrite(id);
        }
    }

    public void CompleteStop(AppHostId id, StopResult result) => _stops[id].SetResult(result);

    public async Task<StartResult> StartAsync(string appHostPath, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<StartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_starts.TryAdd(appHostPath, completion))
        {
            throw new InvalidOperationException("The same path received concurrent start commands.");
        }
        StartRequests.Enqueue(appHostPath);
        _starting.Writer.TryWrite(appHostPath);
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _starts.TryRemove(appHostPath, out _);
            _finishedStarts.Writer.TryWrite(appHostPath);
        }
    }

    public void CompleteStart(string appHostPath, StartResult result) => _starts[appHostPath].SetResult(result);

    public void FailStart(string appHostPath, Exception exception) => _starts[appHostPath].SetException(exception);

    public Task<string> NextStartAsync()
        => _starting.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public Task<string> NextFinishedStartAsync()
        => _finishedStarts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public async Task PublishAndWaitAsync(TrayController controller, AppHostSnapshot snapshot)
    {
        var previous = controller.State;
        Publish(snapshot);
        await WaitForStateAsync(controller, state => !ReferenceEquals(previous, state)).ConfigureAwait(false);
    }

    public Task<AppHostId> NextStopAsync()
        => _started.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public Task<AppHostId> NextFinishedStopAsync()
        => _finished.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    public static AppHostInfo Host(int pid, long startedAt = 1000) => new(
        Path.GetFullPath("sample/apphost.cs"), pid, "https://localhost:1234/")
    {
        ProcessStartTimeUnixMilliseconds = startedAt
    };

    public static async Task WaitForStateAsync(TrayController controller, Func<TrayViewState, bool> predicate)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (predicate(controller.State))
            {
                ready.TrySetResult();
            }
        }
        controller.Changed += OnChanged;
        try
        {
            OnChanged();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            controller.Changed -= OnChanged;
        }
    }
}
