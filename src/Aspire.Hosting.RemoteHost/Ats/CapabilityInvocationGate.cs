// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Serializes the synchronous portion of capability invocations for a guest connection.
/// </summary>
/// <remarks>
/// The JSON-RPC server dispatches requests concurrently, so un-awaited guest calls such as
/// <c>addPostgres</c> and <c>addHealthCheck</c> can run at the same time. Exported methods mutate
/// non-thread-safe builder state (for example the service and resource collections), so their
/// synchronous invocations must not overlap. The gate is released when the invocation returns or
/// when it first calls back into the guest, which may re-enter the remote host before completing.
/// </remarks>
internal sealed class CapabilityInvocationGate
{
    [ThreadStatic]
    private static Lease? s_currentLease;

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Runs <paramref name="invocation"/> once no other gated invocation is running.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> invocation)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);

        var lease = new Lease(_semaphore);
        var previousLease = s_currentLease;
        s_currentLease = lease;
        try
        {
            return invocation();
        }
        finally
        {
            s_currentLease = previousLease;
            lease.Release();
        }
    }

    /// <summary>
    /// Releases the gate held by the current thread before invoking a guest callback.
    /// </summary>
    public static void ReleaseForCallback()
    {
        var lease = s_currentLease;
        if (lease is null)
        {
            return;
        }

        s_currentLease = null;
        lease.Release();
    }

    private sealed class Lease(SemaphoreSlim semaphore)
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Release() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
