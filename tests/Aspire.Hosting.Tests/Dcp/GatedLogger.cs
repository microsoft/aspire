// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Dcp;

internal sealed class GatedLogger<T>(string messageFragment) : ILogger<T>
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockCount;

    public Task Blocked => _blocked.Task;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter(state, exception).Contains(messageFragment, StringComparison.Ordinal) &&
            Interlocked.CompareExchange(ref _blockCount, 1, 0) == 0)
        {
            _blocked.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }
    }

    public void Release()
    {
        _release.TrySetResult();
    }
}
