// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.Utils;

internal static class ConcurrencyUtils
{
    public static Action Once(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var invoked = 0;

        return () =>
        {
            if (Interlocked.CompareExchange(ref invoked, 1, 0) != 0)
            {
                return;
            }

            action();
        };
    }

    public static void AddRange<T>(this ConcurrentBag<T> bag, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in items)
        {
            bag.Add(item);
        }
    }

    // Allows acquiring a set of SemaphoreSlim instances in a way that allows guaranteed, exception-free release.
    public static async ValueTask<IDisposable> AcquireAllAsync(this IEnumerable<SemaphoreSlim> semaphores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semaphores);

        // Ordering forces consistent acquisition order that helps reduce the risk of deadlocks.
        var distinct = semaphores.Distinct().OrderBy(RuntimeHelpers.GetHashCode).ToArray();
        var acquired = new List<SemaphoreSlim>(distinct.Length);

        try
        {
            foreach (var semaphore in distinct)
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(semaphore);
            }

            return new SemaphoreSetReleaser(acquired);
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                acquired[i].Release();
            }

            throw;
        }
    }

    private sealed class SemaphoreSetReleaser : IDisposable
    {
        private List<SemaphoreSlim>? _semaphores;

        public SemaphoreSetReleaser(List<SemaphoreSlim> semaphores)
        {
            _semaphores = semaphores;
        }

        public void Dispose()
        {
            var semaphores = Interlocked.Exchange(ref _semaphores, null);
            if (semaphores is null)
            {
                return;
            }

            for (var i = semaphores.Count - 1; i >= 0; i--)
            {
                semaphores[i].Release();
            }
        }
    }
}
