// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAsyncDisposable(Func<ValueTask> dispose) : IAsyncDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);

        return dispose();
    }
}
