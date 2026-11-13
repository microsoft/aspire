// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Permits one enumerator acquisition without advancing the deferred source.
/// </summary>
/// <param name="events">The deferred event source to expose for a single enumeration.</param>
internal sealed class ProcessEventStream(IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> events)
    : IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>>
{
    private int _enumerationClaimed;

    /// <summary>
    /// Claims the stream's only enumeration without advancing the source.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token passed to the source enumerator.</param>
    /// <returns>The source enumerator.</returns>
    /// <exception cref="InvalidOperationException">An enumerator has already been acquired, even if it was never advanced.</exception>
    public IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerationClaimed, 1) != 0)
        {
            throw new InvalidOperationException("The process event stream can only be consumed once.");
        }

        return events.GetAsyncEnumerator(cancellationToken);
    }
}
