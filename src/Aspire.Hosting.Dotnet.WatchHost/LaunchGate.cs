// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;

namespace Aspire.Hosting.Dotnet.WatchHost;

/// <summary>
/// Launches a project's replicas one at a time until the project has launched once. The watcher hands a concurrent
/// launch of the same web project its browser refresh server before the first launch has started it, and that launch
/// fails ("Server not started", dotnet/sdk BrowserRefreshServerFactory). Hosting creates the marker file when the
/// watcher reports the project's first <c>process_started</c>; until then each proxy holds a named mutex, derived from
/// the marker path, from its launch request until the marker appears or its connection ends.
/// </summary>
internal sealed class LaunchGate : IDisposable
{
    private readonly string _markerPath;
    private readonly ManualResetEventSlim _released = new();

    private LaunchGate(string markerPath) => _markerPath = markerPath;

    /// <summary>Waits for the project's launch turn; returns <see langword="null"/> once the project has launched.</summary>
    public static async Task<LaunchGate?> EnterAsync(string markerPath, CancellationToken cancellationToken)
    {
        if (File.Exists(markerPath))
        {
            return null;
        }

        var gate = new LaunchGate(markerPath);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A mutex is owned by the thread that acquired it, so one thread holds it for the gate's lifetime. A canceled
        // wait releases the gate up front, so the thread hands the mutex on as soon as it gets it.
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(GetMutexName(markerPath), new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The previous holder exited without releasing; ownership passed to this thread.
            }

            acquired.SetResult();
            gate._released.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "dotnet-watch launch gate"
        };
        owner.Start();

        try
        {
            await acquired.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            gate.Dispose();
            throw;
        }

        if (File.Exists(markerPath))
        {
            gate.Dispose();
            return null;
        }

        return gate;
    }

    /// <summary>Releases the gate once the project has launched or <paramref name="connection"/> completes.</summary>
    public async Task ReleaseAfterLaunchAsync(Task connection)
    {
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(_markerPath)!, Path.GetFileName(_markerPath));
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => created.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (!File.Exists(_markerPath))
        {
            await Task.WhenAny(created.Task, connection).ConfigureAwait(false);
        }

        Dispose();
    }

    public void Dispose() => _released.Set();

    internal static string GetMutexName(string markerPath) =>
        "aspire-watch-" + Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(markerPath)));
}
