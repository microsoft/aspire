// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Interaction;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestConsoleInput : IConsoleInput
{
    private readonly ConcurrentQueue<ConsoleKeyInfo> _keys = new();
    private int _readCount;
    private int _keyAvailableChecks;

    public bool IsInputRedirected { get; set; }
    public bool KeyAvailable
    {
        get
        {
            Interlocked.Increment(ref _keyAvailableChecks);
            return !_keys.IsEmpty;
        }
    }

    public int ReadCount => Volatile.Read(ref _readCount);
    public int KeyAvailableChecks => Volatile.Read(ref _keyAvailableChecks);
    public ConcurrentQueue<bool> InterceptedReads { get; } = new();
    public Action? ReadKeyCallback { get; set; }

    public void Enqueue(ConsoleKeyInfo key) => _keys.Enqueue(key);

    public void PressReload() => Enqueue(new ConsoleKeyInfo('\u0012', ConsoleKey.R, shift: false, alt: false, control: true));

    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        if (!_keys.TryDequeue(out var key))
        {
            throw new InvalidOperationException("No console key is available.");
        }
        InterceptedReads.Enqueue(intercept);
        Interlocked.Increment(ref _readCount);
        ReadKeyCallback?.Invoke();
        return key;
    }

    public async Task WaitForReadCountAsync(int count, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (ReadCount < count)
        {
            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }
}
