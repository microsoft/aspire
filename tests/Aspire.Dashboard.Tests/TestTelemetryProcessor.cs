// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry;

namespace Aspire.Dashboard.Tests;

internal sealed class TestTelemetryProcessor<T> : BaseProcessor<T> where T : class
{
    private int _disposeCount;

    public Func<int, bool> ForceFlushCallback { get; set; } = static _ => true;
    public Func<int, bool> ShutdownCallback { get; set; } = static _ => true;
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    protected override bool OnForceFlush(int timeoutMilliseconds) => ForceFlushCallback(timeoutMilliseconds);

    protected override bool OnShutdown(int timeoutMilliseconds) => ShutdownCallback(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Increment(ref _disposeCount);
        }
        base.Dispose(disposing);
    }
}
