// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class SignalingFakeTimeProvider(Func<TimeSpan, bool> shouldSignalTimer) : FakeTimeProvider
{
    public SignalingFakeTimeProvider(TimeSpan signaledDueTime)
        : this(dueTime => dueTime == signaledDueTime)
    {
    }

    public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? TimerCreatedCallback { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (shouldSignalTimer(dueTime))
        {
            TimerCreatedCallback?.Invoke();
            TimerCreated.TrySetResult();
        }

        return timer;
    }
}
