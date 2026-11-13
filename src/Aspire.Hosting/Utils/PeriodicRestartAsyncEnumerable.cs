// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Aspire.Hosting.Utils;

internal static class PeriodicRestartAsyncEnumerable
{
    /// <summary>
    /// Creates an async enumerable that wraps and periodically restarts an inner async enumeration. This is intended to keep long
    /// running watch enumerations fresh and will recreate the watch enumeration on a set interval or if the inner enumerable terminates
    /// unexpectedly. The goal is to ensure that we keep the wrapped enumeration active until the main token is cancelled.
    /// </summary>
    /// <typeparam name="T">The type the enumerable iterates over</typeparam>
    /// <param name="enumerableFactory">Factory method that takes the last iterrated value (if one exists) and a <see cref="CancellationToken"/> and returns a fresh <see cref="IAsyncEnumerable{T}"/> to enumerate over</param>
    /// <param name="restartInterval">How often should we get a new enumerable from the factory</param>
    /// <param name="cancellationToken">Stop all enumeration once this is cancelled</param>
    /// <returns>An <see cref="IAsyncEnumerable{T}"/> of items returned by the inner iterables</returns>
    public static async IAsyncEnumerable<T> CreateAsync<T>(Func<T?, CancellationToken, Task<IAsyncEnumerable<T>>> enumerableFactory, TimeSpan restartInterval, [EnumeratorCancellation] CancellationToken cancellationToken) where T : struct
    {
        T? lastValue = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Outer loop retrieves a new enumerable/enumerator to process
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(restartInterval);

            IAsyncEnumerable<T> enumerable;
            try
            {
                enumerable = await enumerableFactory(lastValue, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The restart interval can expire while the factory is still establishing a streaming
                // connection. Start a fresh factory call just as we do when enumeration is canceled.
                continue;
            }
            var enumerator = enumerable.GetAsyncEnumerator(cts.Token);

            try
            {
                while (true)
                {
                    // Loop over the current enumerable until it is exhausted or we need to restart
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            // For some reason our inner long running enumerable has exited; break out of the inner loop to get a fresh enumerable
                            break;
                        }
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // If the restart token threw a cancellation exception, we should resume the outer loop to get a new enumerable if necessary
                        // If the main token is cancelled, we should just bubble up the exception
                        break;
                    }

                    lastValue = enumerator.Current;

                    yield return (T)lastValue;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Creates an async enumerable that wraps and periodically restarts an inner async enumeration. This is intended to keep long
    /// running watch enumerations fresh and will recreate the watch enumeration on a set interval or if the inner enumerable terminates
    /// unexpectedly. The goal is to ensure that we keep the wrapped enumeration active until the main token is cancelled.
    /// </summary>
    /// <typeparam name="T">The type the enumerable iterates over</typeparam>
    /// <param name="enumerableFactory">Factory method that takes the last iterrated value (if one exists) and a <see cref="CancellationToken"/> and returns a fresh <see cref="IAsyncEnumerable{T}"/> to enumerate over</param>
    /// <param name="restartInterval">How often should we get a new enumerable from the factory</param>
    /// <param name="cancellationToken">Stop all enumeration once this is cancelled</param>
    /// <returns>An <see cref="IAsyncEnumerable{T}"/> of items returned by the inner iterables</returns>
    public static async IAsyncEnumerable<T> CreateAsync<T>(Func<T?, CancellationToken, Task<IAsyncEnumerable<T>>> enumerableFactory, TimeSpan restartInterval, [EnumeratorCancellation] CancellationToken cancellationToken) where T : class?
    {
        T? lastValue = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Outer loop retrieves a new enumerable/enumerator to process
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(restartInterval);

            IAsyncEnumerable<T> enumerable;
            try
            {
                enumerable = await enumerableFactory(lastValue, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The restart interval can expire while the factory is still establishing a streaming
                // connection. Start a fresh factory call just as we do when enumeration is canceled.
                continue;
            }
            var enumerator = enumerable.GetAsyncEnumerator(cts.Token);

            try
            {
                while (true)
                {
                    // Loop over the current enumerable until it is exhausted or we need to restart
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            // For some reason our inner long running enumerable has exited; break out of the inner loop to get a fresh enumerable
                            break;
                        }
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token && cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // If the restart token threw a cancellation exception, we should resume the outer loop to get a new enumerable if necessary
                        // If the main token is cancelled, we should just bubble up the exception
                        break;
                    }

                    lastValue = enumerator.Current;

                    yield return (T)lastValue;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
