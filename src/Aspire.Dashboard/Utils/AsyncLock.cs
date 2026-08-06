// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Utils;

/// <summary>
/// Provides mutual exclusion with asynchronous lock acquisition.
/// </summary>
internal sealed class AsyncLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Acquires the lock synchronously.
    /// </summary>
    /// <returns>A handle that releases the lock when disposed.</returns>
    public IDisposable Lock()
    {
        _semaphore.Wait();
        return new Releaser(_semaphore);
    }

    /// <summary>
    /// Acquires the lock asynchronously.
    /// </summary>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A handle that releases the lock when disposed.</returns>
    public async ValueTask<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}