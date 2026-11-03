// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Adjusts wall time independently of FakeTimeProvider's monotonic timers.</summary>
internal sealed class AdjustableFakeTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _clock = new();
    public TimeSpan UtcOffset { get; set; }
    public Action<TimeSpan>? TimerCreated { get; set; }
    public void Advance(TimeSpan delta) => _clock.Advance(delta);
    public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow() + UtcOffset;
    public override long GetTimestamp() => _clock.GetTimestamp();
    public override long TimestampFrequency => _clock.TimestampFrequency;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _clock.CreateTimer(callback, state, dueTime, period);
        TimerCreated?.Invoke(dueTime);

        return timer;
    }
}
