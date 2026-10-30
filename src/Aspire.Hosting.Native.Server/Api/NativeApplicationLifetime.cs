// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Api;

/// <summary>Allows a guest to retire its application before bounded server shutdown.</summary>
internal sealed class NativeApplicationLifetime : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runs;
    private Task? _stopping;
    private bool _closed;

    public ApplicationRunLifetime CreateRun()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_stopping is not null)
            {
                throw new InvalidOperationException("Application shutdown has started.");
            }
            _runs++;

            return new(this, _requested.Task);
        }
    }

    public Task RequestStopAsync(TimeSpan timeout, TimeProvider clock, Action reportTimeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }
            _requested.TrySetResult();
            if (_runs == 0)
            {
                _released.TrySetResult();
            }

            return _stopping ??= WaitForReleaseAsync(timeout, clock, reportTimeout, cancellationToken);
        }
    }

    private async Task WaitForReleaseAsync(TimeSpan timeout, TimeProvider clock, Action reportTimeout, CancellationToken cancellationToken)
    {
        try
        {
            await _released.Task.WaitAsync(timeout, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            reportTimeout();
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            if (--_runs == 0 && _stopping is not null)
            {
                _released.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _requested.TrySetCanceled();
            _released.TrySetResult();
        }
    }
}

/// <summary>Keeps application transport alive until the owning guest finishes cleanup and disconnects.</summary>
[AspireExport]
internal sealed class ApplicationRunLifetime(NativeApplicationLifetime owner, Task requested) : ICapabilityOwner, ICapabilityLifetime
{
    private int _closed;
    private readonly CancellationTokenSource _connection = new();
    public bool IsRevoked => Volatile.Read(ref _closed) != 0;

    /// <summary>Waits for the host to request ordered application shutdown.</summary>
    [AspireExport]
    public Task WaitForStop() => requested.WaitAsync(_connection.Token);

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _connection.Cancel();
            _connection.Dispose();
            owner.Release();
        }
    }
}
