// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.DevTunnels.Tests;

internal sealed class TestDevTunnelTimeProvider() : FakeTimeProvider(DateTimeOffset.UtcNow)
{
    public Channel<TimeSpan> ScheduledDelays { get; } = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        ScheduledDelays.Writer.TryWrite(dueTime);
        return timer;
    }
}
