// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aspire.Hosting.Utils;

internal static class AsyncEnumerableUtils
{
    /// <summary>
    /// Reads items from a <see cref="ChannelReader{T}"/> until the channel completes or the token is
    /// cancelled, yielding each item. Cancellation causes the sequence to complete gracefully rather
    /// than throwing, making it safe to use inside iterator methods that have <c>finally</c> blocks.
    /// </summary>
    public static async IAsyncEnumerable<T> ReadChannelUntilCancelledAsync<T>(
        ChannelReader<T> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool hasMore;
            try
            {
                hasMore = await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (!hasMore)
            {
                yield break;
            }

            while (reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Iterates an <see cref="IAsyncEnumerable{T}"/> until it completes or the token is cancelled,
    /// yielding each item. Cancellation causes the sequence to complete gracefully rather than
    /// throwing, making it safe to use inside iterator methods that have <c>finally</c> blocks.
    /// </summary>
    public static async IAsyncEnumerable<T> ReadEnumerableUntilCancelledAsync<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
