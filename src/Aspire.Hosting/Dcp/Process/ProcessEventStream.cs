// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Permits one enumerator acquisition without advancing the deferred source.
/// </summary>
internal sealed class ProcessEventStream(IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> events)
    : IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>>
{
    private int _enumerationClaimed;

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
