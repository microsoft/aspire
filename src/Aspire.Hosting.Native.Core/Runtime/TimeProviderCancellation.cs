// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Runtime;

/// <summary>Owns a provider-backed timeout and its optional parent cancellation link.</summary>
internal sealed class TimeProviderCancellation : IDisposable
{
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _linked;

    public TimeProviderCancellation(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        _timeout = new CancellationTokenSource(delay, timeProvider);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token, cancellationToken);
    }

    public CancellationToken Token => _linked.Token;
    public bool IsCancellationRequested => _linked.IsCancellationRequested;
    public Task CancelAsync() => _linked.CancelAsync();
    public void StopTimeout() => _timeout.Dispose();
    public void Dispose()
    {
        _linked.Dispose();
        _timeout.Dispose();
    }
}
