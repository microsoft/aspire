// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Allows tests to adjust UTC independently of monotonic elapsed time.
/// </summary>
internal sealed class AdjustableTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public TimeSpan Elapsed { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override long GetTimestamp() => Elapsed.Ticks;
}
