// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire;

internal static class ChannelExtensions
{
    /// <summary>
    /// Reads batches, grabbing all available messages and returning them as one batch before yielding.
    /// This can give a better downstream experience, as there's less per-item overhead.
    /// </summary>
    /// <remarks>
    /// This sequence adopts the lifetime of <paramref name="channel"/>.
    /// Callers are required to either use a channel that will complete, or to pass a cancellation
    /// token which will also cancel the sequence returned by this method.
    /// </remarks>
    /// <typeparam name="T">The type of items in the channel and returned batch.</typeparam>
    /// <param name="channel">The channel to read values from.</param>
    /// <param name="minReadInterval">The minimum read interval. The enumerable will wait this long before returning the next available result.</param>
    /// <param name="cancellationToken">A token that signals a loss of interest in the operation.</param>
    /// <returns></returns>
    public static async IAsyncEnumerable<IReadOnlyList<T>> GetBatchesAsync<T>(
        this Channel<T> channel,
        TimeSpan? minReadInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DateTime? lastRead = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            List<T>? batch = null;
            // Wait until there's something to read, or the channel closes.
            if (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (minReadInterval != null && lastRead != null)
                {
                    var s = lastRead.Value.Add(minReadInterval.Value) - DateTime.UtcNow;
                    if (s > TimeSpan.Zero)
                    {
                        await Task.Delay(s, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Read everything in the channel into a batch.
                while (!cancellationToken.IsCancellationRequested && channel.Reader.TryRead(out var log))
                {
                    batch ??= [];
                    batch.Add(log);
                }

                if (!cancellationToken.IsCancellationRequested && batch is not null)
                {
                    lastRead = DateTime.UtcNow;
                    yield return batch;
                }
            }
            else
            {
                // The channel completed, so there'll be no further data.
                break;
            }
        }
    }

    /// <summary>
    /// Reads up to <paramref name="maxBatchSize"/> values from an async sequence before yielding each batch.
    /// </summary>
    /// <typeparam name="T">The type of items in the source and returned batch.</typeparam>
    /// <param name="source">The async sequence to read values from.</param>
    /// <param name="maxBatchSize">The maximum number of values to include in each batch.</param>
    /// <param name="cancellationToken">A token that signals a loss of interest in the operation.</param>
    /// <returns>An async sequence of batches.</returns>
    public static async IAsyncEnumerable<T[]> GetBatchesAsync<T>(
        this IAsyncEnumerable<T> source,
        int maxBatchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

        List<T>? batch = null;

        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            batch ??= new List<T>(maxBatchSize);
            batch.Add(item);

            if (batch.Count == maxBatchSize)
            {
                yield return batch.ToArray();
                batch.Clear();
            }
        }

        if (batch is { Count: > 0 })
        {
            yield return batch.ToArray();
        }
    }
}
